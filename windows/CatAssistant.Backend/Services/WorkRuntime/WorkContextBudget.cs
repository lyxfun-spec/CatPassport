using CatAssistant.Backend.Models;

namespace CatAssistant.Backend.Services.Work;

public sealed class WorkContextLimitException(int estimatedCharacters, int characterLimit)
    : Exception($"当前 Work 对话上下文约 {estimatedCharacters} 个字符，已达到第一版上限 {characterLimit}。完整历史仍已保留，请新建 Work 对话后继续；系统没有静默删除或自动摘要历史。")
{
    public int EstimatedCharacters { get; } = estimatedCharacters;
    public int CharacterLimit { get; } = characterLimit;
}

public static class WorkContextBudget
{
    public static int EstimateCharacters(IReadOnlyList<WorkContextItem> context, IReadOnlyList<WorkToolDefinition> tools)
    {
        var total = context.Sum(item =>
            item.PayloadJson.Length + (item.Role?.Length ?? 0) + (item.Name?.Length ?? 0) + (item.ToolCallId?.Length ?? 0) + 32);
        total += tools.Sum(tool => tool.Name.Length + tool.Description.Length + tool.ParametersJson.Length + 32);
        return total;
    }
}
