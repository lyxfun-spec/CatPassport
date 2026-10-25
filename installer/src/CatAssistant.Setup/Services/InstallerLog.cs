using System.Text;

namespace CatAssistant.Setup.Services;

internal sealed class InstallerLog
{
    private readonly object sync = new();

    public InstallerLog()
    {
        var root = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FoloToy", "CatAssistant", "InstallerLogs");
        Directory.CreateDirectory(root);
        Path = System.IO.Path.Combine(root, $"setup-{DateTime.Now:yyyyMMdd-HHmmss}.log");
    }

    public string Path { get; }

    public void Write(string message)
    {
        var line = $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}";
        lock (sync)
        {
            File.AppendAllText(Path, line, new UTF8Encoding(false));
        }
    }
}
