using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace CatAssistant.Setup.Services;

internal sealed class MsiInstaller(InstallerLog log)
{
    internal const string MsiResourceName = "CatAssistant.Setup.Payload.CatAssistant.msi";

    public static bool HasPayload => Assembly.GetExecutingAssembly().GetManifestResourceInfo(MsiResourceName) is not null;

    public async Task<InstallResult> InstallAsync(InstallOptions options, IProgress<InstallationProgress>? progress, CancellationToken cancellationToken)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "CatAssistantSetup", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var msiPath = Path.Combine(tempRoot, "CatAssistant.msi");
        var progressPath = Path.Combine(tempRoot, "progress.jsonl");
        var msiLogPath = Path.Combine(Path.GetDirectoryName(log.Path)!, $"msi-{DateTime.Now:yyyyMMdd-HHmmss}.log");

        try
        {
            progress?.Report(new InstallationProgress(0, "正在准备安装资源…"));
            await ExtractMsiAsync(msiPath, cancellationToken);
            File.WriteAllText(progressPath, string.Empty, new UTF8Encoding(false));
            log.Write($"MSI extracted to {msiPath}");

            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定安装程序路径。");
            var properties = BuildProperties(options);
            var encodedProperties = Convert.ToBase64String(Encoding.UTF8.GetBytes(properties));
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };
            startInfo.ArgumentList.Add("--install-worker");
            startInfo.ArgumentList.Add("--msi");
            startInfo.ArgumentList.Add(msiPath);
            startInfo.ArgumentList.Add("--properties");
            startInfo.ArgumentList.Add(encodedProperties);
            startInfo.ArgumentList.Add("--progress-file");
            startInfo.ArgumentList.Add(progressPath);
            startInfo.ArgumentList.Add("--log");
            startInfo.ArgumentList.Add(msiLogPath);

            log.Write("Starting elevated Windows Installer worker.");
            progress?.Report(new InstallationProgress(0, "等待管理员授权…"));
            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            try
            {
                if (!process.Start()) return new(false, false, -1, "无法启动 Windows Installer。", msiLogPath);
            }
            catch (System.ComponentModel.Win32Exception exception) when (exception.NativeErrorCode == 1223)
            {
                log.Write("Elevation was cancelled by the user.");
                return new(false, true, 1223, "已取消管理员授权，系统没有发生更改。", msiLogPath);
            }

            log.Write($"Elevated installer worker started. pid={process.Id}.");
            progress?.Report(new InstallationProgress(0, "正在启动 Windows Installer…"));
            var lastProgress = -1;
            while (!process.HasExited)
            {
                lastProgress = ReportLatestProgress(progressPath, progress, lastProgress);
                await Task.Delay(120, cancellationToken);
            }
            await process.WaitForExitAsync(cancellationToken);
            ReportLatestProgress(progressPath, progress, lastProgress);

            var exitCode = process.ExitCode;
            log.Write($"Elevated installer worker exited with code {exitCode}.");
            return exitCode switch
            {
                0 => new(true, false, exitCode, "小猫助手已经安装完成。", msiLogPath),
                1602 => new(false, true, exitCode, "安装已取消，Windows Installer 已回滚更改。", msiLogPath),
                3010 => new(true, false, exitCode, "安装完成，部分系统组件将在重启后生效。", msiLogPath),
                _ => new(false, false, exitCode, $"安装未完成（错误代码 {exitCode}）。Windows Installer 已尝试自动回滚。", msiLogPath)
            };
        }
        catch (OperationCanceledException)
        {
            return new(false, true, 1602, "安装已取消。", msiLogPath);
        }
        catch (Exception exception)
        {
            log.Write($"Installation error: {exception}");
            return new(false, false, -1, $"安装程序遇到错误：{exception.Message}", msiLogPath);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static int ReportLatestProgress(string path, IProgress<InstallationProgress>? progress, int lastProgress)
    {
        if (progress is null || !File.Exists(path)) return lastProgress;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            string? lastLine = null;
            while (reader.ReadLine() is { } line)
                if (!string.IsNullOrWhiteSpace(line)) lastLine = line;
            if (lastLine is null) return lastProgress;
            var value = JsonSerializer.Deserialize<InstallationProgress>(lastLine);
            if (value is null || value.Percent == lastProgress) return lastProgress;
            progress.Report(value);
            return value.Percent;
        }
        catch (IOException)
        {
            return lastProgress;
        }
        catch (JsonException)
        {
            return lastProgress;
        }
    }

    private static async Task ExtractMsiAsync(string destination, CancellationToken cancellationToken)
    {
        await using var source = Assembly.GetExecutingAssembly().GetManifestResourceStream(MsiResourceName)
            ?? throw new InvalidOperationException("安装资源缺失，请重新下载安装程序。");
        await using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(target, cancellationToken);
        await target.FlushAsync(cancellationToken);
    }

    private static string BuildProperties(InstallOptions options)
    {
        static string Quote(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
        return string.Join(' ',
            $"INSTALLFOLDER={Quote(options.InstallDirectory)}",
            $"CREATE_DESKTOP_SHORTCUT={(options.DesktopShortcut ? 1 : 0)}",
            $"CREATE_STARTMENU_SHORTCUT={(options.StartMenuShortcut ? 1 : 0)}",
            "REBOOT=ReallySuppress");
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch { /* The elevated worker can briefly retain a handle; cleanup is best effort. */ }
    }
}
