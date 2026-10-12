using System.Buffers.Binary;
using System.Text;

namespace CatAssistant.Core.Protocol;

public static class CatProtocol
{
    public const ushort ControlMagic = 0xCA71;
    public const ushort AudioMagic = 0xCA72;
    public const byte Version = 1;
    public const int ControlHeaderSize = 10;
    public const int AudioHeaderSize = 14;
    public const int RequiredAttMtu = 177;
    public const int TranscriptMaxBytes = 2048;
    public const int AnswerMaxBytes = 12288;

    public static IReadOnlyList<byte[]> Fragment(MessageType type, ushort messageId, ReadOnlyMemory<byte> payload, int maxGattValue)
    {
        var chunkSize = maxGattValue - ControlHeaderSize;
        if (chunkSize <= 0) throw new ArgumentOutOfRangeException(nameof(maxGattValue));
        if (payload.Length > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(payload));

        var packets = new List<byte[]>();
        var offset = 0;
        do
        {
            var count = Math.Min(chunkSize, payload.Length - offset);
            var packet = new byte[ControlHeaderSize + count];
            BinaryPrimitives.WriteUInt16LittleEndian(packet, ControlMagic);
            packet[2] = Version;
            packet[3] = (byte)type;
            BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(4), messageId);
            BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(6), (ushort)offset);
            BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(8), (ushort)payload.Length);
            payload.Slice(offset, count).CopyTo(packet.AsMemory(ControlHeaderSize));
            packets.Add(packet);
            offset += count;
        } while (offset < payload.Length);
        return packets;
    }

    public static byte[] Utf8(string text, int maximumBytes)
    {
        var filtered = DisplayText.Filter(text);
        var bytes = Encoding.UTF8.GetBytes(filtered);
        if (bytes.Length > maximumBytes) throw new ProtocolException($"UTF-8 文本超过 {maximumBytes} 字节限制。");
        return bytes;
    }
}

public enum MessageType : byte
{
    Hello = 0x01, RecordStart = 0x10, RecordEnd = 0x11, RecordCancel = 0x12,
    TranscriptConfirm = 0x13, TranscriptRetry = 0x14, TranscriptCancel = 0x15,
    NewConversation = 0x20, CancelTurn = 0x21, Ping = 0x7E,
    AppReady = 0x81, AsrStarted = 0x82, AsrResult = 0x83, AsrError = 0x84,
    ChatStarted = 0x85, ChatDelta = 0x86, ChatDone = 0x87, ChatError = 0x88,
    SessionReset = 0x89, AppStatus = 0x8A, TimeSync = 0x8B, ScreenTimeout = 0x8C,
    Pong = 0xFE
}

public sealed class ProtocolException(string message) : Exception(message);
public sealed record ControlMessage(MessageType Type, ushort MessageId, byte[] Payload);
