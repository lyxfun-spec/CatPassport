using System.Text.Json;
using CatAssistant.Backend.Services;
using CatAssistant.Backend.Services.Sandbox;
using CatAssistant.Backend.Services.Work;
using CatAssistant.Backend.Services.WorkTools;
using Xunit;

namespace CatAssistant.Backend.Tests;

[CollectionDefinition("Work Python environment", DisableParallelization = true)]
public sealed class WorkPythonEnvironmentCollection;

[Collection("Work Python environment")]
public sealed class PowerShellNativeProcessIntegrationTests
{
    [Fact]
    [Trait("Category", "SecurityIntegration")]
    public async Task PowerShellAndCmdLaunchNativeProcessesInsideAppContainer()
    {
        if (Environment.GetEnvironmentVariable("CATASSISTANT_RUN_APPCONTAINER_PROBE") != "1") return;
        if (!OperatingSystem.IsWindowsVersionAtLeast(10)) return;

        var python = WorkRuntimeLocator.FindPython();
        Assert.False(string.IsNullOrWhiteSpace(python));
        var previousPython = Environment.GetEnvironmentVariable("CATASSISTANT_PYTHON");
        Environment.SetEnvironmentVariable("CATASSISTANT_PYTHON", python);
        var conversationId = Guid.NewGuid().ToString("N");
        using var sandbox = new WindowsSandboxController([Path.GetDirectoryName(python)!]);
        var workspace = Path.Combine(sandbox.GetPrivateStorageRoot(conversationId), "Workspace");
        var workingDirectory = Path.Combine(workspace, "work", "native-probe");
        Directory.CreateDirectory(workingDirectory);
        var context = new WorkToolExecutionContext(conversationId, Guid.NewGuid().ToString("N"), workspace);
        var tool = new ExecuteCommandTool(sandbox);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(workingDirectory, "child.py"),
                "import os,subprocess,sys\nopen('python-started.txt','w',encoding='utf-8').write('started')\n" +
                "print('python-parent:中文')\n" +
                "print('cwd=' + os.getcwd())\n" +
                "print('pythonpath=' + os.environ['PYTHONPATH'])\n" +
                "print('pip_target=' + os.environ['PIP_TARGET'])\n" +
                "r=subprocess.run([os.environ['ComSpec'],'/d','/q','/c','echo python-child:中文'],capture_output=True,text=True,encoding='utf-8')\n" +
                "print(r.stdout.strip())\n" +
                "print('python-child-exit=' + str(r.returncode))\n" +
                "print('python-stderr:中文', file=sys.stderr)\n");
            await File.WriteAllTextAsync(Path.Combine(workingDirectory, "start-process.cmd"),
                "@echo start-process-ok\r\n@cd\r\n");

            var startProcess = await ExecuteAsync(tool, context,
                "$p=Start-Process -FilePath $env:ComSpec -ArgumentList @('/d','/q','/c','call start-process.cmd') -NoNewWindow -Wait -PassThru; Write-Output \"start-process-exit=$($p.ExitCode)\"", "powershell");
            Assert.True(startProcess.Result.Success, Failure(startProcess));
            Assert.True(startProcess.Stdout.Contains("start-process-ok", StringComparison.Ordinal), Failure(startProcess));
            Assert.True(startProcess.Stdout.Contains("start-process-exit=0", StringComparison.Ordinal), Failure(startProcess));
            Assert.Contains(workingDirectory, startProcess.Stdout, StringComparison.OrdinalIgnoreCase);

            var directNative = await ExecuteAsync(tool, context,
                "& $env:ComSpec /d /q /c 'echo native-direct-ok'; Write-Output \"native-direct-exit=$LASTEXITCODE\"", "powershell");
            Assert.True(directNative.Result.Success, Failure(directNative));
            Assert.True(directNative.Stdout.Contains("native-direct-ok", StringComparison.Ordinal), Failure(directNative));
            Assert.True(directNative.Stdout.Contains("native-direct-exit=0", StringComparison.Ordinal), Failure(directNative));

