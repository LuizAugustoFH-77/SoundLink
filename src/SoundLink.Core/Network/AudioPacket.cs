using System;
using System.Buffers.Binary;

namespace SoundLink.Core.Network;

/// <summary>
/// Binary format for UDP audio packets.
/// Header: [SequenceNumber:u32][Timestamp:u32] = 8 bytes, followed by Opus frame data.
/// </summary>
public static class AudioPacket
{
    public const int HeaderSize = 8;

    public static byte[] Create(uint sequenceNumber, uint timestamp, byte[] opusData, int opusLength)
    {
        var packet = new byte[HeaderSize + opusLength];
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(0, 4), sequenceNumber);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4, 4), timestamp);
        Buffer.BlockCopy(opusData, 0, packet, HeaderSize, opusLength);
        return packet;
    }

    public static (uint SequenceNumber, uint Timestamp, int DataOffset, int DataLength) Parse(byte[] packet, int packetLength)
    {
        if (packetLength < HeaderSize)
            throw new ArgumentException("Packet too small to contain header.");

        uint seqNum = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(0, 4));
        uint timestamp = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(4, 4));
        return (seqNum, timestamp, HeaderSize, packetLength - HeaderSize);
    }
}
