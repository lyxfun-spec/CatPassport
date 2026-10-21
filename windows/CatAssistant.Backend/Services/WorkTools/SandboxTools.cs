using System.Text.Json;
using CatAssistant.Backend.Services.Sandbox;
using CatAssistant.Backend.Services.Work;

namespace CatAssistant.Backend.Services.WorkTools;

public sealed class ExecuteCommandTool(WindowsSandboxController sandbox, WorkApprovalService? approvals = null) : IWorkTool
{
    public WorkToolDefinition Definition { get; } = new(
        "execute_command",
        "Run a PowerShell or cmd command inside the current conversation's Windows OS sandbox. The process and all children can access only this workspace and may access the network. Commands start at the workspace root by default. Use PowerShell for PowerShell-native operations and cmd for commands that launch native programs. The environment prepares PATH, PYTHONPATH and PIP_TARGET, so standard python and pip commands in cmd use the bundled read-only runtime and install extras into this conversation's work/python-packages directory.",
        """
        {"type":"object","properties":{"command":{"type":"string"},"shell":{"type":"string","enum":["powershell","cmd"]},"working_directory":{"type":"string","description":"Workspace-relative directory; defaults to the workspace root"},"timeout_seconds":{"type":"integer","minimum":1,"maximum":3600},"outside_sandbox":{"type":"boolean","description":"Request explicit approval to run this exact command outside the sandbox"}},"required":["command"],"additionalProperties":false}
        """);