            var directPython = await ExecuteAsync(tool, context,
                "& $env:CATASSISTANT_PYTHON child.py", "powershell");
            Assert.True(directPython.Result.Success, Failure(directPython));
            Assert.True(directPython.Stdout.Contains("python-parent:中文", StringComparison.Ordinal), Failure(directPython));
            Assert.Contains("python-child:中文", directPython.Stdout);
            Assert.Contains("python-child-exit=0", directPython.Stdout);
            Assert.Contains("python-stderr:中文", directPython.Stderr);
            Assert.Contains(workingDirectory, directPython.Stdout, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("python-packages", directPython.Stdout, StringComparison.OrdinalIgnoreCase);

            var pip = await ExecuteAsync(tool, context, "pip --version", "powershell");
            Assert.True(pip.Result.Success, Failure(pip));
            Assert.Contains("pip ", pip.Stdout, StringComparison.OrdinalIgnoreCase);

            var failingNative = await ExecuteAsync(tool, context,
                "& $env:ComSpec /d /q /c 'echo native-direct:中文 & echo native-error:中文 1>&2 & exit /b 7'", "powershell");
            Assert.False(failingNative.Result.Success);
            Assert.Equal(7, failingNative.ExitCode);
            Assert.Contains("native-direct:中文", failingNative.Stdout);
            Assert.Contains("native-error:中文", failingNative.Stderr);

            var cmd = await ExecuteAsync(tool, context,
                "echo cmd-native-ok & python -c \"print('cmd-python:中文')\"", "cmd");
            Assert.True(cmd.Result.Success, Failure(cmd));
            Assert.Contains("cmd-native-ok", cmd.Stdout);
            Assert.Contains("cmd-python:中文", cmd.Stdout);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CATASSISTANT_PYTHON", previousPython);
            sandbox.DeleteProfile(conversationId);
        }
    }

    [Fact]
    [Trait("Category", "SecurityIntegration")]
    public async Task CancellationAndTimeoutTerminateStartedPowerShellChildTrees()
    {
        if (Environment.GetEnvironmentVariable("CATASSISTANT_RUN_APPCONTAINER_PROBE") != "1") return;
        if (!OperatingSystem.IsWindowsVersionAtLeast(10)) return;

        var conversationId = Guid.NewGuid().ToString("N");
        using var sandbox = new WindowsSandboxController();
        var workspace = Path.Combine(sandbox.GetPrivateStorageRoot(conversationId), "Workspace");
        var workingDirectory = Path.Combine(workspace, "work", "tree-probe");
        Directory.CreateDirectory(workingDirectory);
        var context = new WorkToolExecutionContext(conversationId, Guid.NewGuid().ToString("N"), workspace);
        var tool = new ExecuteCommandTool(sandbox);

        try
        {
            var cancelStarted = Path.Combine(workingDirectory, "cancel-started.txt");
            var cancelSurvived = Path.Combine(workingDirectory, "cancel-survived.txt");
            await WriteDelayedChildAsync(workingDirectory, "cancel-child.ps1", cancelStarted, cancelSurvived);
            using (var cancellation = new CancellationTokenSource())
            {
                var execution = tool.ExecuteAsync(context, JsonSerializer.Serialize(new
                {
                    command = "& powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File cancel-child.ps1; Start-Sleep -Seconds 30",
                    shell = "powershell",
                    working_directory = "work/tree-probe",
                    timeout_seconds = 40
                }), cancellation.Token);
                Assert.True(await WaitForFileAsync(cancelStarted, TimeSpan.FromSeconds(5)),
                    "The cancellation child never reached its explicit startup marker.");
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
            }
            await Task.Delay(TimeSpan.FromSeconds(4));
            Assert.False(File.Exists(cancelSurvived), "A child process escaped the job after cancellation.");

            var timeoutStarted = Path.Combine(workingDirectory, "timeout-started.txt");
            var timeoutSurvived = Path.Combine(workingDirectory, "timeout-survived.txt");
            await WriteDelayedChildAsync(workingDirectory, "timeout-child.ps1", timeoutStarted, timeoutSurvived);
            var timeoutExecution = tool.ExecuteAsync(context, JsonSerializer.Serialize(new
            {
                command = "$null=Start-Process -FilePath powershell.exe -ArgumentList @('-NoLogo','-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File','timeout-child.ps1') -NoNewWindow -PassThru; Start-Sleep -Seconds 30",
                shell = "powershell",
                working_directory = "work/tree-probe",
                timeout_seconds = 1
            }), CancellationToken.None);
            Assert.True(await WaitForFileAsync(timeoutStarted, TimeSpan.FromSeconds(5)),
                "The timeout child never reached its explicit startup marker.");
            var timeout = await timeoutExecution;
            Assert.False(timeout.Success);
            Assert.Equal("command_timeout", timeout.ErrorCode);
            await Task.Delay(TimeSpan.FromSeconds(4));
            Assert.False(File.Exists(timeoutSurvived), "A child process escaped the job after timeout.");
        }
        finally
        {
            sandbox.DeleteProfile(conversationId);
        }
    }

    private static Task WriteDelayedChildAsync(string directory, string fileName, string started, string survived) =>
        File.WriteAllTextAsync(Path.Combine(directory, fileName),
            $"Set-Content -LiteralPath '{PowerShellLiteral(started)}' -Value started\r\n" +
            "Start-Sleep -Seconds 3\r\n" +
            $"Set-Content -LiteralPath '{PowerShellLiteral(survived)}' -Value survived\r\n");

    private static string PowerShellLiteral(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static async Task<bool> WaitForFileAsync(string path, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path)) return true;
            await Task.Delay(50);
        }
        return File.Exists(path);
    }

    private static async Task<Execution> ExecuteAsync(
        ExecuteCommandTool tool,
        WorkToolExecutionContext context,
        string command,
        string shell)
    {
        var result = await tool.ExecuteAsync(context, JsonSerializer.Serialize(new
        {
            command,
            shell,
            working_directory = "work/native-probe",
            timeout_seconds = 20
        }), CancellationToken.None);
        using var payload = JsonDocument.Parse(result.OutputJson);
        return new Execution(
            result,
            payload.RootElement.TryGetProperty("exit_code", out var exitCode) ? exitCode.GetInt32() : null,
            payload.RootElement.TryGetProperty("stdout", out var stdout) ? stdout.GetString() ?? "" : "",
            payload.RootElement.TryGetProperty("stderr", out var stderr) ? stderr.GetString() ?? "" : "");
    }

    private static string Failure(Execution execution) =>
        $"{execution.Result.ErrorCode}: {execution.Result.ErrorMessage}\nexit={execution.ExitCode}\nstdout={execution.Stdout}\nstderr={execution.Stderr}";

    private sealed record Execution(WorkToolResult Result, int? ExitCode, string Stdout, string Stderr);
}
