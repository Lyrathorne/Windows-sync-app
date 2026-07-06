using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeviceSync.Protocol;

public static class ProtocolSerializer
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
    };

    public static string Serialize(ProtocolMessage message)
    {
        try
        {
            return JsonSerializer.Serialize(message, Options);
        }
        catch (JsonException error)
        {
            throw new ProtocolException("Message cannot be serialized.", error);
        }
    }

    public static ProtocolMessage Deserialize(string rawJson)
    {
        try
        {
            return JsonSerializer.Deserialize<ProtocolMessage>(rawJson, Options)
                ?? throw new ProtocolException("Message JSON is empty.");
        }
        catch (JsonException error)
        {
            throw new ProtocolException("Message JSON is invalid.", error);
        }
    }

    public static JsonElement PayloadToJson<T>(T payload)
    {
        return JsonSerializer.SerializeToElement(payload, Options);
    }

    public static T DecodePayload<T>(JsonElement payload)
    {
        try
        {
            return payload.Deserialize<T>(Options)
                ?? throw new ProtocolException("Payload JSON is empty.");
        }
        catch (JsonException error)
        {
            throw new ProtocolException("Payload JSON is invalid.", error);
        }
    }
}
