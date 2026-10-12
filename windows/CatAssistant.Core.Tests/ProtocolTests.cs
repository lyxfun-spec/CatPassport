using System.Buffers.Binary;
using System.Text;
using CatAssistant.Core.Audio;
using CatAssistant.Core.Protocol;
using CatAssistant.Core.Sessions;
using Xunit;

namespace CatAssistant.Core.Tests;

public class ProtocolTests
{
    [Fact]
    public void FragmentsAndReassemblesInOrder()
    {
        var payload = Encoding.UTF8.GetBytes(new string('猫', 80));
        var parts = CatProtocol.Fragment(MessageType.AsrResult, 9, payload, 30).ToArray();
        var receiver = new ControlReassembler();
        ControlMessage? result = null;
        foreach (var part in parts) result = receiver.Accept(part);
        Assert.Equal(payload, result!.Payload);
        Assert.Equal(MessageType.AsrResult, result.Type);
    }

    [Fact]
    public void RejectsMissingControlFragment()
    {
        var parts = CatProtocol.Fragment(MessageType.ChatDelta, 2, Encoding.UTF8.GetBytes(new string('x', 50)), 20).ToArray();
        var receiver = new ControlReassembler();
        receiver.Accept(parts[0]);
        Assert.Throws<ProtocolException>(() => receiver.Accept(parts[2]));
    }

    [Fact]
    public void FiltersUnsupportedUnicodeButKeepsCommonText()
        => Assert.Equal("你好，cat!\n", DisplayText.Filter("你好，cat!🐈\n𝄞"));

    [Fact]
    public void DecodesKnownImaNibbles()
    {
        var packet = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, CatProtocol.AudioMagic);
        packet[2] = 1; packet[3] = 3;
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(4), 7);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(6), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(8), 4);
        BinaryPrimitives.WriteInt16LittleEndian(packet.AsSpan(10), 0);
        packet[12] = 0; packet[14] = 0x11; packet[15] = 0x01;
        Assert.Equal(new short[] { 0, 1, 2, 3 }, ImaAdpcmDecoder.Decode(packet).Samples);
    }

    [Fact]
    public void RejectsAudioSequenceLoss()
    {
        var assembler = new RecordingAssembler(5, 16000);
        assembler.Add(new AudioFrame(5, 10, true, false, [1]));
        Assert.Throws<ProtocolException>(() => assembler.Add(new AudioFrame(5, 12, false, false, [2])));
    }

    [Fact]
    public void SessionExpiresAfterThirtyMinutes()
    {
        var policy = new SessionPolicy(TimeSpan.FromMinutes(30));
        var start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        policy.Complete(start);
        Assert.False(policy.ShouldStartNew(start.AddMinutes(29)));
        Assert.True(policy.ShouldStartNew(start.AddMinutes(30)));
    }
}
