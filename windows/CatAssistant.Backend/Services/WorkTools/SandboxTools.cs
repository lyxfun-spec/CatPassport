using System.Text.Json;
using CatAssistant.Backend.Services.Sandbox;
using CatAssistant.Backend.Services.Work;

namespace CatAssistant.Backend.Services.WorkTools;

public sealed class ExecuteCommandTool(WindowsSandboxController sandbox) : IWorkTool
{
    public WorkToolDefinition Definition { get; } = new(
        "execute_command",
        "Run a PowerShell or cmd command inside the current conversation's Windows OS sandbox. The process and all children can access only this workspace and may access the network.",
        """
        {"type":"object","properties":{"command":{"type":"string"},"shell":{"type":"string","enum":["powershell","cmd"]},"working_directory":{"type":"string","description":"Workspace-relative directory; defaults to work"},"timeout_seconds":{"type":"integer","minimum":1,"maximum":3600}},"required":["command"],"additionalProperties":false}
        """);

    public async Task<WorkToolResult> ExecuteAsync(WorkToolExecutionContext context, string argumentsJson, CancellationToken cancellationToken)
    {
        var arguments = WorkToolArguments.Parse<Arguments>(argumentsJson);
        if (string.IsNullOrWhiteSpace(arguments.Command)) return WorkToolResult.Error("invalid_arguments", "command 不能为空。");
        var workingDirectory = WorkspaceToolPaths.Resolve(context.WorkspaceRoot, arguments.WorkingDirectory ?? "work");
        if (!Directory.Exists(workingDirectory)) return WorkToolResult.Error("directory_not_found", "工作目录不存在。");
        var timeoutSeconds = Math.Clamp(arguments.TimeoutSeconds ?? 300, 1, 3600);
        var useCmd = string.Equals(arguments.Shell, "cmd", StringComparison.OrdinalIgnoreCase);
        if (arguments.Shell is not null && !useCmd && !string.Equals(arguments.Shell, "powershell", StringComparison.OrdinalIgnoreCase))
            return WorkToolResult.Error("invalid_arguments", "shell 只能是 powershell 或 cmd。");
        var executable = useCmd
            ? Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe"
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        var processArguments = useCmd
            ? $"/d /q /c chcp 65001>nul & {arguments.Command}"
            : $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command [Console]::OutputEncoding=[Text.Encoding]::UTF8; {arguments.Command}";
        try
        {
            var result = await sandbox.ExecuteAsync(
                context.ConversationId,
                context.WorkspaceRoot,
                new SandboxCommand(executable, processArguments, workingDirectory, TimeSpan.FromSeconds(timeoutSeconds)),
                cancellationToken);
            var payload = new
            {
                exit_code = result.ExitCode,
                stdout = result.StandardOutput,
                stderr = result.StandardError,
                output_truncated = result.OutputTruncated,
                duration_ms = (long)result.Duration.TotalMilliseconds
            };
            return result.ExitCode == 0
                ? WorkToolResult.Ok(payload)
                : new WorkToolResult(false, JsonSerializer.Serialize(payload), "command_failed", $"命令退出码为 {result.ExitCode}。");
        }
        catch (TimeoutException error) { return WorkToolResult.Error("command_timeout", error.Message); }
        catch (UnauthorizedAccessException error) { return WorkToolResult.Error("access_denied", error.Message); }
    }

    private sealed record Arguments(string Command, string? Shell, string? WorkingDirectory, int? TimeoutSeconds);
}

public sealed class ReadFileTool(WindowsSandboxController sandbox) : IWorkTool
{
    public WorkToolDefinition Definition { get; } = new(
        "read_file",
        "Read a UTF-8 text file through the current conversation's OS sandbox.",
        "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}},\"required\":[\"path\"],\"additionalProperties\":false}");

