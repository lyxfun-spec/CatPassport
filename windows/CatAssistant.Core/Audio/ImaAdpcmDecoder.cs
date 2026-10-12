using System.Buffers.Binary;
using CatAssistant.Core.Protocol;

namespace CatAssistant.Core.Audio;

public sealed record AudioFrame(ushort RecordingId, ushort Sequence, bool First, bool Last, short[] Samples);

public static class ImaAdpcmDecoder
{
    private static readonly int[] IndexTable = [-1, -1, -1, -1, 2, 4, 6, 8, -1, -1, -1, -1, 2, 4, 6, 8];
    private static readonly int[] StepTable = [
        7,8,9,10,11,12,13,14,16,17,19,21,23,25,28,31,34,37,41,45,50,55,60,66,73,80,88,97,107,118,
        130,143,157,173,190,209,230,253,279,307,337,371,408,449,494,544,598,658,724,796,876,963,1060,
        1166,1282,1411,1552,1707,1878,2066,2272,2499,2749,3024,3327,3660,4026,4428,4871,5358,5894,6484,
        7132,7845,8630,9493,10442,11487,12635,13899,15289,16818,18500,20350,22385,24623,27086,29794,32767];

    public static AudioFrame Decode(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < CatProtocol.AudioHeaderSize) throw new ProtocolException("音频帧头不完整。");
        if (BinaryPrimitives.ReadUInt16LittleEndian(packet) != CatProtocol.AudioMagic || packet[2] != CatProtocol.Version)
            throw new ProtocolException("音频帧 magic 或版本错误。");
        if (packet[13] != 0 || packet[12] > 88) throw new ProtocolException("音频帧 ADPCM 状态非法。");

        var count = BinaryPrimitives.ReadUInt16LittleEndian(packet[8..]);
        var expectedBytes = (Math.Max(0, count - 1) + 1) / 2;
        if (count == 0 || packet.Length != CatProtocol.AudioHeaderSize + expectedBytes)
            throw new ProtocolException("音频帧采样数与载荷不匹配。");

        var samples = new short[count];
        var predictor = (int)BinaryPrimitives.ReadInt16LittleEndian(packet[10..]);
        var index = (int)packet[12];
        samples[0] = (short)predictor;
        for (var i = 1; i < count; i++)
        {
            var packed = packet[CatProtocol.AudioHeaderSize + (i - 1) / 2];
            var code = ((i - 1) & 1) == 0 ? packed & 0x0F : packed >> 4;
            var step = StepTable[index];
            var difference = step >> 3;
            if ((code & 1) != 0) difference += step >> 2;
            if ((code & 2) != 0) difference += step >> 1;
            if ((code & 4) != 0) difference += step;
            predictor += (code & 8) != 0 ? -difference : difference;
            predictor = Math.Clamp(predictor, short.MinValue, short.MaxValue);
            index = Math.Clamp(index + IndexTable[code], 0, 88);
            samples[i] = (short)predictor;
        }

        var flags = packet[3];
        return new AudioFrame(
            BinaryPrimitives.ReadUInt16LittleEndian(packet[4..]),
            BinaryPrimitives.ReadUInt16LittleEndian(packet[6..]),
            (flags & 1) != 0, (flags & 2) != 0, samples);
    }
}
