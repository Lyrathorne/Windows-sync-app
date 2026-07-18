using DeviceSync.Protocol;

namespace DeviceSync.Application;

public enum IncomingFileReceiveMode
{
    AlwaysAsk,
    AutoUpToLimit,
    AutoKnownTypes,
    Never,
}

public sealed record IncomingFileAutomationPolicy(
    IncomingFileReceiveMode Mode = IncomingFileReceiveMode.AlwaysAsk,
    long AutomaticLimitBytes = 25L * 1024 * 1024,
    bool TrustedNetworkOnly = true);

public interface IIncomingFilePolicyStore
{
    bool AutomaticReceiveEnabled { get; }
    IncomingFileAutomationPolicy GetPolicy(string deviceId);
}

public interface IIncomingFileNetworkContext
{
    bool IsCurrentNetworkTrusted { get; }
}

public interface IIncomingFileTransferGuard
{
    Task<bool> IsTransferAllowedAsync(string senderDeviceId, CancellationToken cancellationToken = default);
}

public sealed class SecureIncomingFileDecisionService(
    IIncomingFileTransferDecisionService interactiveDecisionService,
    ITrustedDeviceRepository trustedDevices,
    IIncomingFilePolicyStore policies,
    IIncomingFileNetworkContext networkContext)
    : IIncomingFileTransferDecisionService, IIncomingFileTransferGuard
{
    public async Task<IncomingFileTransferDecision> DecideAsync(
        IncomingFileTransfer transfer,
        CancellationToken cancellationToken = default)
    {
        if (!await IsTrustedAsync(transfer.SenderDeviceId, cancellationToken).ConfigureAwait(false))
            return Denied("TRUST_REQUIRED");

        var safetyError = IncomingFileSafetyClassifier.Validate(transfer.SafeFileName, transfer.MimeType);
        if (safetyError is not null)
            return Denied(safetyError);

        var policy = policies.GetPolicy(transfer.SenderDeviceId);
        if (policy.Mode == IncomingFileReceiveMode.Never)
            return Denied("DEVICE_PERMISSION_DENIED");
        if (!policies.AutomaticReceiveEnabled)
            return await interactiveDecisionService.DecideAsync(transfer, cancellationToken).ConfigureAwait(false);
        if (policy.Mode == IncomingFileReceiveMode.AlwaysAsk ||
            policy.TrustedNetworkOnly && !networkContext.IsCurrentNetworkTrusted)
            return await interactiveDecisionService.DecideAsync(transfer, cancellationToken).ConfigureAwait(false);

        var underLimit = transfer.SizeBytes <= Math.Clamp(policy.AutomaticLimitBytes, 0, IncomingFileTransferManager.MaximumFileSize);
        var knownType = IncomingFileSafetyClassifier.IsKnownSafeType(transfer.SafeFileName, transfer.MimeType);
        var autoAccept = policy.Mode switch
        {
            IncomingFileReceiveMode.AutoUpToLimit => underLimit,
            IncomingFileReceiveMode.AutoKnownTypes => underLimit && knownType,
            _ => false,
        };
        return autoAccept
            ? new IncomingFileTransferDecision(true)
            : await interactiveDecisionService.DecideAsync(transfer, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> IsTransferAllowedAsync(string senderDeviceId, CancellationToken cancellationToken = default)
    {
        if (!await IsTrustedAsync(senderDeviceId, cancellationToken).ConfigureAwait(false)) return false;
        return policies.GetPolicy(senderDeviceId).Mode != IncomingFileReceiveMode.Never;
    }

    private async Task<bool> IsTrustedAsync(string deviceId, CancellationToken cancellationToken)
    {
        var device = await trustedDevices.GetTrustedDeviceAsync(deviceId, cancellationToken).ConfigureAwait(false);
        return device is { TrustStatus: TrustStatuses.Active, RevokedAtUtc: null };
    }

    private static IncomingFileTransferDecision Denied(string code) =>
        new(false, RejectionCode: code);
}

public static class IncomingFileSafetyClassifier
{
    private static readonly HashSet<string> BlockedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ade", ".adp", ".apk", ".appx", ".bat", ".chm", ".cmd", ".com", ".cpl", ".dll",
        ".exe", ".hta", ".inf", ".ins", ".iso", ".jar", ".js", ".jse", ".lnk", ".mde",
        ".msc", ".msi", ".msp", ".mst", ".pif", ".ps1", ".reg", ".scr", ".sct", ".sh",
        ".url", ".vb", ".vbe", ".vbs", ".ws", ".wsc", ".wsf", ".wsh",
        ".docm", ".dotm", ".xlsm", ".xltm", ".pptm", ".potm", ".ppam", ".ppsm",
    };

    private static readonly IReadOnlyDictionary<string, string[]> KnownSafeTypes =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            [".txt"] = ["text/plain"], [".csv"] = ["text/csv", "text/plain"],
            [".json"] = ["application/json", "text/json", "text/plain"],
            [".md"] = ["text/markdown", "text/plain"], [".pdf"] = ["application/pdf"],
            [".jpg"] = ["image/jpeg"], [".jpeg"] = ["image/jpeg"], [".png"] = ["image/png"],
            [".gif"] = ["image/gif"], [".webp"] = ["image/webp"], [".bmp"] = ["image/bmp"],
            [".heic"] = ["image/heic", "image/heif"], [".svg"] = ["image/svg+xml"],
            [".docx"] = ["application/vnd.openxmlformats-officedocument.wordprocessingml.document"],
            [".xlsx"] = ["application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"],
            [".pptx"] = ["application/vnd.openxmlformats-officedocument.presentationml.presentation"],
        };

    public static string? Validate(string fileName, string mimeType)
    {
        var extension = Path.GetExtension(fileName);
        if (BlockedExtensions.Contains(extension)) return "CONTENT_TYPE_BLOCKED";
        if (mimeType.Equals("application/x-msdownload", StringComparison.OrdinalIgnoreCase) ||
            mimeType.Equals("application/x-executable", StringComparison.OrdinalIgnoreCase) ||
            mimeType.Equals("application/x-sh", StringComparison.OrdinalIgnoreCase))
            return "CONTENT_TYPE_BLOCKED";
        return null;
    }

    public static bool IsKnownSafeType(string fileName, string mimeType)
    {
        var extension = Path.GetExtension(fileName);
        return KnownSafeTypes.TryGetValue(extension, out var allowed) &&
            allowed.Contains(mimeType, StringComparer.OrdinalIgnoreCase);
    }
}
