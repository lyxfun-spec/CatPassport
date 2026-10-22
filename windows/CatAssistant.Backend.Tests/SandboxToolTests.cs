using System.Text.Json;
using CatAssistant.Backend.Services;
using CatAssistant.Backend.Services.Sandbox;
using CatAssistant.Backend.Services.Work;
using CatAssistant.Backend.Services.WorkTools;
using Xunit;

namespace CatAssistant.Backend.Tests;

[Collection("Work Python environment")]
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

            var chineseWrite = await new WriteFileTool(sandbox).ExecuteAsync(context,
                "{\"path\":\"work/中文报告.txt\",\"content\":\"数据 摘要\"}", CancellationToken.None);
            Assert.True(chineseWrite.Success, chineseWrite.ErrorMessage);
            var chineseRead = await new ReadFileTool(sandbox).ExecuteAsync(context,
                "{\"path\":\"work/中文报告.txt\"}", CancellationToken.None);
            Assert.True(chineseRead.Success, chineseRead.ErrorMessage);
            using (var payload = JsonDocument.Parse(chineseRead.OutputJson))
                Assert.Equal("数据 摘要", payload.RootElement.GetProperty("content").GetString());
            var chineseList = await new ListDirectoryTool(sandbox).ExecuteAsync(context,
                "{\"path\":\"work\"}", CancellationToken.None);
            Assert.True(chineseList.Success, chineseList.ErrorMessage);
            using (var payload = JsonDocument.Parse(chineseList.OutputJson))
                Assert.Contains("中文报告.txt", payload.RootElement.GetProperty("entries_json").GetString());

            // "hello 工作" as cp936 bytes: legacy ANSI files must survive the read_file round trip.
            await File.WriteAllBytesAsync(Path.Combine(workspace, "work", "legacy-gbk.txt"),
                [0x68, 0x65, 0x6C, 0x6C, 0x6F, 0x20, 0xB9, 0xA4, 0xD7, 0xF7]);
            var gbkRead = await new ReadFileTool(sandbox).ExecuteAsync(context,
                "{\"path\":\"work/legacy-gbk.txt\"}", CancellationToken.None);
            Assert.True(gbkRead.Success, gbkRead.ErrorMessage);
            using (var payload = JsonDocument.Parse(gbkRead.OutputJson))
                Assert.Equal("hello 工作", payload.RootElement.GetProperty("content").GetString());

            var command = await new ExecuteCommandTool(sandbox).ExecuteAsync(context,
                "{\"command\":\"cd & type note.txt\",\"shell\":\"cmd\",\"working_directory\":\"work\"}", CancellationToken.None);
            Assert.True(command.Success, $"{command.ErrorMessage}\n{command.OutputJson}");
            using (var payload = JsonDocument.Parse(command.OutputJson))
                Assert.Contains("工作", payload.RootElement.GetProperty("stdout").GetString());

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
                var preparedEnvironment = await new ExecuteCommandTool(sandbox).ExecuteAsync(context,
                    "{\"command\":\"python -c \\\"import os; print(os.environ['CATASSISTANT_WORKSPACE']); print(os.getcwd()); print(os.environ['PIP_TARGET']); print(os.environ['PYTHONPATH'])\\\"\",\"shell\":\"cmd\"}", CancellationToken.None);
                Assert.True(preparedEnvironment.Success, preparedEnvironment.OutputJson);
                using (var preparedPayload = JsonDocument.Parse(preparedEnvironment.OutputJson))
                {
                    var preparedOutput = preparedPayload.RootElement.GetProperty("stdout").GetString() ?? "";
                    Assert.Contains(workspace, preparedOutput, StringComparison.OrdinalIgnoreCase);
                    Assert.Contains("python-packages", preparedOutput, StringComparison.OrdinalIgnoreCase);
                }
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

    [Fact]
    public void CommandPreludePreparesConventionalPythonEnvironment()
    {
        var previous = Environment.GetEnvironmentVariable("CATASSISTANT_PYTHON");
        try
        {
            Environment.SetEnvironmentVariable("CATASSISTANT_PYTHON", @"C:\runtime\python.exe");
            var root = Path.Combine(Path.GetTempPath(), $"cat-command-{Guid.NewGuid():N}");
            var powerShell = ExecuteCommandTool.BuildPowerShellPrelude(root, root);
            var cmd = ExecuteCommandTool.BuildCmdPrelude(root, root);

            Assert.Contains("PIP_TARGET", powerShell);
            Assert.Contains("PYTHONPATH", powerShell);
            Assert.Contains("C:\\runtime", powerShell);
            Assert.Contains("New-PSDrive", powerShell);
            Assert.Contains("Set-Location CATWORK", powerShell);
            Assert.Contains("PIP_TARGET", cmd);
            Assert.Contains("PYTHONPATH", cmd);
            Assert.Contains("C:\\runtime", cmd);
            Assert.True(Directory.Exists(Path.Combine(root, "work", "python-packages")));
            Directory.Delete(root, recursive: true);
        }
        finally { Environment.SetEnvironmentVariable("CATASSISTANT_PYTHON", previous); }
    }

    [Fact]
    public void ToolDefinitionsDescribeExternalApprovalAndAbsolutePaths()
    {
        using var sandbox = new WindowsSandboxController();
        var command = new ExecuteCommandTool(sandbox).Definition;
        Assert.Contains("user approval", command.Description);
        using (var parameters = JsonDocument.Parse(command.ParametersJson))
        {
            var properties = parameters.RootElement.GetProperty("properties");
            Assert.Contains("absolute", properties.GetProperty("working_directory").GetProperty("description").GetString());
            Assert.Contains("outside", properties.GetProperty("outside_sandbox").GetProperty("description").GetString());
        }

        var fileTools = new IWorkTool[]
        {
            new ReadFileTool(sandbox),
            new WriteFileTool(sandbox),
            new ListDirectoryTool(sandbox)
        };
        foreach (var tool in fileTools)
        {
            Assert.Contains("user approval", tool.Definition.Description);
            using var parameters = JsonDocument.Parse(tool.Definition.ParametersJson);
            var properties = parameters.RootElement.GetProperty("properties");
            Assert.Contains("absolute", properties.GetProperty("path").GetProperty("description").GetString());
            Assert.Contains("current run", properties.GetProperty("approval_scope").GetProperty("description").GetString());
        }
    }
}

public sealed class SandboxTextTests
{
    [Fact]
    public void DecodesUtf8AndStripsBom()
    {
        Assert.Equal("hello 工作", SandboxText.Decode("hello 工作"u8.ToArray()));
        Assert.Equal("hello 工作", SandboxText.Decode([0xEF, 0xBB, 0xBF, .. "hello 工作"u8.ToArray()]));
        Assert.Equal(string.Empty, SandboxText.Decode([]));
    }

    [Fact]
    public void FallsBackToOemCodePageForLegacyGbkBytes()
    {
        if (System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage != 936) return;
        // "hello 工作" encoded as cp936.
        Assert.Equal("hello 工作", SandboxText.Decode([0x68, 0x65, 0x6C, 0x6C, 0x6F, 0x20, 0xB9, 0xA4, 0xD7, 0xF7]));
    }
}
