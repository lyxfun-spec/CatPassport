using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace CatAssistant.Backend.Services.Sandbox;

public sealed record SandboxCommand(
    string FileName,
    string Arguments,
    string WorkingDirectory,
    TimeSpan Timeout,
    bool AllowNetwork = true,
    int MaximumOutputCharacters = 128_000);

public sealed record SandboxCommandResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool OutputTruncated,
    TimeSpan Duration);

/// <summary>
/// Launches an untrusted process in a per-conversation AppContainer and a non-breakaway Job Object.
/// The AppContainer SID receives access only to its own workspace. Closing the job terminates the
/// full process tree, including children started by scripts.
/// </summary>
public sealed class WindowsSandboxController : IDisposable
{
    private sealed record Profile(string Name, SecurityIdentifier Sid, string PrivateStorageRoot);

    private readonly ConcurrentDictionary<string, Lazy<Profile>> _profiles = new(StringComparer.Ordinal);
    private bool _disposed;

    public async Task<SandboxCommandResult> ExecuteAsync(
        string conversationId,
        string workspaceRoot,
        SandboxCommand command,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!OperatingSystem.IsWindowsVersionAtLeast(10))
            throw new PlatformNotSupportedException("Work 沙箱需要 Windows 10 或更高版本。");
        ValidateConversationId(conversationId);
        var workspace = Path.GetFullPath(workspaceRoot);
        var workingDirectory = Path.GetFullPath(command.WorkingDirectory);
        EnsureContained(workspace, workingDirectory);
        if (!Directory.Exists(workingDirectory)) throw new DirectoryNotFoundException(workingDirectory);
        if (command.Timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(command.Timeout));
        if (command.MaximumOutputCharacters <= 0) throw new ArgumentOutOfRangeException(nameof(command.MaximumOutputCharacters));

