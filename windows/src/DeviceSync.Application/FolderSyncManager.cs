using DeviceSync.Protocol;

namespace DeviceSync.Application;

public interface IFolderManifestBuilder
{
    Task<FolderManifestPayload> BuildAsync(string rootPath, string syncId, CancellationToken cancellationToken = default);
}

public interface IFolderSyncRootStore
{
    Task<string?> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(string rootPath, CancellationToken cancellationToken = default);
}

public static class FolderConflictResolutions
{
    public const string KeepWindows = "keep_windows";
    public const string KeepAndroid = "keep_android";
    public const string KeepBoth = "keep_both";
    public static readonly IReadOnlySet<string> All = new HashSet<string>([KeepWindows, KeepAndroid, KeepBoth], StringComparer.Ordinal);
}

public static class FolderSyncPlanner
{
    public static FolderPlanPayload Build(FolderManifestPayload local, FolderManifestPayload remote)
    {
        if (local.SyncId != remote.SyncId) throw new InvalidDataException("Folder manifests belong to different sync sessions.");
        var localEntries = local.Entries.ToDictionary(entry => Normalize(entry.RelativePath), StringComparer.Ordinal);
        var remoteEntries = remote.Entries.ToDictionary(entry => Normalize(entry.RelativePath), StringComparer.Ordinal);
        var paths = localEntries.Keys.Concat(remoteEntries.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        var operations = new List<FolderPlanOperationPayload>();
        foreach (var path in paths)
        {
            var hasLocal = localEntries.TryGetValue(path, out var localEntry);
            var hasRemote = remoteEntries.TryGetValue(path, out var remoteEntry);
            if (hasLocal && !hasRemote) operations.Add(new() { RelativePath = path, Action = "upload" });
            else if (!hasLocal && hasRemote) operations.Add(new() { RelativePath = path, Action = "download" });
            else if (localEntry!.Sha256 != remoteEntry!.Sha256)
                operations.Add(new() { RelativePath = path, Action = "conflict", Reason = "both_modified" });
        }
        return new FolderPlanPayload { SyncId = local.SyncId, Operations = operations };
    }

    public static string Normalize(string relativePath)
    {
        var value = relativePath.Replace('\\', '/');
        var segments = value.Split('/');
        if (Path.IsPathRooted(relativePath) || segments.Any(segment => segment is "" or "." or ".." ||
            segment.Contains(':') || segment.Contains('\0')))
            throw new InvalidDataException("Folder manifest contains an unsafe relative path.");
        return string.Join('/', segments);
    }
}

public sealed class FolderSyncManager : IFolderFileTransferAuthorizer
{
    private readonly IFolderManifestBuilder _builder;
    private readonly IFeatureMessageTransport _transport;
    private readonly IFolderSyncRootStore _rootStore;
    private readonly OutgoingTransferQueue _outgoing;
    private readonly object _gate = new();
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private FolderManifestPayload? _pendingRemote;
    private string? _rootPath;

    public FolderSyncManager(IFolderManifestBuilder builder, IFeatureMessageTransport transport,
        IFolderSyncRootStore rootStore, OutgoingTransferQueue outgoing)
    {
        _builder = builder;
        _transport = transport;
        _rootStore = rootStore;
        _outgoing = outgoing;
        _transport.MessageReceived += OnMessageReceived;
    }

    public event Action<FolderPlanPayload>? PlanCreated;
    public event Action<string>? RootSelectionRequired;
    public event Action<string>? StatusChanged;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
        => _rootPath = await _rootStore.LoadAsync(cancellationToken).ConfigureAwait(false);

    public async Task<string> StartAsync(string rootPath, CancellationToken cancellationToken = default)
    {
        _rootPath = Path.GetFullPath(rootPath);
        await _rootStore.SaveAsync(_rootPath, cancellationToken).ConfigureAwait(false);
        FolderManifestPayload? pending;
        lock (_gate) { pending = _pendingRemote; _pendingRemote = null; }
        if (pending is not null)
        {
            await RespondToManifestAsync(pending, cancellationToken).ConfigureAwait(false);
            return pending.SyncId;
        }

        var syncId = Guid.NewGuid().ToString();
        var manifest = await _builder.BuildAsync(_rootPath, syncId, cancellationToken).ConfigureAwait(false);
        lock (_gate) _sessions[syncId] = new Session(syncId, _rootPath, true) { LocalManifest = manifest };
        await SendAsync(ProtocolMessageTypes.FolderManifest, manifest, cancellationToken).ConfigureAwait(false);
        StatusChanged?.Invoke("Manifest sent. Waiting for the Android folder manifest.");
        return syncId;
    }

