using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace CatAssistant.Setup.Services;

internal static class WindowsInstallerWorker
{
    public static int Run(string[] args)
    {
        var msiPath = GetArgument(args, "--msi") ?? throw new ArgumentException("Missing --msi.");
        var encodedProperties = GetArgument(args, "--properties") ?? throw new ArgumentException("Missing --properties.");
        var progressPath = GetArgument(args, "--progress-file") ?? throw new ArgumentException("Missing --progress-file.");
        var logPath = GetArgument(args, "--log") ?? throw new ArgumentException("Missing --log.");
        var properties = Encoding.UTF8.GetString(Convert.FromBase64String(encodedProperties));

        var writer = new ProgressFileWriter(progressPath);
        writer.Write(new InstallationProgress(0, "正在启动 Windows Installer…"));
        var tracker = new WindowsInstallerProgressTracker(writer.Write);
        return checked((int)tracker.Install(msiPath, properties, logPath));
    }

    public static bool ProgressSelfTest()
    {
        var values = new List<int>();
        var tracker = new WindowsInstallerProgressTracker(progress => values.Add(progress.Percent));
        tracker.ApplyProgressRecord(0, 1000, 0, 0);
        tracker.ApplyProgressRecord(2, 250, 0, 0);
        tracker.ApplyProgressRecord(2, 250, 0, 0);
        tracker.ApplyProgressRecord(2, 500, 0, 0);
        return values.Count > 0 && values[^1] == 100 && values.SequenceEqual(values.Order());
    }

    private static string? GetArgument(string[] args, string name)
    {
        var index = Array.FindIndex(args, value => value.Equals(name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private sealed class ProgressFileWriter(string path)
    {
        private readonly object sync = new();

        public void Write(InstallationProgress progress)
        {
            var line = JsonSerializer.Serialize(progress) + Environment.NewLine;
            lock (sync) File.AppendAllText(path, line, new UTF8Encoding(false));
        }
    }
}

internal sealed class WindowsInstallerProgressTracker
{
    private const uint InstallMessageMask = 0xFF000000;
    private const uint InstallMessageActionData = 0x09000000;
    private const uint InstallMessageProgress = 0x0A000000;
    private const uint InstallLogModeFatalExit = 1u << 0;
    private const uint InstallLogModeError = 1u << 1;
    private const uint InstallLogModeWarning = 1u << 2;
    private const uint InstallLogModeActionStart = 1u << 8;
    private const uint InstallLogModeActionData = 1u << 9;
    private const uint InstallLogModeProgress = 1u << 10;
    private const uint InstallLogModeVerbose = 1u << 12;
    private const uint InstallLogModeExtraDebug = 1u << 13;
    private const uint InstallLogAttributesFlushEachLine = 1;
    private const uint InstallUiLevelNone = 2;

    private readonly InstallUiHandlerRecord handler;
    private readonly Action<InstallationProgress> report;
    private long totalTicks;
    private long completedTicks;
    private int ticksPerActionData;
    private bool actionDataIncrements;
    private bool forward = true;
    private bool initialized;
    private int lastPercent = -1;

    public WindowsInstallerProgressTracker(Action<InstallationProgress> report)
    {
        this.report = report;
        handler = HandleMessage;
    }

    public uint Install(string msiPath, string properties, string logPath)
    {
        NativeMethods.MsiEnableLog(InstallLogModeVerbose | InstallLogModeExtraDebug, logPath, InstallLogAttributesFlushEachLine);
        NativeMethods.MsiSetInternalUI(InstallUiLevelNone, IntPtr.Zero);
        var filter = InstallLogModeFatalExit | InstallLogModeError | InstallLogModeWarning |
                     InstallLogModeActionStart | InstallLogModeActionData | InstallLogModeProgress;
        var setHandlerResult = NativeMethods.MsiSetExternalUIRecord(handler, filter, IntPtr.Zero, IntPtr.Zero);
        if (setHandlerResult != 0) return setHandlerResult;

        try
        {
            var result = NativeMethods.MsiInstallProduct(msiPath, properties);
            if (result is 0 or 3010) Report(100);
            return result;
        }
        finally
        {
            NativeMethods.MsiSetExternalUIRecord(null, 0, IntPtr.Zero, IntPtr.Zero);
        }
    }

    internal void ApplyProgressRecord(int type, int field2, int field3, int field4)
    {
        switch (type)
        {
            case 0:
                totalTicks = Math.Max(1, Math.Abs((long)field2));
                forward = field3 == 0;
                completedTicks = forward ? 0 : totalTicks;
                initialized = true;
                Report(CalculatePercent());
                break;
            case 1:
                ticksPerActionData = Math.Abs(field2);
                actionDataIncrements = field3 == 1;
                break;
            case 2 when initialized:
                completedTicks += forward ? Math.Abs((long)field2) : -Math.Abs((long)field2);
                Report(CalculatePercent());
                break;
            case 3 when initialized:
                totalTicks = Math.Max(1, totalTicks + field2);
                Report(CalculatePercent());
                break;
        }
    }

    private int HandleMessage(IntPtr context, uint messageType, IntPtr record)
    {
        var message = messageType & InstallMessageMask;
        if (message == InstallMessageProgress)
        {
            ApplyProgressRecord(
                NativeMethods.MsiRecordGetInteger(record, 1),
                NativeMethods.MsiRecordGetInteger(record, 2),
                NativeMethods.MsiRecordGetInteger(record, 3),
                NativeMethods.MsiRecordGetInteger(record, 4));
        }
        else if (message == InstallMessageActionData && initialized && actionDataIncrements)
        {
            completedTicks += forward ? ticksPerActionData : -ticksPerActionData;
            Report(CalculatePercent());
        }

        return 1;
    }

    private int CalculatePercent()
    {
        if (!initialized || totalTicks <= 0) return 0;
        return Math.Clamp((int)Math.Round(completedTicks * 100d / totalTicks), 0, 100);
    }

    private void Report(int percent)
    {
        percent = Math.Max(lastPercent, Math.Clamp(percent, 0, 100));
        if (percent == lastPercent) return;
        lastPercent = percent;
        var status = percent switch
        {
            >= 96 => "正在完成安装…",
            >= 10 => "正在安装程序文件…",
            _ => "正在准备 Windows Installer…"
        };
        report(new InstallationProgress(percent, status));
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int InstallUiHandlerRecord(IntPtr context, uint messageType, IntPtr record);

    private static class NativeMethods
    {
        [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiInstallProductW")]
        internal static extern uint MsiInstallProduct(string packagePath, string commandLine);

        [DllImport("msi.dll")]
        internal static extern uint MsiSetInternalUI(uint uiLevel, IntPtr windowHandle);

        [DllImport("msi.dll")]
        internal static extern uint MsiSetExternalUIRecord(InstallUiHandlerRecord? handler, uint messageFilter, IntPtr context, IntPtr previousHandler);

        [DllImport("msi.dll")]
        internal static extern int MsiRecordGetInteger(IntPtr record, uint field);

        [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiEnableLogW")]
        internal static extern uint MsiEnableLog(uint logMode, string logFile, uint logAttributes);
    }
}
