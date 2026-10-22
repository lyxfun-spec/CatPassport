using System.Text.Json;
using CatAssistant.Backend.Services.Sandbox;
using CatAssistant.Backend.Services.Work;

namespace CatAssistant.Backend.Services.WorkTools;

public sealed class DeliverFilesTool(SqliteRepository repository) : IWorkTool
{
    public WorkToolDefinition Definition { get; } = new(
        "deliver_files",
        "Deliver existing regular files from the outputs directory to the user as file capsules.",
        """
        {"type":"object","properties":{"files":{"type":"array","minItems":1,"items":{"type":"object","properties":{"path":{"type":"string","description":"Relative or absolute path under outputs"},"label":{"type":"string"}},"required":["path"],"additionalProperties":false}}},"required":["files"],"additionalProperties":false}
        """);

    public async Task<WorkToolResult> ExecuteAsync(WorkToolExecutionContext context, string argumentsJson, CancellationToken cancellationToken)
    {
        var arguments = WorkToolArguments.Parse<Arguments>(argumentsJson);
        if (arguments.Files is null || arguments.Files.Count == 0) return WorkToolResult.Error("invalid_arguments", "至少需要一个成果文件。");
        var outputs = Path.Combine(context.WorkspaceRoot, "outputs");
        var delivered = new List<object>();
        foreach (var item in arguments.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.GetFullPath(Path.IsPathRooted(item.Path) ? item.Path : Path.Combine(outputs, item.Path));
            try { WindowsSandboxController.EnsureContained(outputs, path); }
            catch (UnauthorizedAccessException) { return WorkToolResult.Error("invalid_artifact", "只能交付 outputs 目录内的文件。"); }
            var file = new FileInfo(path);
            if (!file.Exists || (file.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                return WorkToolResult.Error("invalid_artifact", $"成果文件不存在或不是普通文件：{item.Path}");
            var label = string.IsNullOrWhiteSpace(item.Label) ? file.Name : item.Label.Trim();
            var artifact = await repository.AddWorkArtifactAsync(context.ConversationId, context.RunId, path, label);
            delivered.Add(new { artifact.Id, artifact.Path, artifact.Label, bytes = file.Length });
        }
        return WorkToolResult.Ok(new { files = delivered });
    }

    private sealed record Arguments(IReadOnlyList<FileArgument> Files);
    private sealed record FileArgument(string Path, string? Label);
}
