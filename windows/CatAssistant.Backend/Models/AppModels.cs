using CatAssistant.Backend.Services;

namespace CatAssistant.Backend.Models;

public sealed class AppSettings
{
    public string AsrModel { get; set; } = "qwen-audio-3.1-asr-flash";
    public string ChatModel { get; set; } = "qwen-plus";
    public ulong? LastBluetoothAddress { get; set; }
    public string? LastDeviceName { get; set; }
    public uint ScreenTimeoutSeconds { get; set; } = 60;
    public bool AutoReconnect { get; set; } = true;
}

public sealed record ChatMessage(string Role, string Content, DateTimeOffset CreatedAt);
public sealed record PublicSettings(string AsrModel, string ChatModel, uint ScreenTimeoutSeconds, bool AutoReconnect, bool HasApiKey);
public sealed record BackendSnapshot(
    string Status,
    string Detail,
    bool IsConnected,
    bool HasPairedDevice,
    uint MaxPduSize,
    IReadOnlyList<CatDevice> Devices,
    PublicSettings Settings,
    long? ConversationId,
    IReadOnlyList<ChatMessage> Messages);

public sealed record StreamUpdate(long ConversationId, string Phase, string? Delta = null, ChatMessage? Message = null);
