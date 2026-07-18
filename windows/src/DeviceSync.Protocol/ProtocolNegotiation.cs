namespace DeviceSync.Protocol;

public static class ProtocolVersionNegotiator
{
    public static int? Negotiate(int remoteLegacyVersion, int? remoteMin = null, int? remoteMax = null)
    {
        var min = remoteMin ?? remoteLegacyVersion;
        var max = remoteMax ?? remoteLegacyVersion;
        if (min <= 0 || max < min)
        {
            return null;
        }

        var commonMin = Math.Max(ProtocolConstants.ProtocolMinVersion, min);
        var commonMax = Math.Min(ProtocolConstants.ProtocolMaxVersion, max);
        return commonMax >= commonMin ? commonMax : null;
    }
}

public static class ProtocolErrorCodes
{
    public const string UnsupportedProtocolVersion = "UNSUPPORTED_PROTOCOL_VERSION";
    public const string CapabilityRequired = "CAPABILITY_REQUIRED";
    public const string PayloadTooLarge = "PAYLOAD_TOO_LARGE";
    public const string InvalidMessage = "INVALID_MESSAGE";
    public const string AuthenticationRequired = "AUTHENTICATION_REQUIRED";
}

public static class CapabilityNegotiator
{
    public static IReadOnlyList<string> Intersect(IEnumerable<string> remoteCapabilities) =>
        SupportedCapabilities.Values.Intersect(remoteCapabilities, StringComparer.Ordinal).ToArray();

    public static void Require(IEnumerable<string> remoteCapabilities, string capability)
    {
        if (!remoteCapabilities.Contains(capability, StringComparer.Ordinal))
        {
            throw new ProtocolException($"{ProtocolErrorCodes.CapabilityRequired}:{capability}");
        }
    }
}
