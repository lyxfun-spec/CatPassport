using System.Runtime.InteropServices;
using System.Text;

namespace CatAssistant.Setup.Services;

internal static class InstalledProductService
{
    internal const string UpgradeCode = "{7F1998F1-D743-45B3-BFF9-6B37A0A51A2F}";

    public static InstalledVersionState GetState(Version packageVersion)
    {
        var installedVersions = EnumerateRelatedProductVersions();
        return Classify(packageVersion, installedVersions);
    }

    internal static InstalledVersionState Classify(Version packageVersion, IEnumerable<Version> installedVersions)
    {
        var highest = installedVersions.DefaultIfEmpty().Max();
        if (highest is null) return new(VersionInstallDisposition.FreshInstall, null);

        var comparison = highest.CompareTo(packageVersion);
        return comparison switch
        {
            > 0 => new(VersionInstallDisposition.BlockNewerVersion, highest),
            0 => new(VersionInstallDisposition.RepairSameVersion, highest),
            _ => new(VersionInstallDisposition.UpgradeOlderVersion, highest)
        };
    }

    internal static bool PolicySelfTest()
    {
        var package = new Version(1, 2, 0);
        return Classify(package, []).Disposition == VersionInstallDisposition.FreshInstall
            && Classify(package, [new Version(1, 2, 0)]).Disposition == VersionInstallDisposition.RepairSameVersion
            && Classify(package, [new Version(1, 1, 9)]).Disposition == VersionInstallDisposition.UpgradeOlderVersion
            && Classify(package, [new Version(1, 0, 0), new Version(2, 0, 0)]).Disposition == VersionInstallDisposition.BlockNewerVersion;
    }

    private static IReadOnlyList<Version> EnumerateRelatedProductVersions()
    {
        var versions = new List<Version>();
        for (uint index = 0; ; index++)
        {
            var productCode = new StringBuilder(39);
            var result = NativeMethods.MsiEnumRelatedProducts(UpgradeCode, 0, index, productCode);
            if (result == NativeMethods.ErrorNoMoreItems) break;
            if (result != NativeMethods.ErrorSuccess) continue;

            var versionText = new StringBuilder(64);
            var length = versionText.Capacity;
            result = NativeMethods.MsiGetProductInfo(productCode.ToString(), "VersionString", versionText, ref length);
            if (result == NativeMethods.ErrorSuccess && Version.TryParse(versionText.ToString(), out var version))
                versions.Add(version);
        }
        return versions;
    }

    private static class NativeMethods
    {
        internal const uint ErrorSuccess = 0;
        internal const uint ErrorNoMoreItems = 259;

        [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiEnumRelatedProductsW")]
        internal static extern uint MsiEnumRelatedProducts(
            string upgradeCode,
            uint reserved,
            uint productIndex,
            StringBuilder productCode);

        [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiGetProductInfoW")]
        internal static extern uint MsiGetProductInfo(
            string productCode,
            string property,
            StringBuilder valueBuffer,
            ref int valueBufferLength);
    }
}