    public async Task ApproveAsync(string syncId, IReadOnlyDictionary<string, string> conflictResolutions,
        CancellationToken cancellationToken = default)
    {
        Session session;
        lock (_gate) session = GetSession(syncId);
        if (session.Plan is null) throw new InvalidOperationException("The folder plan has not arrived.");
        var conflicts = session.Plan.Operations.Where(operation => operation.Action == "conflict")
            .Select(operation => FolderSyncPlanner.Normalize(operation.RelativePath)).Order(StringComparer.Ordinal).ToArray();
        if (conflicts.Any(path => !conflictResolutions.TryGetValue(path, out var value) || !FolderConflictResolutions.All.Contains(value)) ||
            conflictResolutions.Keys.Select(FolderSyncPlanner.Normalize).Except(conflicts, StringComparer.Ordinal).Any())
            throw new InvalidDataException("Every conflict must have exactly one valid resolution.");
        var approval = new FolderPlanApprovedPayload
        {
            SyncId = syncId,
            ConflictResolutions = conflicts.Select(path => new FolderConflictResolutionPayload
            {
                RelativePath = path,
                Resolution = conflictResolutions[path],
            }).ToArray(),
        };
        lock (_gate) session.LocalApproval = approval;
        await SendAsync(ProtocolMessageTypes.FolderPlanApproved, approval, cancellationToken).ConfigureAwait(false);
        StatusChanged?.Invoke("Local plan approved. Waiting for Android approval.");
        await TryExecuteAsync(session, cancellationToken).ConfigureAwait(false);
    }

    public IncomingFileTransferDecision? Authorize(FileOfferPayload offer)
    {
        if (offer.FolderSyncId is null || offer.RelativePath is null) return null;
        var relative = FolderSyncPlanner.Normalize(offer.RelativePath);
        lock (_gate)
        {
            if (!_sessions.TryGetValue(offer.FolderSyncId, out var session) || !session.ExecutionStarted ||
                !session.ExpectedIncoming.TryGetValue(relative, out var expected) || expected.Consumed ||
                expected.Sha256 != offer.Sha256 || expected.SizeBytes != offer.SizeBytes || expected.ConflictCopy != offer.ConflictCopy)
                return new IncomingFileTransferDecision(false, RejectionCode: "folder_transfer_not_authorized");
            expected.Consumed = true;
            var directoryPart = Path.GetDirectoryName(relative.Replace('/', Path.DirectorySeparatorChar));
            var destinationDirectory = directoryPart is null ? session.RootPath : Path.Combine(session.RootPath, directoryPart);
            var fileName = Path.GetFileName(relative);
            if (offer.ConflictCopy) fileName = AddOriginSuffix(fileName, "Android");
            return new IncomingFileTransferDecision(true, destinationDirectory, DestinationFileName: fileName,
                ReplaceExisting: expected.ReplaceExisting && !offer.ConflictCopy);
        }
    }

