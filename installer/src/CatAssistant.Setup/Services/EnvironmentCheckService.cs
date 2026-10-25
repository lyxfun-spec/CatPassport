using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CatAssistant.Setup.Services;

internal static class EnvironmentCheckService
{
    private const long RequiredFreeBytes = 900L * 1024 * 1024;

    public static Task<IReadOnlyList<CheckResult>> RunAsync(bool hasEmbeddedMsi)
    {
        return Task.Run<IReadOnlyList<CheckResult>>(() =>
        {
            var results = new List<CheckResult>();
            var windowsVersion = Environment.OSVersion.Version;
            var supportedWindows = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041);
            results.Add(new("Windows 版本", supportedWindows ? "Windows 10 2004 或更高版本" : $"当前版本 {windowsVersion} 不受支持", supportedWindows, true));

            var architectureOk = RuntimeInformation.OSArchitecture == Architecture.X64;
            results.Add(new("系统架构", architectureOk ? "64 位 Windows" : $"当前架构为 {RuntimeInformation.OSArchitecture}", architectureOk, true));

            var running = Process.GetProcessesByName("CatAssistant");
            foreach (var process in running) process.Dispose();
            var appClosed = running.Length == 0;
            results.Add(new("运行状态", appClosed ? "小猫助手未在运行" : "请退出正在运行的小猫助手后重试", appClosed, true));

            results.Add(new("安装资源", hasEmbeddedMsi ? "安装资源完整" : "当前是界面预览构建，未嵌入 MSI", hasEmbeddedMsi, true));

            return results;
        });
    }

    public static Task<InstallLocationCheck> CheckInstallLocationAsync(string installDirectory, CancellationToken cancellationToken)
    {
        return Task.Run<InstallLocationCheck>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (string.IsNullOrWhiteSpace(installDirectory))
                    return new(false, "请输入安装位置");

                var fullPath = Path.GetFullPath(installDirectory);
                if (!Path.IsPathFullyQualified(fullPath))
                    return new(false, "请选择完整的安装路径");
                if (File.Exists(fullPath))
                    return new(false, "该位置已被同名文件占用");

                var current = fullPath;
                while (!Directory.Exists(current))
                {
                    if (File.Exists(current)) return new(false, $"路径中的 {Path.GetFileName(current)} 是文件，无法创建文件夹");
                    var parent = Directory.GetParent(current)?.FullName;
                    if (string.IsNullOrWhiteSpace(parent) || parent == current) break;
                    current = parent;
                }

                var root = Path.GetPathRoot(fullPath);
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                    return new(false, "目标磁盘不存在或当前不可用");

                var drive = new DriveInfo(root);
                if (!drive.IsReady) return new(false, "目标磁盘尚未就绪");
                if (drive.AvailableFreeSpace < RequiredFreeBytes)
                    return new(false, $"磁盘空间不足：可用 {FormatBytes(drive.AvailableFreeSpace)}，需要至少 {FormatBytes(RequiredFreeBytes)}");

                return new(true, $"位置可用 · 磁盘剩余 {FormatBytes(drive.AvailableFreeSpace)}，需要至少 {FormatBytes(RequiredFreeBytes)}");
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return new(false, $"安装位置不可用：{exception.Message}");
            }
        }, cancellationToken);
    }

    private static string FormatBytes(long bytes) => $"{bytes / 1024d / 1024d / 1024d:0.0} GB";
}

