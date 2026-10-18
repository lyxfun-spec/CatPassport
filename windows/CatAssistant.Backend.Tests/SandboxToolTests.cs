using System.Text.Json;
using CatAssistant.Backend.Services;
using CatAssistant.Backend.Services.Sandbox;
using CatAssistant.Backend.Services.Work;
using CatAssistant.Backend.Services.WorkTools;
using Xunit;

namespace CatAssistant.Backend.Tests;

public sealed class SandboxToolTests
{
    [Fact]
    [Trait("Category", "SecurityIntegration")]
    public async Task FileAndCommandToolsRunInsideSameOsBoundary()
    {
        if (Environment.GetEnvironmentVariable("CATASSISTANT_RUN_APPCONTAINER_PROBE") != "1") return;
        var conversationId = Guid.NewGuid().ToString("N");
        var workPython = WorkRuntimeLocator.FindPython();
        if (workPython is not null) Environment.SetEnvironmentVariable("CATASSISTANT_PYTHON", workPython);
        using var sandbox = new WindowsSandboxController(workPython is null ? null : [Path.GetDirectoryName(workPython)!]);
        var workspace = Path.Combine(sandbox.GetPrivateStorageRoot(conversationId), "Workspace");
        Directory.CreateDirectory(Path.Combine(workspace, "work"));
        var context = new WorkToolExecutionContext(conversationId, Guid.NewGuid().ToString("N"), workspace);
        try
        {
            var write = await new WriteFileTool(sandbox).ExecuteAsync(context,
                "{\"path\":\"work/note.txt\",\"content\":\"hello 工作\"}", CancellationToken.None);
            Assert.True(write.Success, write.ErrorMessage);

            var read = await new ReadFileTool(sandbox).ExecuteAsync(context,
                "{\"path\":\"work/note.txt\"}", CancellationToken.None);
            Assert.True(read.Success, read.ErrorMessage);
            using (var payload = JsonDocument.Parse(read.OutputJson))
                Assert.Equal("hello 工作", payload.RootElement.GetProperty("content").GetString());

            var list = await new ListDirectoryTool(sandbox).ExecuteAsync(context,
                "{\"path\":\"work\"}", CancellationToken.None);
            Assert.True(list.Success, list.ErrorMessage);
            Assert.Contains("note.txt", list.OutputJson);

            var command = await new ExecuteCommandTool(sandbox).ExecuteAsync(context,
                "{\"command\":\"cd & type note.txt\",\"shell\":\"cmd\",\"working_directory\":\"work\"}", CancellationToken.None);
            Assert.True(command.Success, $"{command.ErrorMessage}\n{command.OutputJson}");
            Assert.Contains("hello", command.OutputJson);

            if (workPython is not null)
            {
                await File.WriteAllTextAsync(Path.Combine(workspace, "work", "verify-runtime.py"),
                    "import docx,pptx,openpyxl,pypdf,pdfplumber,reportlab\nprint('runtime-ok')\n");
                var environmentProbe = await new ExecuteCommandTool(sandbox).ExecuteAsync(context,
                    "{\"command\":\"Write-Output 'ps-ok'; Write-Output $env:CATASSISTANT_PYTHON\",\"working_directory\":\"work\"}", CancellationToken.None);
                Assert.Contains("ps-ok", environmentProbe.OutputJson);
                Assert.Contains("python.exe", environmentProbe.OutputJson, StringComparison.OrdinalIgnoreCase);
                var pythonViaTool = await new ExecuteCommandTool(sandbox).ExecuteAsync(context,
                    "{\"command\":\"\\\"%CATASSISTANT_PYTHON%\\\" verify-runtime.py\",\"shell\":\"cmd\",\"working_directory\":\"work\"}", CancellationToken.None);
                Assert.True(pythonViaTool.Success, pythonViaTool.OutputJson);
                Assert.Contains("runtime-ok", pythonViaTool.OutputJson);
                var runtimeSentinel = Path.Combine(Path.GetDirectoryName(workPython)!, "runtime-read-only-probe.txt");
                await File.WriteAllTextAsync(runtimeSentinel, "host");
                try
                {
                    var mutateRuntime = await new ExecuteCommandTool(sandbox).ExecuteAsync(context,
                        "{\"command\":\"Set-Content -LiteralPath (Join-Path (Split-Path $env:CATASSISTANT_PYTHON) 'runtime-read-only-probe.txt') -Value 'sandbox'\"}", CancellationToken.None);
                    Assert.False(mutateRuntime.Success);
                    Assert.Equal("host", await File.ReadAllTextAsync(runtimeSentinel));
                }
                finally { File.Delete(runtimeSentinel); }
                var python = await sandbox.ExecuteAsync(conversationId, workspace,
                    new SandboxCommand(workPython, "verify-runtime.py", Path.Combine(workspace, "work"), TimeSpan.FromSeconds(20)), CancellationToken.None);
                Assert.Equal(0, python.ExitCode);
                Assert.Contains("runtime-ok", python.StandardOutput);
            }

            var outside = Path.Combine(Path.GetTempPath(), $"outside-{Guid.NewGuid():N}.txt");
            await File.WriteAllTextAsync(outside, "secret");
            try
            {
                var denied = await new ExecuteCommandTool(sandbox).ExecuteAsync(context,
                    JsonSerializer.Serialize(new { command = $"Get-Content -LiteralPath '{outside}'" }), CancellationToken.None);
                Assert.False(denied.Success);
                Assert.DoesNotContain("secret", denied.OutputJson);
            }
            finally { File.Delete(outside); }
        }
        finally
        {
            sandbox.DeleteProfile(conversationId);
        }
    }
}