    private async void OnMessageReceived(object? sender, FeatureMessageEventArgs args)
    {
        try
        {
            switch (args.Type)
            {
                case ProtocolMessageTypes.FolderManifest:
                    await HandleManifestAsync(ProtocolSerializer.DecodePayload<FolderManifestPayload>(args.Payload)).ConfigureAwait(false);
                    break;
                case ProtocolMessageTypes.FolderPlan:
                    HandlePlan(ProtocolSerializer.DecodePayload<FolderPlanPayload>(args.Payload));
                    break;
                case ProtocolMessageTypes.FolderPlanApproved:
                    await HandleApprovalAsync(ProtocolSerializer.DecodePayload<FolderPlanApprovedPayload>(args.Payload)).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception error) { StatusChanged?.Invoke($"Folder sync failed: {error.Message}"); }
    }

    private async Task HandleManifestAsync(FolderManifestPayload remote)
    {
        Session? session;
        lock (_gate) _sessions.TryGetValue(remote.SyncId, out session);
        if (session is null)
        {
            if (string.IsNullOrWhiteSpace(_rootPath))
            {
                lock (_gate) _pendingRemote = remote;
                RootSelectionRequired?.Invoke(remote.SyncId);
                StatusChanged?.Invoke("Android requested folder sync. Select the matching Windows folder.");
                return;
            }
            await RespondToManifestAsync(remote, CancellationToken.None).ConfigureAwait(false);
            return;
        }
        if (!session.InitiatedLocally) return;
        session.RemoteManifest = remote;
        session.Plan = FolderSyncPlanner.Build(session.LocalManifest!, remote);
        PlanCreated?.Invoke(session.Plan);
        await SendAsync(ProtocolMessageTypes.FolderPlan, session.Plan, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task RespondToManifestAsync(FolderManifestPayload remote, CancellationToken cancellationToken)
    {
        var root = _rootPath ?? throw new InvalidOperationException("Select a Windows sync folder first.");
        var local = await _builder.BuildAsync(root, remote.SyncId, cancellationToken).ConfigureAwait(false);
        lock (_gate) _sessions[remote.SyncId] = new Session(remote.SyncId, root, false) { LocalManifest = local, RemoteManifest = remote };
        await SendAsync(ProtocolMessageTypes.FolderManifest, local, cancellationToken).ConfigureAwait(false);
        StatusChanged?.Invoke("Windows manifest sent. Waiting for the folder plan.");
    }

    private void HandlePlan(FolderPlanPayload plan)
    {
        lock (_gate)
        {
            var session = GetSession(plan.SyncId);
            if (session.InitiatedLocally) throw new InvalidDataException("Only the initiating device may create a folder plan.");
            foreach (var operation in plan.Operations) FolderSyncPlanner.Normalize(operation.RelativePath);
            var expected = FolderSyncPlanner.Build(session.RemoteManifest!, session.LocalManifest!);
            if (!expected.Operations.SequenceEqual(plan.Operations))
                throw new InvalidDataException("The received folder plan does not match the two manifests.");
            session.Plan = plan;
        }
        PlanCreated?.Invoke(plan);
    }

    private async Task HandleApprovalAsync(FolderPlanApprovedPayload approval)
    {
        Session session;
        lock (_gate)
        {
            session = GetSession(approval.SyncId);
            session.RemoteApproval = NormalizeApproval(approval);
        }
        StatusChanged?.Invoke("Android approved the folder plan.");
        await TryExecuteAsync(session, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task TryExecuteAsync(Session session, CancellationToken cancellationToken)
    {
        FolderPlanPayload plan;
        FolderPlanApprovedPayload approval;
        lock (_gate)
        {
            if (session.ExecutionStarted || session.Plan is null || session.LocalApproval is null || session.RemoteApproval is null) return;
            var local = NormalizeApproval(session.LocalApproval);
            var remote = NormalizeApproval(session.RemoteApproval);
            if (!ApprovalsEqual(local, remote)) throw new InvalidDataException("Windows and Android selected different conflict resolutions.");
            session.ExecutionStarted = true;
            plan = session.Plan;
            approval = local;
            RegisterIncoming(session, plan, approval);
        }

        var resolutions = approval.ConflictResolutions.ToDictionary(item => item.RelativePath, item => item.Resolution, StringComparer.Ordinal);
        foreach (var operation in plan.Operations)
        {
            var relative = FolderSyncPlanner.Normalize(operation.RelativePath);
            var send = operation.Action switch
            {
                "upload" => session.InitiatedLocally,
                "download" => !session.InitiatedLocally,
                "conflict" => resolutions[relative] is FolderConflictResolutions.KeepWindows or FolderConflictResolutions.KeepBoth,
                _ => throw new InvalidDataException($"Unknown folder action: {operation.Action}"),
            };
            if (!send) continue;
            var path = SafeCombine(session.RootPath, relative);
            var conflictCopy = operation.Action == "conflict" && resolutions[relative] == FolderConflictResolutions.KeepBoth;
            await _outgoing.EnqueueAsync(path, cancellationToken: cancellationToken,
                folder: new FolderTransferMetadata(session.SyncId, relative, conflictCopy)).ConfigureAwait(false);
        }
        StatusChanged?.Invoke("Both devices approved. Folder files were queued for verified transfer.");
    }

    private static void RegisterIncoming(Session session, FolderPlanPayload plan, FolderPlanApprovedPayload approval)
    {
        var resolutions = approval.ConflictResolutions.ToDictionary(item => item.RelativePath, item => item.Resolution, StringComparer.Ordinal);
        foreach (var operation in plan.Operations)
        {
            var relative = FolderSyncPlanner.Normalize(operation.RelativePath);
            var receive = operation.Action switch
            {
                "upload" => !session.InitiatedLocally,
                "download" => session.InitiatedLocally,
                "conflict" => resolutions[relative] is FolderConflictResolutions.KeepAndroid or FolderConflictResolutions.KeepBoth,
                _ => false,
            };
            if (!receive) continue;
            var remote = session.RemoteManifest!.Entries.Single(entry => FolderSyncPlanner.Normalize(entry.RelativePath) == relative);
            var copy = operation.Action == "conflict" && resolutions[relative] == FolderConflictResolutions.KeepBoth;
            session.ExpectedIncoming[relative] = new ExpectedIncoming(remote.Sha256, remote.SizeBytes, copy,
                ReplaceExisting: operation.Action == "conflict" && !copy);
        }
    }

    private static FolderPlanApprovedPayload NormalizeApproval(FolderPlanApprovedPayload value) => new()
    {
        SyncId = value.SyncId,
        ConflictResolutions = value.ConflictResolutions.Select(item => new FolderConflictResolutionPayload
        {
            RelativePath = FolderSyncPlanner.Normalize(item.RelativePath),
            Resolution = FolderConflictResolutions.All.Contains(item.Resolution)
                ? item.Resolution : throw new InvalidDataException("Unknown folder conflict resolution."),
        }).OrderBy(item => item.RelativePath, StringComparer.Ordinal).ToArray(),
    };

    private static bool ApprovalsEqual(FolderPlanApprovedPayload left, FolderPlanApprovedPayload right)
        => left.SyncId == right.SyncId && left.ConflictResolutions.SequenceEqual(right.ConflictResolutions);

    private static string SafeCombine(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Folder path escaped the selected root.");
        return full;
    }

    private static string AddOriginSuffix(string fileName, string origin)
        => $"{Path.GetFileNameWithoutExtension(fileName)} (from {origin}){Path.GetExtension(fileName)}";

    private Session GetSession(string syncId) => _sessions.TryGetValue(syncId, out var value)
        ? value : throw new InvalidDataException("Unknown folder sync session.");

    private Task SendAsync<T>(string type, T payload, CancellationToken cancellationToken)
        => _transport.SendAsync(type, ProtocolSerializer.PayloadToJson(payload), cancellationToken);

    private sealed class Session(string syncId, string rootPath, bool initiatedLocally)
    {
        public string SyncId { get; } = syncId;
        public string RootPath { get; } = rootPath;
        public bool InitiatedLocally { get; } = initiatedLocally;
        public FolderManifestPayload? LocalManifest { get; set; }
        public FolderManifestPayload? RemoteManifest { get; set; }
        public FolderPlanPayload? Plan { get; set; }
        public FolderPlanApprovedPayload? LocalApproval { get; set; }
        public FolderPlanApprovedPayload? RemoteApproval { get; set; }
        public bool ExecutionStarted { get; set; }
        public Dictionary<string, ExpectedIncoming> ExpectedIncoming { get; } = new(StringComparer.Ordinal);
    }

    private sealed record ExpectedIncoming(string Sha256, long SizeBytes, bool ConflictCopy, bool ReplaceExisting)
    {
        public bool Consumed { get; set; }
    }
}
