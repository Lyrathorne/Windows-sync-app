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
}
