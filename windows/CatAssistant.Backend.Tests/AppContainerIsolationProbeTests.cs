using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Xunit;

namespace CatAssistant.Backend.Tests;

public sealed class AppContainerIsolationProbeTests
{
    [Fact]
    [Trait("Category", "SecurityIntegration")]
    public void AppContainerCanReadGrantedDirectoryButNotSiblingDirectory()
    {
        // Creating an AppContainer profile changes per-user Windows state and is blocked by ordinary test sandboxes.
        // Run this probe explicitly outside the development sandbox with CATASSISTANT_RUN_APPCONTAINER_PROBE=1.
        if (Environment.GetEnvironmentVariable("CATASSISTANT_RUN_APPCONTAINER_PROBE") != "1") return;
        if (!OperatingSystem.IsWindowsVersionAtLeast(10)) return;
        var root = Path.Combine(Path.GetTempPath(), $"cat-assistant-sandbox-{Guid.NewGuid():N}");
        var allowed = Path.Combine(root, "allowed");
        var denied = Path.Combine(root, "denied");
        Directory.CreateDirectory(allowed);
        Directory.CreateDirectory(denied);
        var allowedFile = Path.Combine(allowed, "allowed.txt");
        var deniedFile = Path.Combine(denied, "denied.txt");
        File.WriteAllText(allowedFile, "allowed");
        File.WriteAllText(deniedFile, "denied");

        var profileName = $"FoloToy.CatAssistant.Probe.{Guid.NewGuid():N}";
        IntPtr sidPointer = IntPtr.Zero;
        try
        {
            var result = NativeMethods.CreateAppContainerProfile(profileName, profileName, "Temporary Cat Assistant sandbox probe", IntPtr.Zero, 0, out sidPointer);
            if (result < 0) Marshal.ThrowExceptionForHR(result);
            var sid = new SecurityIdentifier(sidPointer);
            GrantDirectoryAccess(allowed, sid);

            Assert.Equal(0u, RunTypeCommandInAppContainer(sidPointer, allowed, allowedFile));
            Assert.NotEqual(0u, RunTypeCommandInAppContainer(sidPointer, allowed, deniedFile));
            var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
            Assert.Equal(0u, RunPowerShellReadInAppContainer(sidPointer, allowed, powershell, allowedFile));
            Assert.NotEqual(0u, RunPowerShellReadInAppContainer(sidPointer, allowed, powershell, deniedFile));
            var curl = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "curl.exe");
            var networkWithCapability = RunNetworkProbe(sidPointer, allowed, curl, includeInternetClientCapability: true);
            Assert.True(networkWithCapability.ExitCode == 0, networkWithCapability.Error);
            Assert.NotEqual(0u, RunNetworkProbe(sidPointer, allowed, curl, includeInternetClientCapability: false).ExitCode);
        }
        finally
        {
            if (sidPointer != IntPtr.Zero) NativeMethods.FreeSid(sidPointer);
            NativeMethods.DeleteAppContainerProfile(profileName);
            Directory.Delete(root, recursive: true);
        }
    }

    private static void GrantDirectoryAccess(string path, SecurityIdentifier sid)
    {
        var directory = new DirectoryInfo(path);
        var security = directory.GetAccessControl(AccessControlSections.Access);
        security.AddAccessRule(new FileSystemAccessRule(
            sid,
            FileSystemRights.Modify | FileSystemRights.ReadAndExecute | FileSystemRights.ListDirectory,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        directory.SetAccessControl(security);
    }

    private static uint RunTypeCommandInAppContainer(IntPtr appContainerSid, string workingDirectory, string filePath)
    {
        if (filePath.IndexOfAny(['&', '|', '<', '>']) >= 0) throw new ArgumentException("Probe path contains shell metacharacters.", nameof(filePath));
        var executable = Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";
        return RunCommandInAppContainer(appContainerSid, workingDirectory, executable, $"/d /q /c type \"{filePath}\" >nul", false);
    }

    private static uint RunPowerShellReadInAppContainer(IntPtr appContainerSid, string workingDirectory, string executable, string filePath)
    {
        if (filePath.Contains('\'')) throw new ArgumentException("Probe path contains a quote.", nameof(filePath));
        return RunCommandInAppContainer(
            appContainerSid,
            workingDirectory,
            executable,
            $"-NoLogo -NoProfile -NonInteractive -Command \"$ErrorActionPreference='Stop'; Get-Content -LiteralPath '{filePath}' | Out-Null\"",
            false);
    }

    private static (uint ExitCode, string Error) RunNetworkProbe(
        IntPtr appContainerSid,
        string workingDirectory,
        string executable,
        bool includeInternetClientCapability)
    {
        var suffix = includeInternetClientCapability ? "allowed" : "denied";
        var errorPath = Path.Combine(workingDirectory, $"curl-{suffix}.err");
        var outputPath = Path.Combine(workingDirectory, $"curl-{suffix}.html");
        var commandInterpreter = Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";
        var arguments = $"/d /q /c \"\"{executable}\" --fail --silent --show-error --connect-timeout 3 --max-time 5 https://example.com/ -o \"{outputPath}\" 2>\"{errorPath}\"\"";
        var exitCode = RunCommandInAppContainer(
            appContainerSid,
            workingDirectory,
            commandInterpreter,
            arguments,
            includeInternetClientCapability,
            tolerateTimeout: !includeInternetClientCapability);
        var error = File.Exists(errorPath) ? File.ReadAllText(errorPath) : "curl produced no diagnostic output.";
        return (exitCode, error);
    }

    private static uint RunCommandInAppContainer(
        IntPtr appContainerSid,
        string workingDirectory,
        string executable,
        string arguments,
        bool includeInternetClientCapability,
        bool tolerateTimeout = false)
    {
        var attributeListSize = IntPtr.Zero;
        NativeMethods.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeListSize);
        var attributeList = Marshal.AllocHGlobal(attributeListSize);
        var capabilitiesPointer = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.SecurityCapabilities>());
        IntPtr capabilitySid = IntPtr.Zero;
        IntPtr capabilityArray = IntPtr.Zero;
        IntPtr processHandle = IntPtr.Zero;
        IntPtr threadHandle = IntPtr.Zero;
        IntPtr jobHandle = IntPtr.Zero;
        try
        {
            if (!NativeMethods.InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var capabilities = new NativeMethods.SecurityCapabilities { AppContainerSid = appContainerSid };
            if (includeInternetClientCapability)
            {
                if (!NativeMethods.ConvertStringSidToSid("S-1-15-3-1", out capabilitySid))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                capabilityArray = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.SidAndAttributes>());
                Marshal.StructureToPtr(new NativeMethods.SidAndAttributes { Sid = capabilitySid, Attributes = 4 }, capabilityArray, false);
                capabilities.Capabilities = capabilityArray;
                capabilities.CapabilityCount = 1;
            }
            Marshal.StructureToPtr(capabilities, capabilitiesPointer, false);
            if (!NativeMethods.UpdateProcThreadAttribute(
                    attributeList,
                    0,
                    (IntPtr)NativeMethods.ProcThreadAttributeSecurityCapabilities,
                    capabilitiesPointer,
                    (IntPtr)Marshal.SizeOf<NativeMethods.SecurityCapabilities>(),
                    IntPtr.Zero,
                    IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            var startup = new NativeMethods.StartupInfoEx
            {
                StartupInfo = new NativeMethods.StartupInfo { Cb = Marshal.SizeOf<NativeMethods.StartupInfoEx>() },
                AttributeList = attributeList
            };
            var command = new StringBuilder($"\"{executable}\" {arguments}");
            if (!NativeMethods.CreateProcess(
                    executable,
                    command,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    NativeMethods.ExtendedStartupInfoPresent | NativeMethods.CreateSuspended | NativeMethods.CreateNoWindow,
                    IntPtr.Zero,
                    workingDirectory,
                    ref startup,
                    out var process))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            processHandle = process.Process;
            threadHandle = process.Thread;

            jobHandle = NativeMethods.CreateJobObject(IntPtr.Zero, null);
            if (jobHandle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            var limits = new NativeMethods.JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new NativeMethods.JobObjectBasicLimitInformation
                {
                    LimitFlags = NativeMethods.JobObjectLimitKillOnJobClose | NativeMethods.JobObjectLimitActiveProcess,
                    ActiveProcessLimit = 8
                }
            };
            var limitsPointer = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.JobObjectExtendedLimitInformation>());
            try
            {
                Marshal.StructureToPtr(limits, limitsPointer, false);
                if (!NativeMethods.SetInformationJobObject(jobHandle, 9, limitsPointer, (uint)Marshal.SizeOf<NativeMethods.JobObjectExtendedLimitInformation>()))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally { Marshal.FreeHGlobal(limitsPointer); }
            if (!NativeMethods.AssignProcessToJobObject(jobHandle, processHandle)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (NativeMethods.ResumeThread(threadHandle) == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (NativeMethods.WaitForSingleObject(processHandle, 15_000) != 0)
            {
                if (tolerateTimeout) return uint.MaxValue;
                throw new TimeoutException("AppContainer probe did not exit in time.");
            }
            if (!NativeMethods.GetExitCodeProcess(processHandle, out var exitCode)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return exitCode;
        }
        finally
        {
            if (jobHandle != IntPtr.Zero) NativeMethods.CloseHandle(jobHandle);
            if (threadHandle != IntPtr.Zero) NativeMethods.CloseHandle(threadHandle);
            if (processHandle != IntPtr.Zero) NativeMethods.CloseHandle(processHandle);
            if (attributeList != IntPtr.Zero)
            {
                NativeMethods.DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }
            Marshal.FreeHGlobal(capabilitiesPointer);
            if (capabilityArray != IntPtr.Zero) Marshal.FreeHGlobal(capabilityArray);
            if (capabilitySid != IntPtr.Zero) NativeMethods.LocalFree(capabilitySid);
        }
    }

    private static class NativeMethods
    {
        internal const long ProcThreadAttributeSecurityCapabilities = 0x00020009;
        internal const uint ExtendedStartupInfoPresent = 0x00080000;
        internal const uint CreateSuspended = 0x00000004;
        internal const uint CreateNoWindow = 0x08000000;
        internal const uint JobObjectLimitActiveProcess = 0x00000008;
        internal const uint JobObjectLimitKillOnJobClose = 0x00002000;

        [StructLayout(LayoutKind.Sequential)]
        internal struct SecurityCapabilities
        {
            public IntPtr AppContainerSid;
            public IntPtr Capabilities;
            public uint CapabilityCount;
            public uint Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct SidAndAttributes
        {
            public IntPtr Sid;
            public uint Attributes;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct StartupInfo
        {
            public int Cb;
            public string? Reserved;
            public string? Desktop;
            public string? Title;
            public uint X;
            public uint Y;
            public uint XSize;
            public uint YSize;
            public uint XCountChars;
            public uint YCountChars;
            public uint FillAttribute;
            public uint Flags;
            public ushort ShowWindow;
            public ushort Reserved2;
            public IntPtr Reserved2Pointer;
            public IntPtr StandardInput;
            public IntPtr StandardOutput;
            public IntPtr StandardError;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct StartupInfoEx
        {
            public StartupInfo StartupInfo;
            public IntPtr AttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct ProcessInformation
        {
            public IntPtr Process;
            public IntPtr Thread;
            public uint ProcessId;
            public uint ThreadId;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct JobObjectBasicLimitInformation
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct JobObjectExtendedLimitInformation
        {
            public JobObjectBasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
        internal static extern int CreateAppContainerProfile(string appContainerName, string displayName, string description, IntPtr capabilities, uint capabilityCount, out IntPtr appContainerSid);

        [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
        internal static extern int DeleteAppContainerProfile(string appContainerName);

        [DllImport("advapi32.dll")]
        internal static extern IntPtr FreeSid(IntPtr sid);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ConvertStringSidToSid(string stringSid, out IntPtr sid);

        [DllImport("kernel32.dll")]
        internal static extern IntPtr LocalFree(IntPtr memory);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool InitializeProcThreadAttributeList(IntPtr attributeList, int attributeCount, int flags, ref IntPtr size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UpdateProcThreadAttribute(IntPtr attributeList, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previousValue, IntPtr returnSize);

        [DllImport("kernel32.dll")]
        internal static extern void DeleteProcThreadAttributeList(IntPtr attributeList);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateProcess(
            string? applicationName,
            StringBuilder commandLine,
            IntPtr processAttributes,
            IntPtr threadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
            uint creationFlags,
            IntPtr environment,
            string currentDirectory,
            ref StartupInfoEx startupInfo,
            out ProcessInformation processInformation);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetInformationJobObject(IntPtr job, int informationClass, IntPtr information, uint informationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint ResumeThread(IntPtr thread);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr handle);
    }
}
