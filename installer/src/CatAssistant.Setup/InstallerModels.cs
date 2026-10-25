namespace CatAssistant.Setup;

internal enum WizardPage
{
    Welcome,
    AppVersion,
    Check,
    Options,
    Installing,
    Complete
}

internal sealed record InstallOptions(
    string InstallDirectory,
    bool DesktopShortcut,
    bool StartMenuShortcut,
    bool LaunchAfterInstall);

internal sealed record CheckResult(string Name, string Detail, bool Passed, bool Blocking);

internal sealed record InstallResult(bool Succeeded, bool Cancelled, int ExitCode, string Message, string LogPath);

internal sealed record InstallationProgress(int Percent, string Status);

internal sealed record InstallLocationCheck(
    bool IsUsable,
    string Message);

internal enum VersionInstallDisposition
{
    FreshInstall,
    RepairSameVersion,
    UpgradeOlderVersion,
    BlockNewerVersion
}

internal sealed record InstalledVersionState(
    VersionInstallDisposition Disposition,
    Version? HighestInstalledVersion);
