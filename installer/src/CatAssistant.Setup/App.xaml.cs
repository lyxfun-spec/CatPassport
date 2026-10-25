using CatAssistant.Setup.Services;

namespace CatAssistant.Setup;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--install-worker", StringComparer.OrdinalIgnoreCase))
        {
            try { Shutdown(WindowsInstallerWorker.Run(e.Args)); }
            catch { Shutdown(-1); }
            return;
        }

        if (e.Args.Contains("--progress-self-test", StringComparer.OrdinalIgnoreCase))
        {
            Shutdown(WindowsInstallerWorker.ProgressSelfTest() ? 0 : 3);
            return;
        }

        if (e.Args.Contains("--version-policy-self-test", StringComparer.OrdinalIgnoreCase))
        {
            Shutdown(InstalledProductService.PolicySelfTest() ? 0 : 4);
            return;
        }

        if (e.Args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
        {
            var assembly = typeof(App).Assembly;
            using var stream = assembly.GetManifestResourceStream(MsiInstaller.MsiResourceName);
            var valid = stream is { Length: > 1024 * 1024 };
            Console.WriteLine(valid ? $"OK embedded-msi bytes={stream!.Length}" : "ERROR embedded-msi missing-or-empty");
            Shutdown(valid ? 0 : 2);
            return;
        }

        new MainWindow().Show();
    }
}