        var profile = PrepareWorkspace(conversationId, workspace);
        return await LaunchAsync(profile.Sid, command with { WorkingDirectory = workingDirectory }, cancellationToken);
    }

    public void Prepare(string conversationId, string workspaceRoot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!OperatingSystem.IsWindowsVersionAtLeast(10))
            throw new PlatformNotSupportedException("Work 沙箱需要 Windows 10 或更高版本。");
        ValidateConversationId(conversationId);
        PrepareWorkspace(conversationId, Path.GetFullPath(workspaceRoot));
    }

    public string GetPrivateStorageRoot(string conversationId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateConversationId(conversationId);
        return GetProfile(conversationId).PrivateStorageRoot;
    }

    public void DeleteProfile(string conversationId)
    {
        ValidateConversationId(conversationId);
        var name = ProfileName(conversationId);
        _profiles.TryRemove(conversationId, out _);
        var result = NativeMethods.DeleteAppContainerProfile(name);
        const int hresultFileNotFound = unchecked((int)0x80070002);
        if (result < 0 && result != hresultFileNotFound) Marshal.ThrowExceptionForHR(result);
    }

    internal static void EnsureContained(string workspaceRoot, string path)
    {
        var root = Path.GetFullPath(workspaceRoot);
        var candidate = Path.GetFullPath(path);
        if (candidate.Equals(root, StringComparison.OrdinalIgnoreCase)) return;
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("工作目录必须位于当前对话工作区内。");
    }

    private static Profile EnsureProfile(string conversationId)
    {
        var name = ProfileName(conversationId);
        var result = NativeMethods.CreateAppContainerProfile(name, "Cat Assistant Work", "Cat Assistant Work sandbox", IntPtr.Zero, 0, out var sidPointer);
        const int hresultAlreadyExists = unchecked((int)0x800700B7);
        if (result == hresultAlreadyExists)
        {
            result = NativeMethods.DeriveAppContainerSidFromAppContainerName(name, out sidPointer);
        }
        if (result < 0) Marshal.ThrowExceptionForHR(result);
        try
        {
            var sid = new SecurityIdentifier(sidPointer);
            var folderResult = NativeMethods.GetAppContainerFolderPath(sid.Value, out var folderPointer);
            if (folderResult < 0) Marshal.ThrowExceptionForHR(folderResult);
            try
            {
                var folder = Marshal.PtrToStringUni(folderPointer) ?? throw new InvalidOperationException("无法解析 AppContainer 存储路径。");
                Directory.CreateDirectory(folder);
                return new Profile(name, sid, folder);
            }
            finally { if (folderPointer != IntPtr.Zero) Marshal.FreeCoTaskMem(folderPointer); }
        }
        finally { if (sidPointer != IntPtr.Zero) NativeMethods.FreeSid(sidPointer); }
    }

    private Profile GetProfile(string conversationId) => _profiles.GetOrAdd(conversationId, id => new Lazy<Profile>(
        () => EnsureProfile(id), LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    private Profile PrepareWorkspace(string conversationId, string workspace)
    {
        var profile = GetProfile(conversationId);
        lock (profile) GrantWorkspaceAccess(workspace, profile.Sid);
        return profile;
    }

    private static void GrantWorkspaceAccess(string workspace, SecurityIdentifier sid)
    {
        var directory = new DirectoryInfo(workspace);
        var security = directory.GetAccessControl(AccessControlSections.Access);
        var rule = new FileSystemAccessRule(
            sid,
            FileSystemRights.Modify | FileSystemRights.ReadAndExecute | FileSystemRights.ListDirectory,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow);
        if (HasModifyRule(security, sid)) return;
        security.AddAccessRule(rule);
        directory.SetAccessControl(security);
        SetLowIntegrityLabel(directory.FullName, isDirectory: true);

        // inputs/work/outputs already exist before the first launch. Apply the initial grant to
        // existing descendants once; later files inherit it. Never follow reparse points here.
        var pending = new Stack<DirectoryInfo>();
        pending.Push(directory);
        while (pending.TryPop(out var current))
        {
            foreach (var entry in current.EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (entry is DirectoryInfo childDirectory)
                {
                    SetLowIntegrityLabel(childDirectory.FullName, isDirectory: true);
                    var childSecurity = childDirectory.GetAccessControl(AccessControlSections.Access);
                    childSecurity.AddAccessRule(rule);
                    childDirectory.SetAccessControl(childSecurity);
                    pending.Push(childDirectory);
                }
                else if (entry is FileInfo childFile)
                {
                    SetLowIntegrityLabel(childFile.FullName, isDirectory: false);
                    var fileSecurity = childFile.GetAccessControl(AccessControlSections.Access);
                    fileSecurity.AddAccessRule(new FileSystemAccessRule(
                        sid, FileSystemRights.Modify | FileSystemRights.ReadAndExecute, AccessControlType.Allow));
                    childFile.SetAccessControl(fileSecurity);
                }
            }
        }
    }

    private static bool HasModifyRule(FileSystemSecurity security, SecurityIdentifier sid) =>
        security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().Any(existing =>
            existing.IdentityReference == sid && existing.AccessControlType == AccessControlType.Allow &&
            (existing.FileSystemRights & FileSystemRights.Modify) == FileSystemRights.Modify);

    private static void SetLowIntegrityLabel(string path, bool isDirectory)
    {
        var sddl = isDirectory ? "S:(ML;OICI;NW;;;LW)" : "S:(ML;;NW;;;LW)";
        if (!NativeMethods.ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, out var descriptor, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            if (!NativeMethods.SetFileSecurity(path, NativeMethods.LabelSecurityInformation, descriptor))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { NativeMethods.LocalFree(descriptor); }
    }

    private static async Task<SandboxCommandResult> LaunchAsync(
        SecurityIdentifier appContainerSid,
        SandboxCommand command,
        CancellationToken cancellationToken)
    {
        using var stdout = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        using var stderr = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        using var stdin = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        var attributeListSize = IntPtr.Zero;
        NativeMethods.InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref attributeListSize);
        var attributeList = Marshal.AllocHGlobal(attributeListSize);
        var capabilitiesPointer = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.SecurityCapabilities>());
        var inheritedHandlesPointer = Marshal.AllocHGlobal(IntPtr.Size * 3);
        var sidBytes = new byte[appContainerSid.BinaryLength];
        appContainerSid.GetBinaryForm(sidBytes, 0);
        var appContainerSidPointer = Marshal.AllocHGlobal(sidBytes.Length);
        Marshal.Copy(sidBytes, 0, appContainerSidPointer, sidBytes.Length);
        IntPtr capabilitySid = IntPtr.Zero;
        IntPtr capabilityArray = IntPtr.Zero;
        IntPtr processHandle = IntPtr.Zero;
        IntPtr threadHandle = IntPtr.Zero;
        IntPtr jobHandle = IntPtr.Zero;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            if (!NativeMethods.InitializeProcThreadAttributeList(attributeList, 2, 0, ref attributeListSize))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var capabilities = new NativeMethods.SecurityCapabilities { AppContainerSid = appContainerSidPointer };
            if (command.AllowNetwork)
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
                    attributeList, 0, (IntPtr)NativeMethods.ProcThreadAttributeSecurityCapabilities,
                    capabilitiesPointer, (IntPtr)Marshal.SizeOf<NativeMethods.SecurityCapabilities>(), IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            Marshal.WriteIntPtr(inheritedHandlesPointer, 0, stdin.ClientSafePipeHandle.DangerousGetHandle());
            Marshal.WriteIntPtr(inheritedHandlesPointer, IntPtr.Size, stdout.ClientSafePipeHandle.DangerousGetHandle());
            Marshal.WriteIntPtr(inheritedHandlesPointer, IntPtr.Size * 2, stderr.ClientSafePipeHandle.DangerousGetHandle());
            if (!NativeMethods.UpdateProcThreadAttribute(
                    attributeList, 0, (IntPtr)NativeMethods.ProcThreadAttributeHandleList,
                    inheritedHandlesPointer, (IntPtr)(IntPtr.Size * 3), IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            var startup = new NativeMethods.StartupInfoEx
            {
                StartupInfo = new NativeMethods.StartupInfo
                {
                    Cb = Marshal.SizeOf<NativeMethods.StartupInfoEx>(),
                    Flags = NativeMethods.StartfUseStdHandles,
                    StandardInput = stdin.ClientSafePipeHandle.DangerousGetHandle(),
                    StandardOutput = stdout.ClientSafePipeHandle.DangerousGetHandle(),
                    StandardError = stderr.ClientSafePipeHandle.DangerousGetHandle()
                },
                AttributeList = attributeList
            };
            var commandLine = new StringBuilder($"\"{command.FileName}\" {command.Arguments}");
            if (!NativeMethods.CreateProcess(
                    command.FileName, commandLine, IntPtr.Zero, IntPtr.Zero, true,
                    NativeMethods.ExtendedStartupInfoPresent | NativeMethods.CreateSuspended | NativeMethods.CreateNoWindow,
                    IntPtr.Zero, command.WorkingDirectory, ref startup, out var process))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            processHandle = process.Process;
            threadHandle = process.Thread;
            stdout.DisposeLocalCopyOfClientHandle();
            stderr.DisposeLocalCopyOfClientHandle();
            stdin.DisposeLocalCopyOfClientHandle();
            stdin.Dispose();

            jobHandle = CreateRestrictedJob();
            if (!NativeMethods.AssignProcessToJobObject(jobHandle, processHandle))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (NativeMethods.ResumeThread(threadHandle) == uint.MaxValue)
                throw new Win32Exception(Marshal.GetLastWin32Error());

            var stdoutTask = DrainAsync(stdout, command.MaximumOutputCharacters);
            var stderrTask = DrainAsync(stderr, command.MaximumOutputCharacters);
            using var timeout = new CancellationTokenSource(command.Timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            try
            {
                await WaitForProcessAsync(processHandle, linked.Token);
            }
            catch (OperationCanceledException)
            {
                NativeMethods.TerminateJobObject(jobHandle, 1);
                await WaitForProcessAsync(processHandle, CancellationToken.None);
                if (cancellationToken.IsCancellationRequested) throw;
                throw new TimeoutException($"命令执行超过 {command.Timeout.TotalSeconds:0.#} 秒，进程树已终止。");
            }
            if (!NativeMethods.GetExitCodeProcess(processHandle, out var exitCode))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var output = await stdoutTask;
            var error = await stderrTask;
            stopwatch.Stop();
            return new SandboxCommandResult((int)exitCode, output.Text, error.Text, output.Truncated || error.Truncated, stopwatch.Elapsed);
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
            Marshal.FreeHGlobal(inheritedHandlesPointer);
            Marshal.FreeHGlobal(appContainerSidPointer);
            if (capabilityArray != IntPtr.Zero) Marshal.FreeHGlobal(capabilityArray);
            if (capabilitySid != IntPtr.Zero) NativeMethods.LocalFree(capabilitySid);
        }
    }

    private static IntPtr CreateRestrictedJob()
    {
        var job = NativeMethods.CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        var limits = new NativeMethods.JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new NativeMethods.JobObjectBasicLimitInformation
            {
                LimitFlags = NativeMethods.JobObjectLimitKillOnJobClose |
                             NativeMethods.JobObjectLimitActiveProcess |
                             NativeMethods.JobObjectLimitProcessMemory |
                             NativeMethods.JobObjectLimitJobMemory,
                ActiveProcessLimit = 32
            },
            ProcessMemoryLimit = (UIntPtr)(1024UL * 1024 * 1024),
            JobMemoryLimit = (UIntPtr)(2UL * 1024 * 1024 * 1024)
        };
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.JobObjectExtendedLimitInformation>());
        try
        {
            Marshal.StructureToPtr(limits, pointer, false);
            if (!NativeMethods.SetInformationJobObject(job, 9, pointer, (uint)Marshal.SizeOf<NativeMethods.JobObjectExtendedLimitInformation>()))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return job;
        }
        catch
        {
            NativeMethods.CloseHandle(job);
            throw;
        }
        finally { Marshal.FreeHGlobal(pointer); }
    }

    private static async Task WaitForProcessAsync(IntPtr processHandle, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = NativeMethods.WaitForSingleObject(processHandle, 100);
            if (result == 0) return;
            if (result != NativeMethods.WaitTimeout) throw new Win32Exception(Marshal.GetLastWin32Error());
            await Task.Yield();
        }
    }

    private static async Task<(string Text, bool Truncated)> DrainAsync(Stream stream, int maximumCharacters)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, leaveOpen: true);
        var builder = new StringBuilder(Math.Min(maximumCharacters, 8192));
        var buffer = new char[4096];
        var truncated = false;
        while (true)
        {
            var count = await reader.ReadAsync(buffer);
            if (count == 0) break;
            var remaining = maximumCharacters - builder.Length;
            if (remaining > 0) builder.Append(buffer, 0, Math.Min(count, remaining));
            if (count > remaining) truncated = true;
        }
        return (builder.ToString(), truncated);
    }

    private static string ProfileName(string conversationId) => $"FoloToy.CatAssistant.Work.{conversationId}";
    private static void ValidateConversationId(string conversationId)
    {
        if (!Guid.TryParseExact(conversationId, "N", out _))
            throw new ArgumentException("Work 对话 ID 无效。", nameof(conversationId));
    }

    public void Dispose()
    {
        _disposed = true;
        _profiles.Clear();
    }

    private static class NativeMethods
    {
        internal const long ProcThreadAttributeSecurityCapabilities = 0x00020009;
        internal const long ProcThreadAttributeHandleList = 0x00020002;
        internal const uint ExtendedStartupInfoPresent = 0x00080000;
        internal const uint CreateSuspended = 0x00000004;
        internal const uint CreateNoWindow = 0x08000000;
        internal const uint StartfUseStdHandles = 0x00000100;
        internal const uint JobObjectLimitActiveProcess = 0x00000008;
        internal const uint JobObjectLimitProcessMemory = 0x00000100;
        internal const uint JobObjectLimitJobMemory = 0x00000200;
        internal const uint JobObjectLimitKillOnJobClose = 0x00002000;
        internal const uint WaitTimeout = 0x00000102;
        internal const uint LabelSecurityInformation = 0x00000010;

        [StructLayout(LayoutKind.Sequential)] internal struct SecurityCapabilities { public IntPtr AppContainerSid; public IntPtr Capabilities; public uint CapabilityCount; public uint Reserved; }
        [StructLayout(LayoutKind.Sequential)] internal struct SidAndAttributes { public IntPtr Sid; public uint Attributes; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct StartupInfo
        {
            public int Cb; public string? Reserved; public string? Desktop; public string? Title;
            public uint X; public uint Y; public uint XSize; public uint YSize; public uint XCountChars; public uint YCountChars;
            public uint FillAttribute; public uint Flags; public ushort ShowWindow; public ushort Reserved2; public IntPtr Reserved2Pointer;
            public IntPtr StandardInput; public IntPtr StandardOutput; public IntPtr StandardError;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct StartupInfoEx { public StartupInfo StartupInfo; public IntPtr AttributeList; }
        [StructLayout(LayoutKind.Sequential)] internal struct ProcessInformation { public IntPtr Process; public IntPtr Thread; public uint ProcessId; public uint ThreadId; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct JobObjectBasicLimitInformation
        {
            public long PerProcessUserTimeLimit; public long PerJobUserTimeLimit; public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize; public UIntPtr MaximumWorkingSetSize; public uint ActiveProcessLimit;
            public UIntPtr Affinity; public uint PriorityClass; public uint SchedulingClass;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct IoCounters
        {
            public ulong ReadOperationCount; public ulong WriteOperationCount; public ulong OtherOperationCount;
            public ulong ReadTransferCount; public ulong WriteTransferCount; public ulong OtherTransferCount;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct JobObjectExtendedLimitInformation
        {
            public JobObjectBasicLimitInformation BasicLimitInformation; public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit; public UIntPtr JobMemoryLimit; public UIntPtr PeakProcessMemoryUsed; public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("userenv.dll", CharSet = CharSet.Unicode)] internal static extern int CreateAppContainerProfile(string appContainerName, string displayName, string description, IntPtr capabilities, uint capabilityCount, out IntPtr appContainerSid);
        [DllImport("userenv.dll", CharSet = CharSet.Unicode)] internal static extern int DeleteAppContainerProfile(string appContainerName);
        [DllImport("userenv.dll", CharSet = CharSet.Unicode)] internal static extern int DeriveAppContainerSidFromAppContainerName(string appContainerName, out IntPtr appContainerSid);
        [DllImport("userenv.dll", CharSet = CharSet.Unicode)] internal static extern int GetAppContainerFolderPath(string appContainerSid, out IntPtr path);
        [DllImport("advapi32.dll")] internal static extern IntPtr FreeSid(IntPtr sid);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ConvertStringSidToSid(string stringSid, out IntPtr sid);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string stringSecurityDescriptor, uint stringSdRevision, out IntPtr securityDescriptor, out uint securityDescriptorSize);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetFileSecurity(string fileName, uint securityInformation, IntPtr securityDescriptor);
        [DllImport("kernel32.dll")] internal static extern IntPtr LocalFree(IntPtr memory);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool InitializeProcThreadAttributeList(IntPtr attributeList, int attributeCount, int flags, ref IntPtr size);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UpdateProcThreadAttribute(IntPtr attributeList, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previousValue, IntPtr returnSize);
        [DllImport("kernel32.dll")] internal static extern void DeleteProcThreadAttributeList(IntPtr attributeList);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateProcess(string? applicationName, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags, IntPtr environment, string currentDirectory,
            ref StartupInfoEx startupInfo, out ProcessInformation processInformation);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetInformationJobObject(IntPtr job, int informationClass, IntPtr information, uint informationLength);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool TerminateJobObject(IntPtr job, uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CloseHandle(IntPtr handle);
    }
}
