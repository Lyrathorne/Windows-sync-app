namespace DeviceSync.Protocol;

public static class ProtocolMessageTypes
{
    public const string ConnectionHello = "connection.hello";
    public const string ConnectionHelloAck = "connection.hello_ack";
    public const string ConnectionPing = "connection.ping";
    public const string ConnectionPong = "connection.pong";
    public const string ConnectionClose = "connection.close";
    public const string MessageAck = "message.ack";
    public const string ProtocolError = "error.protocol";
    public const string PairingRequest = "pairing.request";
    public const string PairingChallenge = "pairing.challenge";
    public const string PairingConfirm = "pairing.confirm";
    public const string PairingAccepted = "pairing.accepted";
    public const string PairingRejected = "pairing.rejected";
    public const string PairingCancel = "pairing.cancel";
    public const string PairingCompleteAck = "pairing.complete_ack";
    public const string AuthChallenge = "auth.challenge";
    public const string AuthResponse = "auth.response";
    public const string AuthAccepted = "auth.accepted";
    public const string AuthRejected = "auth.rejected";
    public const string FileOffer = "file.offer";
    public const string FileAccept = "file.accept";
    public const string FileReject = "file.reject";
    public const string FileChunk = "file.chunk";
    public const string FileComplete = "file.complete";
    public const string FileReceived = "file.received";
    public const string FileCancel = "file.cancel";
    public const string FileError = "file.error";
    public const string FileChunkReceived = "file.chunk.received";
    public const string FileResumeRequest = "file.resume.request";
    public const string FileResumeAccepted = "file.resume.accepted";
    public const string ClipboardUpdate = "clipboard.update";
    public const string TextShare = "text.share";
    public const string NotificationPosted = "notification.posted";
    public const string NotificationRemoved = "notification.removed";
    public const string FolderManifest = "folder.manifest";
    public const string FolderPlan = "folder.plan";
    public const string FolderPlanApproved = "folder.plan.approved";
    public const string FolderCancel = "folder.cancel";
    public const string FolderError = "folder.error";
}