    public async Task<WorkToolResult> ExecuteAsync(WorkToolExecutionContext context, string argumentsJson, CancellationToken cancellationToken)
    {
        var arguments = WorkToolArguments.Parse<PathArguments>(argumentsJson);
        var path = WorkspaceToolPaths.Resolve(context.WorkspaceRoot, arguments.Path);
        var command = $"[Console]::OutputEncoding=[Text.Encoding]::UTF8; [Console]::Write([IO.File]::ReadAllText('{PowerShellLiteral(path)}'))";
        return await RunPowerShellAsync(sandbox, context, command, cancellationToken, result => new { path = arguments.Path, content = result.StandardOutput });
    }

    internal static string PowerShellLiteral(string value) => value.Replace("'", "''", StringComparison.Ordinal);
    private sealed record PathArguments(string Path);

    internal static async Task<WorkToolResult> RunPowerShellAsync(
        WindowsSandboxController sandbox,
        WorkToolExecutionContext context,
        string command,
        CancellationToken cancellationToken,
        Func<SandboxCommandResult, object> success)
    {
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        var result = await sandbox.ExecuteAsync(context.ConversationId, context.WorkspaceRoot,
            new SandboxCommand(executable, $"-NoLogo -NoProfile -NonInteractive -Command \"$ErrorActionPreference='Stop'; {command}\"", context.WorkspaceRoot, TimeSpan.FromMinutes(2)), cancellationToken);
        return result.ExitCode == 0
            ? WorkToolResult.Ok(success(result))
            : new WorkToolResult(false, JsonSerializer.Serialize(new { stderr = result.StandardError, stdout = result.StandardOutput }), "file_operation_failed", result.StandardError.Trim());
    }
}

public sealed class WriteFileTool(WindowsSandboxController sandbox) : IWorkTool
{
    public WorkToolDefinition Definition { get; } = new(
        "write_file",
        "Create or replace a UTF-8 text file through the current conversation's OS sandbox.",
        "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"},\"content\":{\"type\":\"string\"}},\"required\":[\"path\",\"content\"],\"additionalProperties\":false}");

    public async Task<WorkToolResult> ExecuteAsync(WorkToolExecutionContext context, string argumentsJson, CancellationToken cancellationToken)
    {
        var arguments = WorkToolArguments.Parse<Arguments>(argumentsJson);
        var destination = WorkspaceToolPaths.Resolve(context.WorkspaceRoot, arguments.Path);
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

    private sealed record Arguments(string Path, string Content);
}

public sealed class ListDirectoryTool(WindowsSandboxController sandbox) : IWorkTool
{
    public WorkToolDefinition Definition { get; } = new(
        "list_directory",
        "List files and directories through the current conversation's OS sandbox.",
        "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}},\"additionalProperties\":false}");

    public async Task<WorkToolResult> ExecuteAsync(WorkToolExecutionContext context, string argumentsJson, CancellationToken cancellationToken)
    {
        var arguments = WorkToolArguments.Parse<Arguments>(argumentsJson);
        var path = WorkspaceToolPaths.Resolve(context.WorkspaceRoot, arguments.Path ?? ".");
        var command = $"Get-ChildItem -LiteralPath '{ReadFileTool.PowerShellLiteral(path)}' -Force | Select-Object Name,Length,Attributes,LastWriteTimeUtc | ConvertTo-Json -Compress";
        return await ReadFileTool.RunPowerShellAsync(sandbox, context, command, cancellationToken,
            result => new { path = arguments.Path ?? ".", entries_json = string.IsNullOrWhiteSpace(result.StandardOutput) ? "[]" : result.StandardOutput.Trim() });
    }

    private sealed record Arguments(string? Path);
}

internal static class WorkspaceToolPaths
{
    internal static string Resolve(string workspaceRoot, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("path 不能为空。");
        var candidate = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(workspaceRoot, path));
        WindowsSandboxController.EnsureContained(workspaceRoot, candidate);
        return candidate;
    }
}

internal static class WorkToolArguments
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    internal static T Parse<T>(string json) where T : class =>
        JsonSerializer.Deserialize<T>(json, Options) ?? throw new ArgumentException("工具参数无效。");
}
