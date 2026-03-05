using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SoundLink.Core.Network;

/// <summary>
/// JSON-based control protocol messages exchanged over TCP+TLS.
/// </summary>
public enum MessageType
{
    PairRequest,
    PairResponse,
    PairAccepted,
    PairRejected,
    AudioStart,
    AudioStop,
    VolumeChange,
    Ping,
    Pong,
    DeviceInfo,
    Disconnect
}

public sealed class ControlMessage
{
    [JsonPropertyName("type")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public MessageType Type { get; set; }

    [JsonPropertyName("payload")]
    public JsonElement? Payload { get; set; }

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public static ControlMessage Create(MessageType type, object? payload = null)
    {
        var msg = new ControlMessage { Type = type };
        if (payload != null)
        {
            var json = JsonSerializer.Serialize(payload);
            msg.Payload = JsonSerializer.Deserialize<JsonElement>(json);
        }
        return msg;
    }

    public T? GetPayload<T>() where T : class
    {
        if (Payload == null) return null;
        return JsonSerializer.Deserialize<T>(Payload.Value.GetRawText());
    }

    public byte[] Serialize()
    {
        var json = JsonSerializer.Serialize(this);
        var jsonBytes = System.Text.Encoding.UTF8.GetBytes(json);
        // Length-prefixed: [4 bytes length][JSON bytes]
        var packet = new byte[4 + jsonBytes.Length];
        BitConverter.TryWriteBytes(packet.AsSpan(0, 4), jsonBytes.Length);
        Buffer.BlockCopy(jsonBytes, 0, packet, 4, jsonBytes.Length);
        return packet;
    }

    public static ControlMessage? Deserialize(byte[] data, int offset, int length)
    {
        var json = System.Text.Encoding.UTF8.GetString(data, offset, length);
        return JsonSerializer.Deserialize<ControlMessage>(json);
    }
}

// -- Payload types --

public sealed class PairRequestPayload
{
    [JsonPropertyName("deviceName")]
    public string DeviceName { get; set; } = "";

    [JsonPropertyName("pin")]
    public string Pin { get; set; } = "";
}

public sealed class PairResponsePayload
{
    [JsonPropertyName("accepted")]
    public bool Accepted { get; set; }

    [JsonPropertyName("serverName")]
    public string ServerName { get; set; } = "";

    [JsonPropertyName("audioPort")]
    public int AudioPort { get; set; }
}

public sealed class DeviceInfoPayload
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("platform")]
    public string Platform { get; set; } = "";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "";
}

public sealed class VolumeChangePayload
{
    [JsonPropertyName("volume")]
    public float Volume { get; set; } // 0.0 - 1.0
}

public sealed class PingPongPayload
{
    [JsonPropertyName("sendTime")]
    public long SendTime { get; set; }
}

public sealed class AudioStartPayload
{
    [JsonPropertyName("udpPort")]
    public int UdpPort { get; set; }

    [JsonPropertyName("transport")]
    public string Transport { get; set; } = AudioTransport.Udp;
}

public static class AudioTransport
{
    public const string Udp = "udp";
    public const string Tcp = "tcp";
}
