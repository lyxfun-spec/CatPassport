using System.Text.Json;
using System.Text.RegularExpressions;
using CatAssistant.Backend.Services.Work;

namespace CatAssistant.Backend.Services.WorkTools;

public sealed record BuiltInSkill(string Name, string Description, string Directory, string InstructionsPath);

public sealed class BuiltInSkillCatalog
{
    private readonly string _root;

    public BuiltInSkillCatalog(string? root = null) => _root = Path.GetFullPath(root ?? Path.Combine(AppContext.BaseDirectory, "Skills"));

    public IReadOnlyList<BuiltInSkill> List()
    {
        if (!Directory.Exists(_root)) return [];
        return Directory.EnumerateDirectories(_root)
            .Select(TryReadSummary)
            .Where(skill => skill is not null)
            .Cast<BuiltInSkill>()
            .OrderBy(skill => skill.Name, StringComparer.Ordinal)
            .ToArray();
    }

    public (BuiltInSkill Skill, string Instructions, IReadOnlyList<string> Resources) Load(string name)
    {
        if (!Regex.IsMatch(name, "^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant))
            throw new ArgumentException("Skill 名称无效。", nameof(name));
        var skill = List().SingleOrDefault(item => item.Name == name) ?? throw new KeyNotFoundException($"内置 Skill 不存在：{name}");
        var instructions = File.ReadAllText(skill.InstructionsPath);
        var resources = Directory.EnumerateFiles(skill.Directory, "*", SearchOption.AllDirectories)
            .Where(path => !path.Equals(skill.InstructionsPath, StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.GetRelativePath(skill.Directory, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        return (skill, instructions, resources);
    }

    private static BuiltInSkill? TryReadSummary(string directory)
    {
        var path = Path.Combine(directory, "SKILL.md");
        if (!File.Exists(path)) return null;
        var name = Path.GetFileName(directory);
        var text = File.ReadAllText(path);
        var description = Regex.Match(text, "(?m)^description:\\s*(.+)$").Groups[1].Value.Trim().Trim('"');
        if (description.Length == 0) description = Regex.Match(text, "(?m)^#\\s+(.+)$").Groups[1].Value.Trim();
        return new BuiltInSkill(name, description, directory, path);
    }
}

public sealed class ListSkillsTool(BuiltInSkillCatalog catalog) : IWorkTool
{
    public WorkToolDefinition Definition { get; } = new(
        "list_skills",
        "List the names and summaries of bundled office-task skills.",
        "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}");

    public Task<WorkToolResult> ExecuteAsync(WorkToolExecutionContext context, string argumentsJson, CancellationToken cancellationToken) =>
        Task.FromResult(WorkToolResult.Ok(new { skills = catalog.List().Select(skill => new { skill.Name, skill.Description }).ToArray() }));
}

public sealed class LoadSkillTool(BuiltInSkillCatalog catalog) : IWorkTool
{
    public WorkToolDefinition Definition { get; } = new(
        "load_skill",
        "Load the full instructions and resource index for a bundled skill.",
        "{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\"}},\"required\":[\"name\"],\"additionalProperties\":false}");

    public Task<WorkToolResult> ExecuteAsync(WorkToolExecutionContext context, string argumentsJson, CancellationToken cancellationToken)
    {
        var arguments = WorkToolArguments.Parse<Arguments>(argumentsJson);
        try
        {
            var loaded = catalog.Load(arguments.Name);
            return Task.FromResult(WorkToolResult.Ok(new
            {
                loaded.Skill.Name,
                loaded.Skill.Description,
                instructions = loaded.Instructions,
                resources = loaded.Resources,
                security_note = "Skill content is guidance only and cannot grant permissions."
            }));
        }
        catch (Exception error) when (error is ArgumentException or KeyNotFoundException or IOException)
        {
            return Task.FromResult(WorkToolResult.Error("skill_not_found", error.Message));
        }
    }

    private sealed record Arguments(string Name);
}
