using System.Collections.Concurrent;
using System.Text.Json;

namespace CatAssistant.Backend.Services.Work;

public sealed record WorkToolResult(bool Success, string OutputJson, string? ErrorCode = null, string? ErrorMessage = null)
{
    public static WorkToolResult Ok(object value) => new(true, JsonSerializer.Serialize(value));
    public static WorkToolResult Error(string code, string message) => new(false, JsonSerializer.Serialize(new { error = message }), code, message);
}

public sealed record WorkToolExecutionContext(string ConversationId, string RunId, string WorkspaceRoot);

public interface IWorkTool
{
    WorkToolDefinition Definition { get; }
    Task<WorkToolResult> ExecuteAsync(WorkToolExecutionContext context, string argumentsJson, CancellationToken cancellationToken);
}

public sealed class WorkToolRegistry
{
    private readonly ConcurrentDictionary<string, IWorkTool> _tools = new(StringComparer.Ordinal);

    public IReadOnlyList<WorkToolDefinition> Definitions => _tools.Values
        .Select(tool => tool.Definition)
        .OrderBy(tool => tool.Name, StringComparer.Ordinal)
        .ToArray();

    public void Register(IWorkTool tool)
    {
        if (!_tools.TryAdd(tool.Definition.Name, tool))
            throw new InvalidOperationException($"工具 {tool.Definition.Name} 已注册。");
    }

    public Task<WorkToolResult> ExecuteAsync(
        string name,
        WorkToolExecutionContext context,
        string argumentsJson,
        CancellationToken cancellationToken)
    {
        if (!_tools.TryGetValue(name, out var tool))
            return Task.FromResult(WorkToolResult.Error("unknown_tool", $"未知工具：{name}"));
        try { using var _ = JsonDocument.Parse(argumentsJson); }
        catch (JsonException) { return Task.FromResult(WorkToolResult.Error("invalid_arguments", "工具参数不是有效 JSON。")); }
        return tool.ExecuteAsync(context, argumentsJson, cancellationToken);
    }
}
