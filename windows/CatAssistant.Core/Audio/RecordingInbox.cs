using CatAssistant.Core.Protocol;

namespace CatAssistant.Core.Audio;

/// <summary>
/// Reorders audio notifications around RECORD_START without weakening the
/// recording assembler's gap detection. Windows may dispatch notifications
/// from different GATT characteristics on different callback threads.
/// </summary>
public sealed class RecordingInbox(int maxBufferedFrames = 64)
{
    private readonly Dictionary<ushort, SortedDictionary<ushort, AudioFrame>> _pending = [];
    private RecordingAssembler? _active;
    private ushort? _activeId;

    public ushort? ActiveRecordingId => _activeId;
    public int ActiveFrameCount => _active?.FrameCount ?? 0;

    public void Begin(ushort recordingId, int sampleRate)
    {
        _active = new RecordingAssembler(recordingId, sampleRate);
        _activeId = recordingId;

        foreach (var staleId in _pending.Keys.Where(id => id != recordingId).ToArray())
            _pending.Remove(staleId);
        DrainContiguous();
    }

    public void Add(AudioFrame frame)
    {
        if (_activeId == frame.RecordingId && frame.Sequence < ActiveFrameCount)
            throw new ProtocolException($"音频序号重复，收到 {frame.Sequence}。");

        if (!_pending.TryGetValue(frame.RecordingId, out var frames))
        {
            frames = [];
            _pending.Add(frame.RecordingId, frames);
        }
        if (frames.ContainsKey(frame.Sequence))
            throw new ProtocolException($"音频序号重复，收到 {frame.Sequence}。");
        if (frames.Count >= maxBufferedFrames)
            throw new ProtocolException($"等待排序的音频超过 {maxBufferedFrames} 帧。");

        frames.Add(frame.Sequence, frame);
        if (_activeId == frame.RecordingId) DrainContiguous();
    }

    public bool TryComplete(ushort recordingId, int advertisedFrames,
                            out RecordingAssembler? recording)
    {
        recording = null;
        if (_activeId != recordingId || _active is null)
            throw new ProtocolException(
                $"录音结束 ID 与当前录音不匹配（结束 #{recordingId}，当前 #{_activeId?.ToString() ?? "无"}）。");
        if (advertisedFrames < 0)
            throw new ProtocolException("录音帧数非法。");

        DrainContiguous();
        if (_active.FrameCount < advertisedFrames) return false;
        if (_active.FrameCount != advertisedFrames ||
            (_pending.TryGetValue(recordingId, out var pending) && pending.Count != 0))
            throw new ProtocolException(
                $"录音帧数不符，收到 {_active.FrameCount}，卡片声明 {advertisedFrames}。");

        recording = _active;
        _active = null;
        _activeId = null;
        _pending.Remove(recordingId);
        return true;
    }

    public void Cancel(ushort? recordingId = null)
    {
        if (recordingId is ushort id) _pending.Remove(id);
        else _pending.Clear();

        if (recordingId is null || _activeId == recordingId)
        {
            _active = null;
            _activeId = null;
        }
    }

    private void DrainContiguous()
    {
        if (_active is null || _activeId is not ushort id ||
            !_pending.TryGetValue(id, out var frames)) return;

        while (frames.TryGetValue(checked((ushort)_active.FrameCount), out var frame))
        {
            _active.Add(frame);
            frames.Remove(frame.Sequence);
        }
        if (frames.Count == 0) _pending.Remove(id);
    }
}
