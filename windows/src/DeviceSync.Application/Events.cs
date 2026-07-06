namespace DeviceSync.Application;

public sealed class ServerStateChangedEventArgs : EventArgs
{
    public ServerStateChangedEventArgs(bool isRunning, int port, string status)
    {
        IsRunning = isRunning;
        Port = port;
        Status = status;
    }

    public bool IsRunning { get; }
    public int Port { get; }
    public string Status { get; }
}

public sealed class DeviceSessionChangedEventArgs : EventArgs
{
    public DeviceSessionChangedEventArgs(DeviceSessionInfo? session)
    {
        Session = session;
    }

    public DeviceSessionInfo? Session { get; }
}
