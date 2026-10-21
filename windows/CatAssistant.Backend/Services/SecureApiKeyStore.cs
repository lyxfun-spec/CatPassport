using System.Security.Cryptography;
using System.Text;

namespace CatAssistant.Backend.Services;

public sealed class SecureApiKeyStore
{
    private readonly string _path;

    public SecureApiKeyStore(string fileName = "dashscope.key") => _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FoloToy", "CatAssistant", fileName);

    public string? Load()
    {
        if (!File.Exists(_path)) return null;
        try
        {
            var clear = ProtectedData.Unprotect(File.ReadAllBytes(_path), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(clear);
        }
        catch (CryptographicException) { return null; }
    }

    public void Save(string apiKey)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            if (File.Exists(_path)) File.Delete(_path);
            return;
        }
        var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(apiKey.Trim()), null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(_path, protectedBytes);
    }
}
