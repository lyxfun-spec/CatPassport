namespace CatAssistant.App.Models;

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
