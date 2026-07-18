using DeviceSync.Application;
using Xunit;

namespace DeviceSync.Application.Tests;

public sealed class IncomingFileAutomationTests
{
    [Fact]
    public async Task UntrustedOrSpoofedSender_IsRejectedWithoutPrompt()
    {
        var interactive = new FakeInteractiveDecision();
        var service = CreateService(interactive, trusted: false, new(IncomingFileReceiveMode.AutoUpToLimit), networkTrusted: true);

        var decision = await service.DecideAsync(Transfer("unknown", "photo.jpg", "image/jpeg", 100));

        Assert.False(decision.Accepted);
        Assert.Equal("TRUST_REQUIRED", decision.RejectionCode);
        Assert.Equal(0, interactive.CallCount);
    }

    [Fact]
    public async Task TrustedSafeFileUnderLimit_IsAutomaticallyAccepted()
    {
        var interactive = new FakeInteractiveDecision();
        var service = CreateService(interactive, trusted: true,
            new(IncomingFileReceiveMode.AutoKnownTypes, 5 * 1024 * 1024, TrustedNetworkOnly: true),
            networkTrusted: true);

        var decision = await service.DecideAsync(Transfer("phone", "photo.jpg", "image/jpeg", 1024));

        Assert.True(decision.Accepted);
        Assert.Equal(0, interactive.CallCount);
    }

    [Theory]
    [InlineData("payload.exe", "application/octet-stream")]
    [InlineData("script.txt", "application/x-sh")]
    [InlineData("report.docm", "application/vnd.ms-word.document.macroEnabled.12")]
    public async Task DangerousExtensionOrMime_IsBlocked(string fileName, string mimeType)
    {
        var interactive = new FakeInteractiveDecision();
        var service = CreateService(interactive, trusted: true, new(IncomingFileReceiveMode.AutoUpToLimit), networkTrusted: true);

        var decision = await service.DecideAsync(Transfer("phone", fileName, mimeType, 100));

        Assert.False(decision.Accepted);
        Assert.Equal("CONTENT_TYPE_BLOCKED", decision.RejectionCode);
        Assert.Equal(0, interactive.CallCount);
    }

    [Fact]
    public async Task OversizedOrUnapprovedNetwork_FallsBackToExplicitPrompt()
    {
        var interactive = new FakeInteractiveDecision(accepted: true);
        var policy = new IncomingFileAutomationPolicy(IncomingFileReceiveMode.AutoUpToLimit, 1024, TrustedNetworkOnly: true);
        var service = CreateService(interactive, trusted: true, policy, networkTrusted: false);

        var decision = await service.DecideAsync(Transfer("phone", "large.bin", "application/octet-stream", 2048));

        Assert.True(decision.Accepted);
        Assert.Equal(1, interactive.CallCount);
    }

    [Fact]
    public async Task RevokedTrustOrNeverMode_BlocksAnActiveTransfer()
    {
        var repository = new FakeTrustedRepository(Trusted("phone"));
        var store = new FakePolicyStore(new(IncomingFileReceiveMode.AutoUpToLimit));
        var service = new SecureIncomingFileDecisionService(new FakeInteractiveDecision(), repository, store, new FakeNetwork(true));
        Assert.True(await service.IsTransferAllowedAsync("phone"));

        store.Policy = store.Policy with { Mode = IncomingFileReceiveMode.Never };
        Assert.False(await service.IsTransferAllowedAsync("phone"));
        store.Policy = store.Policy with { Mode = IncomingFileReceiveMode.AlwaysAsk };
        repository.Device = repository.Device! with { TrustStatus = TrustStatuses.Revoked, RevokedAtUtc = DateTimeOffset.UtcNow };
        Assert.False(await service.IsTransferAllowedAsync("phone"));
    }

    private static SecureIncomingFileDecisionService CreateService(
        FakeInteractiveDecision interactive,
        bool trusted,
        IncomingFileAutomationPolicy policy,
        bool networkTrusted)
    {
        var repository = new FakeTrustedRepository(trusted ? Trusted("phone") : null);
        return new(interactive, repository, new FakePolicyStore(policy), new FakeNetwork(networkTrusted));
    }

    private static IncomingFileTransfer Transfer(string sender, string fileName, string mimeType, long size) => new()
    {
        TransferId = Guid.NewGuid().ToString(), SenderDeviceId = sender, FileName = fileName,
        SafeFileName = fileName, MimeType = mimeType, SizeBytes = size, ExpectedSha256 = "hash",
        StartedAtUtc = DateTimeOffset.UtcNow,
    };

    private static TrustedDevice Trusted(string deviceId) => new()
    {
        DeviceId = deviceId, DeviceName = "Phone", IdentityPublicKey = "key", IdentityFingerprint = "fingerprint",
        PairedAtUtc = DateTimeOffset.UtcNow, TrustStatus = TrustStatuses.Active,
    };

    private sealed class FakeInteractiveDecision(bool accepted = false) : IIncomingFileTransferDecisionService
    {
        public int CallCount { get; private set; }
        public Task<IncomingFileTransferDecision> DecideAsync(IncomingFileTransfer transfer, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new IncomingFileTransferDecision(accepted));
        }
    }

    private sealed class FakePolicyStore(IncomingFileAutomationPolicy policy) : IIncomingFilePolicyStore
    {
        public bool AutomaticReceiveEnabled { get; set; } = true;
        public IncomingFileAutomationPolicy Policy { get; set; } = policy;
        public IncomingFileAutomationPolicy GetPolicy(string deviceId) => Policy;
    }

    private sealed class FakeNetwork(bool trusted) : IIncomingFileNetworkContext
    {
        public bool IsCurrentNetworkTrusted { get; } = trusted;
    }

    private sealed class FakeTrustedRepository(TrustedDevice? device) : ITrustedDeviceRepository
    {
        public TrustedDevice? Device { get; set; } = device;
        public Task<IReadOnlyList<TrustedDevice>> GetTrustedDevicesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TrustedDevice>>(Device is null ? [] : [Device]);
        public Task<TrustedDevice?> GetTrustedDeviceAsync(string deviceId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Device?.DeviceId == deviceId ? Device : null);
        public Task SaveTrustedDeviceAsync(TrustedDevice device, CancellationToken cancellationToken = default) { Device = device; return Task.CompletedTask; }
        public Task ActivateTrustedDeviceAsync(string deviceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateLastVerifiedAtAsync(string deviceId, DateTimeOffset timestamp, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RevokeAsync(string deviceId, DateTimeOffset timestamp, CancellationToken cancellationToken = default)
        {
            if (Device is not null) Device = Device with { TrustStatus = TrustStatuses.Revoked, RevokedAtUtc = timestamp };
            return Task.CompletedTask;
        }
        public Task DeleteAsync(string deviceId, CancellationToken cancellationToken = default) { Device = null; return Task.CompletedTask; }
    }
}
