using DeviceSync.Application;
using DeviceSync.Protocol;
using Xunit;

namespace DeviceSync.Application.Tests;

public sealed class DiscoveryPublisherTests
{
    [Fact]
    public async Task FakePublisher_StartPublishesExpectedService()
    {
        var publisher = new FakePublisher();
        var service = Service();

        await publisher.StartAsync(service);

        Assert.Equal(PublisherState.Published, publisher.State);
        Assert.Equal("_devicesync._tcp", publisher.CurrentService?.ServiceType);
        Assert.Equal(54321, publisher.CurrentService?.Port);
        Assert.Equal("windows-fixed", publisher.CurrentService?.TxtRecords["deviceId"]);
        Assert.Equal("1", publisher.CurrentService?.TxtRecords["protocolMin"]);
        Assert.Equal("1", publisher.CurrentService?.TxtRecords["protocolMax"]);
    }

    [Fact]
    public async Task FakePublisher_RepeatedStartDoesNotCreateSecondPublication()
    {
        var publisher = new FakePublisher();
        var service = Service();

        await publisher.StartAsync(service);
        await publisher.StartAsync(service);

        Assert.Equal(1, publisher.StartCount);
    }

    [Fact]
    public async Task FakePublisher_StopIsIdempotent()
    {
        var publisher = new FakePublisher();

        await publisher.StopAsync();
        await publisher.StopAsync();

        Assert.Equal(PublisherState.Stopped, publisher.State);
    }

    [Fact]
    public async Task FakePublisher_PortChangeRepublishes()
    {
        var publisher = new FakePublisher();

        await publisher.StartAsync(Service(port: 54321));
        await publisher.StartAsync(Service(port: 54322));

        Assert.Equal(2, publisher.StartCount);
        Assert.Equal(54322, publisher.CurrentService?.Port);
    }

    private static PublishedService Service(int port = 54321) => new()
    {
        InstanceName = "Gleb-PC",
        ServiceType = "_devicesync._tcp",
        Port = port,
        TxtRecords = new Dictionary<string, string>
        {
            ["deviceId"] = "windows-fixed",
            ["deviceName"] = "Gleb-PC",
            ["deviceType"] = "windows",
            ["protocolMin"] = ProtocolConstants.ProtocolVersion.ToString(),
            ["protocolMax"] = ProtocolConstants.ProtocolVersion.ToString(),
            ["pairingAvailable"] = "false",
        },
    };
}

internal sealed class FakePublisher : IServiceDiscoveryPublisher
{
    public PublisherState State { get; private set; } = PublisherState.Stopped;
    public PublishedService? CurrentService { get; private set; }
    public string? LastError { get; private set; }
    public DateTimeOffset? LastPublishedAtUtc { get; private set; }
    public int StartCount { get; private set; }
    public event EventHandler<PublisherStateChangedEventArgs>? StateChanged;

    public Task StartAsync(PublishedService service, CancellationToken cancellationToken = default)
    {
        if (State == PublisherState.Published && CurrentService == service) return Task.CompletedTask;
        StartCount++;
        CurrentService = service;
        State = PublisherState.Published;
        LastPublishedAtUtc = DateTimeOffset.UtcNow;
        StateChanged?.Invoke(this, new PublisherStateChangedEventArgs(State, service, LastError, LastPublishedAtUtc));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        CurrentService = null;
        State = PublisherState.Stopped;
        StateChanged?.Invoke(this, new PublisherStateChangedEventArgs(State, null, LastError, LastPublishedAtUtc));
        return Task.CompletedTask;
    }
}
