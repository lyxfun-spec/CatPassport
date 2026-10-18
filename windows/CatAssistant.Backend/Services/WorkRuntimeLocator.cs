namespace CatAssistant.Backend.Services;

public static class WorkRuntimeLocator
{
    public static string? FindPython()
    {
        var configured = Environment.GetEnvironmentVariable("CATASSISTANT_WORK_RUNTIME");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var configuredPython = Directory.Exists(configured) ? Path.Combine(configured, "python.exe") : configured;
            if (File.Exists(configuredPython)) return Path.GetFullPath(configuredPython);
        }

        var packaged = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "work-runtime", "python.exe"));
        if (File.Exists(packaged)) return packaged;
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "CatAssistant.Desktop", ".artifacts", "work-runtime", "python.exe");
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            candidate = Path.Combine(directory.FullName, "windows", "CatAssistant.Desktop", ".artifacts", "work-runtime", "python.exe");
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        return null;
    }
}
