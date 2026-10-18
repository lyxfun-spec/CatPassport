using CatAssistant.Backend.Services.Work;
using CatAssistant.Backend.Services.WorkTools;
using Xunit;

namespace CatAssistant.Backend.Tests;

public sealed class SkillToolTests
{
    [Fact]
    public async Task SkillsAreDiscoveredBySummaryAndLoadedExplicitly()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cat-skill-tests-{Guid.NewGuid():N}");
        var skillDirectory = Path.Combine(root, "demo-skill");
        Directory.CreateDirectory(Path.Combine(skillDirectory, "scripts"));
        await File.WriteAllTextAsync(Path.Combine(skillDirectory, "SKILL.md"), "---\nname: demo-skill\ndescription: Demo description.\n---\n# Full instructions\nDo the work.");
        await File.WriteAllTextAsync(Path.Combine(skillDirectory, "scripts", "run.ps1"), "Write-Output ok");
        var catalog = new BuiltInSkillCatalog(root);
        var context = new WorkToolExecutionContext(Guid.NewGuid().ToString("N"), "run", root);
        try
        {
            var list = await new ListSkillsTool(catalog).ExecuteAsync(context, "{}", CancellationToken.None);
            Assert.True(list.Success);
            Assert.Contains("Demo description", list.OutputJson);
            Assert.DoesNotContain("Full instructions", list.OutputJson);

            var loaded = await new LoadSkillTool(catalog).ExecuteAsync(context, "{\"name\":\"demo-skill\"}", CancellationToken.None);
            Assert.True(loaded.Success);
            Assert.Contains("Full instructions", loaded.OutputJson);
            Assert.Contains("scripts/run.ps1", loaded.OutputJson);

            var traversal = await new LoadSkillTool(catalog).ExecuteAsync(context, "{\"name\":\"../escape\"}", CancellationToken.None);
            Assert.False(traversal.Success);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
