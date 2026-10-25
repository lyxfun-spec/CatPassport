using CatAssistant.Backend.Models;

namespace CatAssistant.Backend.Services.Work;

public sealed record WorkToolDefinition(string Name, string Description, string ParametersJson);

public sealed record WorkModelToolCall(string ToolCallId, string Name, string ArgumentsJson);

public sealed record WorkModelResponse(string? Content, IReadOnlyList<WorkModelToolCall> ToolCalls, ModelUsage? Usage = null, bool OutputTruncated = false);

public interface IWorkRequestBudgetProvider
{
    ContextBudgetAssessment AssessWorkRequest(
        IReadOnlyList<WorkContextItem> context,
        IReadOnlyList<WorkToolDefinition> tools,
        WorkEnvironmentInfo environment,
        string model);
}

public sealed record WorkEnvironmentInfo(
    string WorkspaceRoot,
    string InputsDirectory,
    string WorkDirectory,
    string OutputsDirectory,
    string PythonPackagesDirectory,
    string? BundledPythonPath)
{
    public static WorkEnvironmentInfo From(WorkWorkspacePaths workspace)
    {
        var packages = Path.Combine(workspace.Work, "python-packages");
        return new WorkEnvironmentInfo(
            workspace.Root,
            workspace.Inputs,
            workspace.Work,
            workspace.Outputs,
            packages,
            WorkRuntimeLocator.FindPython());
    }
}

public interface IWorkModelProvider
{
    Task<WorkModelResponse> CompleteAsync(
        IReadOnlyList<WorkContextItem> context,
        IReadOnlyList<WorkToolDefinition> tools,
        WorkEnvironmentInfo environment,
        string model,
        string apiKey,
        Func<string, Task> onContentDelta,
        CancellationToken cancellationToken);
}
