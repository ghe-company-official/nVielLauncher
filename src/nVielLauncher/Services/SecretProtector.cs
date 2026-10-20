using System.Security.Cryptography;
using System.Text;

namespace nVielLauncher.Services;

/// <summary>
/// Stores the GitHub token encrypted for the current Windows user via DPAPI.
/// The plaintext token never touches settings.json or the log.
/// </summary>
public static class SecretProtector
{
    public static string Protect(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";

        try
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(encrypted);
        }
        catch (Exception ex)
        {
            Log.Write("SECRET: не удалось зашифровать токен: " + ex.Message);
            return "";
        }
    }

    public static string Unprotect(string? protectedValue)
    {
        if (string.IsNullOrWhiteSpace(protectedValue)) return "";

        try
        {
            var encrypted = Convert.FromBase64String(protectedValue);
            var bytes = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex)
        {
            // Settings copied from another Windows account cannot be decrypted here.
            Log.Write("SECRET: не удалось расшифровать токен: " + ex.Message);
            return "";
        }
    }
}
