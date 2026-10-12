using CatAssistant.Core.Protocol;

namespace CatAssistant.Core.Audio;

public sealed class RecordingAssembler(ushort recordingId, int sampleRate)
{
    private readonly MemoryStream _pcm = new();
    private ushort _expectedSequence;
    private bool _hasFrame;
    public int FrameCount { get; private set; }

    public void Add(AudioFrame frame)
    {
        if (frame.RecordingId != recordingId) throw new ProtocolException("音频录音 ID 不匹配。");
        if (!_hasFrame && !frame.First) throw new ProtocolException("录音缺少首帧。");
        if (_hasFrame && frame.Sequence != _expectedSequence) throw new ProtocolException($"音频序号丢失，期望 {_expectedSequence}，收到 {frame.Sequence}。");
        if (!_hasFrame) _expectedSequence = frame.Sequence;
        if (_pcm.Length + frame.Samples.Length * sizeof(short) > sampleRate * sizeof(short) * 30L)
            throw new ProtocolException("录音超过 30 秒限制。");

        foreach (var sample in frame.Samples)
        {
            _pcm.WriteByte((byte)sample);
            _pcm.WriteByte((byte)(sample >> 8));
        }
        _expectedSequence = unchecked((ushort)(frame.Sequence + 1));
        _hasFrame = true;
        FrameCount++;
    }

    public byte[] ToWav()
    {
        var pcm = _pcm.ToArray();
        using var wav = new MemoryStream(44 + pcm.Length);
        using var writer = new BinaryWriter(wav);
        writer.Write("RIFF"u8);
        writer.Write(36 + pcm.Length);
        writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)1);
        writer.Write(sampleRate); writer.Write(sampleRate * 2); writer.Write((short)2); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(pcm.Length); writer.Write(pcm);
        return wav.ToArray();
    }
}