    public async Task<WorkToolResult> ExecuteAsync(WorkToolExecutionContext context, string argumentsJson, CancellationToken cancellationToken)
    {
        var arguments = WorkToolArguments.Parse<Arguments>(argumentsJson);
        if (string.IsNullOrWhiteSpace(arguments.Command)) return WorkToolResult.Error("invalid_arguments", "command 不能为空。");
        var workingDirectory = WorkspaceToolPaths.FullPath(context.WorkspaceRoot, arguments.WorkingDirectory ?? ".");
        if (!Directory.Exists(workingDirectory)) return WorkToolResult.Error("directory_not_found", "工作目录不存在。");
        var timeoutSeconds = Math.Clamp(arguments.TimeoutSeconds ?? 300, 1, 3600);
        var useCmd = string.Equals(arguments.Shell, "cmd", StringComparison.OrdinalIgnoreCase);
        if (arguments.Shell is not null && !useCmd && !string.Equals(arguments.Shell, "powershell", StringComparison.OrdinalIgnoreCase))
            return WorkToolResult.Error("invalid_arguments", "shell 只能是 powershell 或 cmd。");
        var executable = useCmd
            ? Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe"
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        var processArguments = useCmd
            ? $"/d /q /c {BuildCmdPrelude(context.WorkspaceRoot, workingDirectory)} {arguments.Command}"
            : $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes($"{BuildPowerShellPrelude(context.WorkspaceRoot, workingDirectory)} {arguments.Command}; if ($LASTEXITCODE -ne $null -and $LASTEXITCODE -ne 0) {{ exit $LASTEXITCODE }}"))}";
        try
        {
            var command = new SandboxCommand(executable, processArguments, workingDirectory, TimeSpan.FromSeconds(timeoutSeconds));
            SandboxCommandResult result;
            if (arguments.OutsideSandbox == true || !WorkspaceToolPaths.IsInside(context.WorkspaceRoot, workingDirectory))
            {
                if (approvals is null) return WorkToolResult.Error("approval_unavailable", "外部命令需要用户审批。");
                var approval = await approvals.RequestAsync(context, argumentsJson, "single", workingDirectory, "execute", cancellationToken);
                if (!approval.Approved) return WorkToolResult.Error("permission_rejected", "用户拒绝了外部命令执行请求。");
                result = await sandbox.ExecuteApprovedExternalAsync(command, cancellationToken);
            }
            else
            {
                result = await sandbox.ExecuteAsync(context.ConversationId, context.WorkspaceRoot, command, cancellationToken);
            }
            var completeStdout = await PreserveCompleteOutputAsync(context, result.CompleteStandardOutputPath, "stdout", cancellationToken);
            var completeStderr = await PreserveCompleteOutputAsync(context, result.CompleteStandardErrorPath, "stderr", cancellationToken);
            var payload = new
            {
                exit_code = result.ExitCode,
                stdout = result.StandardOutput,
                stderr = result.StandardError,
                output_truncated = result.OutputTruncated,
                complete_stdout_path = completeStdout,
                complete_stderr_path = completeStderr,
                duration_ms = (long)result.Duration.TotalMilliseconds
            };
            return result.ExitCode == 0
                ? WorkToolResult.Ok(payload)
                : new WorkToolResult(false, JsonSerializer.Serialize(payload), "command_failed", $"命令退出码为 {result.ExitCode}。");
        }
        catch (TimeoutException error) { return WorkToolResult.Error("command_timeout", error.Message); }
        catch (UnauthorizedAccessException error) { return WorkToolResult.Error("access_denied", error.Message); }
    }

    private static async Task<string?> PreserveCompleteOutputAsync(
        WorkToolExecutionContext context,
        string? temporaryPath,
        string streamName,
        CancellationToken cancellationToken)
    {
        if (temporaryPath is null) return null;
        var directory = Path.Combine(context.WorkspaceRoot, "work", ".context", "tool-results");
        Directory.CreateDirectory(directory);
        var safeId = string.Concat($"{context.RunId}-{context.ToolCallId}".Select(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' ? character : '_'));
        var fileName = $"{safeId}-{streamName}.log";
        var destination = Path.Combine(directory, fileName);
        var staging = destination + ".tmp";
        try
        {
            await using (var input = new FileStream(temporaryPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(staging, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await input.CopyToAsync(output, cancellationToken);
            File.Move(staging, destination, true);
            return Path.GetRelativePath(context.WorkspaceRoot, destination).Replace('\\', '/');
        }
        finally
        {
            try { File.Delete(temporaryPath); } catch { }
            try { File.Delete(staging); } catch { }
        }
    }

    internal static string BuildPowerShellPrelude(string workspaceRoot, string workingDirectory)
    {
        var packages = Path.Combine(workspaceRoot, "work", "python-packages");
        Directory.CreateDirectory(packages);
        var python = Environment.GetEnvironmentVariable("CATASSISTANT_PYTHON");
        var pathPrefix = string.IsNullOrWhiteSpace(python)
            ? ""
            : $"$env:CATASSISTANT_PYTHON='{ReadFileTool.PowerShellLiteral(python)}'; $env:PATH='{ReadFileTool.PowerShellLiteral(Path.GetDirectoryName(python)!)};{ReadFileTool.PowerShellLiteral(Path.Combine(Path.GetDirectoryName(python)!, "Scripts"))};' + $env:PATH; ";
        return $"[Console]::OutputEncoding=[Text.Encoding]::UTF8; $ErrorActionPreference='Stop'; $env:CATASSISTANT_WORKSPACE='{ReadFileTool.PowerShellLiteral(workspaceRoot)}'; $env:CATASSISTANT_WORKING_DIRECTORY='{ReadFileTool.PowerShellLiteral(workingDirectory)}'; {pathPrefix}$env:PYTHONPATH='{ReadFileTool.PowerShellLiteral(packages)}'; $env:PIP_TARGET='{ReadFileTool.PowerShellLiteral(packages)}'; $env:PIP_CONFIG_FILE='NUL'; $env:PIP_DISABLE_PIP_VERSION_CHECK='1'; $env:PYTHONNOUSERSITE='1'; $env:PYTHONUTF8='1';";
    }

    internal static string BuildCmdPrelude(string workspaceRoot, string workingDirectory)
    {
        var packages = Path.Combine(workspaceRoot, "work", "python-packages");
        Directory.CreateDirectory(packages);
        var python = Environment.GetEnvironmentVariable("CATASSISTANT_PYTHON");
        var pathPrefix = string.IsNullOrWhiteSpace(python)
            ? ""
            : $"set \"CATASSISTANT_PYTHON={python}\" & set \"PATH={Path.GetDirectoryName(python)};{Path.Combine(Path.GetDirectoryName(python)!, "Scripts")};%PATH%\" & ";
        return $"chcp 65001>nul & set \"CATASSISTANT_WORKSPACE={workspaceRoot}\" & set \"CATASSISTANT_WORKING_DIRECTORY={workingDirectory}\" & {pathPrefix}set \"PYTHONPATH={packages}\" & set \"PIP_TARGET={packages}\" & set \"PIP_CONFIG_FILE=NUL\" & set \"PIP_DISABLE_PIP_VERSION_CHECK=1\" & set \"PYTHONNOUSERSITE=1\" & set \"PYTHONUTF8=1\" &";
    }

    private sealed record Arguments(string Command, string? Shell, string? WorkingDirectory, int? TimeoutSeconds, bool? OutsideSandbox);
}

public sealed class ReadFileTool(WindowsSandboxController sandbox, WorkApprovalService? approvals = null) : IWorkTool
{
    public WorkToolDefinition Definition { get; } = new(
        "read_file",
        "Read a text file through the current conversation's OS sandbox. Decodes UTF-8 (with or without BOM) and falls back to the system ANSI/OEM code page (e.g. GBK) for legacy Chinese files.",
        "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"},\"approval_scope\":{\"type\":\"string\",\"enum\":[\"single\",\"task_directory\"]}},\"required\":[\"path\"],\"additionalProperties\":false}");

    public async Task<WorkToolResult> ExecuteAsync(WorkToolExecutionContext context, string argumentsJson, CancellationToken cancellationToken)
    {
        var arguments = WorkToolArguments.Parse<PathArguments>(argumentsJson);
        var path = WorkspaceToolPaths.FullPath(context.WorkspaceRoot, arguments.Path);
        if (!WorkspaceToolPaths.IsInside(context.WorkspaceRoot, path))
        {
            try { ExternalPathPolicy.RejectReparsePoints(path); }
            catch (UnauthorizedAccessException error) { return WorkToolResult.Error("unsafe_external_path", error.Message); }
            if (approvals is null) return WorkToolResult.Error("approval_unavailable", "读取工作区外文件需要用户审批。");
            var approval = await approvals.RequestAsync(context, argumentsJson, Scope(arguments.ApprovalScope), path, "read", cancellationToken);
            if (!approval.Approved) return WorkToolResult.Error("permission_rejected", "用户拒绝了文件读取请求。");
            try
            {
                ExternalPathPolicy.RejectReparsePoints(path);
                var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
                return WorkToolResult.Ok(new { path = arguments.Path, content = SandboxText.Decode(bytes) });
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return WorkToolResult.Error("file_operation_failed", error.Message); }
        }
        // Emit the raw file bytes to stdout; the shared UTF-8→OEM decoder in DrainAsync handles
        // UTF-8 and GBK/ANSI files alike, so legacy Chinese text no longer comes back as mojibake.
        var command = $"$b=[IO.File]::ReadAllBytes('{PowerShellLiteral(path)}'); $s=[Console]::OpenStandardOutput(); $s.Write($b,0,$b.Length); $s.Flush()";
        return await RunPowerShellAsync(sandbox, context, command, cancellationToken, result => new { path = arguments.Path, content = result.StandardOutput });
    }

    internal static string PowerShellLiteral(string value) => value.Replace("'", "''", StringComparison.Ordinal);
    internal static string Scope(string? value) => value is null or "single" ? "single" : value == "task_directory" ? value : throw new ArgumentException("approval_scope 无效。");
    private sealed record PathArguments(string Path, string? ApprovalScope);

    internal static async Task<WorkToolResult> RunPowerShellAsync(
        WindowsSandboxController sandbox,
        WorkToolExecutionContext context,
        string command,
        CancellationToken cancellationToken,
        Func<SandboxCommandResult, object> success)
    {
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        // -EncodedCommand (Base64 UTF-16LE) passes Chinese paths/literals without relying on the
        // process code page, and the UTF-8 output preamble keeps list_directory/stderr from coming
        // back as GBK (which the UTF-8-first decoder would otherwise have to guess at).
        var script = $"[Console]::OutputEncoding=[Text.Encoding]::UTF8; $ErrorActionPreference='Stop'; {command}";
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        var result = await sandbox.ExecuteAsync(context.ConversationId, context.WorkspaceRoot,
            new SandboxCommand(executable, $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}", context.WorkspaceRoot, TimeSpan.FromMinutes(2)), cancellationToken);
        return result.ExitCode == 0
            ? WorkToolResult.Ok(success(result))
            : new WorkToolResult(false, JsonSerializer.Serialize(new { stderr = result.StandardError, stdout = result.StandardOutput }), "file_operation_failed", result.StandardError.Trim());
    }
}

public sealed class WriteFileTool(WindowsSandboxController sandbox, WorkApprovalService? approvals = null) : IWorkTool
{
    public WorkToolDefinition Definition { get; } = new(
        "write_file",
        "Create or replace a UTF-8 text file through the current conversation's OS sandbox.",
        "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"},\"content\":{\"type\":\"string\"},\"approval_scope\":{\"type\":\"string\",\"enum\":[\"single\",\"task_directory\"]}},\"required\":[\"path\",\"content\"],\"additionalProperties\":false}");

    public async Task<WorkToolResult> ExecuteAsync(WorkToolExecutionContext context, string argumentsJson, CancellationToken cancellationToken)
    {
        var arguments = WorkToolArguments.Parse<Arguments>(argumentsJson);
        var destination = WorkspaceToolPaths.FullPath(context.WorkspaceRoot, arguments.Path);
        if (!WorkspaceToolPaths.IsInside(context.WorkspaceRoot, destination))
        {
            try { ExternalPathPolicy.RejectReparsePoints(destination); }
            catch (UnauthorizedAccessException error) { return WorkToolResult.Error("unsafe_external_path", error.Message); }
            if (approvals is null) return WorkToolResult.Error("approval_unavailable", "写入工作区外文件需要用户审批。");
            var approval = await approvals.RequestAsync(context, argumentsJson, ReadFileTool.Scope(arguments.ApprovalScope), destination, "write", cancellationToken);
            if (!approval.Approved) return WorkToolResult.Error("permission_rejected", "用户拒绝了文件写入请求。");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? throw new ArgumentException("目标路径无效。"));
                ExternalPathPolicy.RejectReparsePoints(destination);
                await File.WriteAllTextAsync(destination, arguments.Content, cancellationToken);
                return WorkToolResult.Ok(new { path = arguments.Path, bytes = System.Text.Encoding.UTF8.GetByteCount(arguments.Content) });
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return WorkToolResult.Error("file_operation_failed", error.Message); }
        }
        sandbox.Prepare(context.ConversationId, context.WorkspaceRoot);
        var runtime = Path.Combine(context.WorkspaceRoot, ".runtime");
        Directory.CreateDirectory(runtime);
        var staging = Path.Combine(runtime, $"write-{Guid.NewGuid():N}.tmp");
        await File.WriteAllTextAsync(staging, arguments.Content, cancellationToken);
        try
        {
            var parent = Path.GetDirectoryName(destination) ?? context.WorkspaceRoot;
            var command = $"[IO.Directory]::CreateDirectory('{ReadFileTool.PowerShellLiteral(parent)}') | Out-Null; [IO.File]::Copy('{ReadFileTool.PowerShellLiteral(staging)}','{ReadFileTool.PowerShellLiteral(destination)}',$true)";
            return await ReadFileTool.RunPowerShellAsync(sandbox, context, command, cancellationToken,
                _ => new { path = arguments.Path, bytes = System.Text.Encoding.UTF8.GetByteCount(arguments.Content) });
        }
        finally { try { File.Delete(staging); } catch { } }
    }

    private sealed record Arguments(string Path, string Content, string? ApprovalScope);
}

public sealed class ListDirectoryTool(WindowsSandboxController sandbox, WorkApprovalService? approvals = null) : IWorkTool
{
    public WorkToolDefinition Definition { get; } = new(
        "list_directory",
        "List files and directories through the current conversation's OS sandbox.",
        "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"},\"approval_scope\":{\"type\":\"string\",\"enum\":[\"single\",\"task_directory\"]}},\"additionalProperties\":false}");

    public async Task<WorkToolResult> ExecuteAsync(WorkToolExecutionContext context, string argumentsJson, CancellationToken cancellationToken)
    {
        var arguments = WorkToolArguments.Parse<Arguments>(argumentsJson);
        var path = WorkspaceToolPaths.FullPath(context.WorkspaceRoot, arguments.Path ?? ".");
        if (!WorkspaceToolPaths.IsInside(context.WorkspaceRoot, path))
        {
            try { ExternalPathPolicy.RejectReparsePoints(path); }
            catch (UnauthorizedAccessException error) { return WorkToolResult.Error("unsafe_external_path", error.Message); }
            if (approvals is null) return WorkToolResult.Error("approval_unavailable", "列出工作区外目录需要用户审批。");
            var approval = await approvals.RequestAsync(context, argumentsJson, ReadFileTool.Scope(arguments.ApprovalScope), path, "read", cancellationToken);
            if (!approval.Approved) return WorkToolResult.Error("permission_rejected", "用户拒绝了目录读取请求。");
            try
            {
                ExternalPathPolicy.RejectReparsePoints(path);
                var entries = new DirectoryInfo(path).EnumerateFileSystemInfos().Select(entry => new
                {
                    entry.Name,
                    Length = entry is FileInfo file ? file.Length : (long?)null,
                    Attributes = entry.Attributes.ToString(),
                    entry.LastWriteTimeUtc
                }).ToArray();
                return WorkToolResult.Ok(new { path = arguments.Path ?? ".", entries });
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return WorkToolResult.Error("file_operation_failed", error.Message); }
        }
        var command = $"Get-ChildItem -LiteralPath '{ReadFileTool.PowerShellLiteral(path)}' -Force | Select-Object Name,Length,Attributes,LastWriteTimeUtc | ConvertTo-Json -Compress";
        return await ReadFileTool.RunPowerShellAsync(sandbox, context, command, cancellationToken,
            result => new { path = arguments.Path ?? ".", entries_json = string.IsNullOrWhiteSpace(result.StandardOutput) ? "[]" : result.StandardOutput.Trim() });
    }

    private sealed record Arguments(string? Path, string? ApprovalScope);
}

internal static class WorkspaceToolPaths
{
    internal static string FullPath(string workspaceRoot, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("path 不能为空。");
        return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(workspaceRoot, path));
    }

    internal static bool IsInside(string workspaceRoot, string path)
    {
        try { WindowsSandboxController.EnsureContained(workspaceRoot, path); return true; }
        catch (UnauthorizedAccessException) { return false; }
    }
}

internal static class WorkToolArguments
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    internal static T Parse<T>(string json) where T : class =>
        JsonSerializer.Deserialize<T>(json, Options) ?? throw new ArgumentException("工具参数无效。");
}

internal static class ExternalPathPolicy
{
    internal static void RejectReparsePoints(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath) ?? throw new ArgumentException("外部路径无效。", nameof(path));
        var relative = fullPath[root.Length..];
        var current = root;
        foreach (var segment in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current)) continue;
            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException($"为避免授权范围被重解析点绕过，拒绝访问：{current}");
        }
    }
}
