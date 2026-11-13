using System.IO;
using System.Security.Cryptography;

namespace nVielLauncher.Services;

/// <summary>
/// Detached ECDSA P-256 release signature. The private key never ships with the
/// launcher; only this public key is compiled in. A release without a valid
/// signature is rejected even if it came from the private GitHub repository.
/// </summary>
public static class UpdateSignature
{
    public const int MaxSignatureBytes = 512;

    public static async Task<bool> VerifyFileAsync(
        string path,
        string signatureBase64,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(signatureBase64)) return false;

        try
        {
            var signature = Convert.FromBase64String(signatureBase64.Trim());
            if (signature.Length is <= 0 or > MaxSignatureBytes) return false;

            return await Task.Run(() => VerifyFile(path, signature, AppInfo.UpdatePublicKeyBase64), ct);
        }
        catch (FormatException)
        {
            return false;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    public static bool VerifyFile(string path, byte[] signature, string publicKeyBase64)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);

            using var stream = File.OpenRead(path);
            return key.VerifyData(stream, signature, HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or FormatException)
        {
            return false;
        }
    }

    /// <summary>Verifies an in-memory document (for example manifest.json) with the release key.</summary>
    public static bool VerifyBytes(byte[] data, byte[] signature, string publicKeyBase64)
    {
        try
        {
            if (data.Length == 0 || signature.Length is <= 0 or > MaxSignatureBytes)
                return false;

            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);
            return key.VerifyData(data, signature, HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return false;
        }
    }
}
