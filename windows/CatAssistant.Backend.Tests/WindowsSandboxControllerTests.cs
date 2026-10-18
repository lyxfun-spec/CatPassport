using CatAssistant.Backend.Services.Sandbox;
using Xunit;

namespace CatAssistant.Backend.Tests;

public sealed class WindowsSandboxControllerTests
{
    [Fact]
    public void ContainmentRejectsSiblingAndPrefixCollision()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "work-a"));
        WindowsSandboxController.EnsureContained(root, Path.Combine(root, "work"));
        Assert.Throws<UnauthorizedAccessException>(() =>
            WindowsSandboxController.EnsureContained(root, Path.Combine(Path.GetDirectoryName(root)!, "work-a-escape")));
    }

    [Fact]
    [Trait("Category", "SecurityIntegration")]
    public async Task ProductionControllerCapturesOutputAndEnforcesFileAndNetworkBoundaries()
    {
        if (Environment.GetEnvironmentVariable("CATASSISTANT_RUN_APPCONTAINER_PROBE") != "1") return;
        if (!OperatingSystem.IsWindowsVersionAtLeast(10)) return;

        var root = Path.Combine(Path.GetTempPath(), $"cat-assistant-controller-{Guid.NewGuid():N}");
        var workspace = Path.Combine(root, "workspace");
        var sibling = Path.Combine(root, "sibling");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(sibling);
        var allowed = Path.Combine(workspace, "allowed.txt");
        var denied = Path.Combine(sibling, "denied.txt");
        await File.WriteAllTextAsync(allowed, "inside");
        await File.WriteAllTextAsync(denied, "outside");
        var conversationId = Guid.NewGuid().ToString("N");
        var junction = Path.Combine(workspace, "escape-junction");
        using var controller = new WindowsSandboxController();
        try
        {
            var shell = Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";
            var inside = await controller.ExecuteAsync(conversationId, workspace,
                new SandboxCommand(shell, $"/d /q /c type \"{allowed}\"", workspace, TimeSpan.FromSeconds(10), AllowNetwork: false));
            Assert.Equal(0, inside.ExitCode);
            Assert.Contains("inside", inside.StandardOutput);

            var outside = await controller.ExecuteAsync(conversationId, workspace,
                new SandboxCommand(shell, $"/d /q /c type \"{denied}\"", workspace, TimeSpan.FromSeconds(10), AllowNetwork: false));
            Assert.NotEqual(0, outside.ExitCode);
            Assert.DoesNotContain("outside", outside.StandardOutput);

            using (var junctionProcess = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                       shell, $"/d /q /c mklink /J \"{junction}\" \"{sibling}\"")
                   { UseShellExecute = false, CreateNoWindow = true }))
            {
                Assert.NotNull(junctionProcess);
                await junctionProcess!.WaitForExitAsync();
                Assert.Equal(0, junctionProcess.ExitCode);
            }
            var throughJunction = await controller.ExecuteAsync(conversationId, workspace,
                new SandboxCommand(shell, $"/d /q /c type \"{Path.Combine(junction, "denied.txt")}\"", workspace, TimeSpan.FromSeconds(10), AllowNetwork: false));
            Assert.NotEqual(0, throughJunction.ExitCode);
            Assert.DoesNotContain("outside", throughJunction.StandardOutput);

            var marker = Path.Combine(workspace, "child-survived.txt");
            var childScript = Path.Combine(workspace, "delayed-child.cmd");
            await File.WriteAllTextAsync(childScript, $"@ping -n 6 127.0.0.1 >nul\r\n@echo escaped>\"{marker}\"\r\n");
            var script = Path.Combine(workspace, "spawn-child.ps1");
            await File.WriteAllTextAsync(script,
                $"$child = Start-Process -FilePath $env:ComSpec -ArgumentList @('/d','/q','/c','call \"{childScript}\"') -PassThru -WindowStyle Hidden\r\nStart-Sleep -Seconds 30\r\n");
            var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
            using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(500)))
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => controller.ExecuteAsync(conversationId, workspace,
                    new SandboxCommand(powershell, $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{script}\"", workspace, TimeSpan.FromSeconds(40), AllowNetwork: false), cancel.Token));
            }
            await Task.Delay(TimeSpan.FromSeconds(7));
            Assert.False(File.Exists(marker), "A child process escaped the job and wrote after cancellation.");

            var curl = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "curl.exe");
            var noNetwork = await controller.ExecuteAsync(conversationId, workspace,
                new SandboxCommand(curl, "--fail --silent --show-error --connect-timeout 2 --max-time 4 https://example.com/ -o NUL", workspace, TimeSpan.FromSeconds(8), AllowNetwork: false));
            Assert.NotEqual(0, noNetwork.ExitCode);
            var withNetwork = await controller.ExecuteAsync(conversationId, workspace,
                new SandboxCommand(curl, "--fail --silent --show-error --connect-timeout 3 --max-time 5 https://example.com/ -o NUL", workspace, TimeSpan.FromSeconds(10)));
            Assert.Equal(0, withNetwork.ExitCode);
        }
        finally
        {
            controller.DeleteProfile(conversationId);
            if (Directory.Exists(junction)) Directory.Delete(junction);
            Directory.Delete(root, recursive: true);
        }
    }
}
