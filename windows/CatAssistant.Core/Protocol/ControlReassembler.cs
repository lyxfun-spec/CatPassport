using System.Buffers.Binary;

namespace CatAssistant.Core.Protocol;

public sealed class ControlReassembler(int capacity = CatProtocol.AnswerMaxBytes)
{
    private readonly byte[] _buffer = new byte[capacity];
    private bool _active;
    private ushort _messageId;
    private MessageType _type;
    private int _received;
    private int _total;

    public ControlMessage? Accept(ReadOnlySpan<byte> fragment)
    {
        try
        {
            if (fragment.Length < CatProtocol.ControlHeaderSize) throw new ProtocolException("控制分片头不完整。");
            if (BinaryPrimitives.ReadUInt16LittleEndian(fragment) != CatProtocol.ControlMagic) throw new ProtocolException("控制分片 magic 错误。");
            if (fragment[2] != CatProtocol.Version) throw new ProtocolException("不支持的协议版本。");

            var type = (MessageType)fragment[3];
            var id = BinaryPrimitives.ReadUInt16LittleEndian(fragment[4..]);
            var offset = BinaryPrimitives.ReadUInt16LittleEndian(fragment[6..]);
            var total = BinaryPrimitives.ReadUInt16LittleEndian(fragment[8..]);
            var body = fragment[CatProtocol.ControlHeaderSize..];
            if (total > _buffer.Length || offset > total || body.Length > total - offset)
                throw new ProtocolException("控制分片长度非法。");

            if (offset == 0)
            {
                _active = true;
                _messageId = id;
                _type = type;
                _received = 0;
                _total = total;
            }
            else if (!_active || id != _messageId || type != _type || total != _total || offset != _received)
            {
                throw new ProtocolException("控制分片顺序不连续。");
            }

            body.CopyTo(_buffer.AsSpan(offset));
            _received += body.Length;
            if (_received != _total) return null;

            _active = false;
            return new ControlMessage(_type, _messageId, _buffer.AsSpan(0, _total).ToArray());
        }
        catch
        {
            _active = false;
            throw;
        }
    }

    public void Reset() => _active = false;
}
