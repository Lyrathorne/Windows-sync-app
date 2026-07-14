namespace DeviceSync.Application;

public sealed class IncomingFileTransferDecisionRequestedEventArgs : EventArgs
{
    public IncomingFileTransferDecisionRequestedEventArgs(IncomingFileTransfer transfer)
    {
        Transfer = transfer;
    }

    public IncomingFileTransfer Transfer { get; }
}

public sealed class IncomingFileTransferChangedEventArgs : EventArgs
{
    public IncomingFileTransferChangedEventArgs(IncomingFileTransfer transfer)
    {
        Transfer = transfer;
    }

    public IncomingFileTransfer Transfer { get; }
}

public sealed class IncomingFileTransferProgressEventArgs : EventArgs
{
    public IncomingFileTransferProgressEventArgs(IncomingFileTransfer transfer, long bytesPerSecond)
    {
        Transfer = transfer;
        BytesPerSecond = bytesPerSecond;
    }

    public IncomingFileTransfer Transfer { get; }
    public long BytesPerSecond { get; }
    public double Progress => Transfer.SizeBytes == 0
        ? 1
        : (double)Transfer.ReceivedBytes / Transfer.SizeBytes;
}

public sealed class FileTransferResponseRequestedEventArgs : EventArgs
{
    public FileTransferResponseRequestedEventArgs(IncomingFileTransfer transfer, FileTransferResponse response)
    {
        Transfer = transfer;
        Response = response;
    }

    public IncomingFileTransfer Transfer { get; }
    public FileTransferResponse Response { get; }
}
