namespace DeviceSync.Protocol;

public sealed class ProtocolException : Exception
{
    public ProtocolException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
