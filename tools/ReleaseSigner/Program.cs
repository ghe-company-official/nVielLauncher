using System.Security.Cryptography;

if (args.Length >= 1 && args[0].Equals("generate", StringComparison.OrdinalIgnoreCase))
{
    if (args.Length < 3)
    {
        Console.Error.WriteLine("Usage: ReleaseSigner generate <private-key-out> <public-key-out>");
        return 2;
    }

    try
    {
        var privateKeyPath = Path.GetFullPath(args[1]);
        var publicKeyPath = Path.GetFullPath(args[2]);
        if (File.Exists(privateKeyPath) || File.Exists(publicKeyPath))
            throw new IOException("Key file already exists; refusing to overwrite signing material.");

        Directory.CreateDirectory(Path.GetDirectoryName(privateKeyPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(publicKeyPath)!);

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        File.WriteAllText(privateKeyPath,
            Convert.ToBase64String(key.ExportPkcs8PrivateKey()));
        File.WriteAllText(publicKeyPath,
            Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));

        Console.WriteLine("Private key: " + privateKeyPath);
        Console.WriteLine("Public key:  " + publicKeyPath);
        Console.WriteLine("Embed the public key in AppInfo.UpdatePublicKeyBase64.");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine("Key generation failed: " + ex.Message);
        return 1;
    }
}

if (args.Length >= 1 && args[0].Equals("verify", StringComparison.OrdinalIgnoreCase))
{
    if (args.Length < 4)
    {
        Console.Error.WriteLine("Usage: ReleaseSigner verify <file> <signature> <public-key-base64-file>");
        return 2;
    }

    try
    {
        var file = Path.GetFullPath(args[1]);
        var signaturePath = Path.GetFullPath(args[2]);
        var publicKeyPath = Path.GetFullPath(args[3]);

        var signature = Convert.FromBase64String(File.ReadAllText(signaturePath).Trim());
        var publicKey = Convert.FromBase64String(File.ReadAllText(publicKeyPath).Trim());

        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(publicKey, out _);

        using var stream = File.OpenRead(file);
        var valid = key.VerifyData(stream, signature, HashAlgorithmName.SHA256);
        Console.WriteLine(valid ? "Signature valid." : "Signature INVALID.");
        return valid ? 0 : 1;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine("Verification failed: " + ex.Message);
        return 1;
    }
}

if (args.Length < 3)
{
    Console.Error.WriteLine("Usage: ReleaseSigner <file> <signature-out> <private-key-base64-file>");
    return 2;
}

try
{
    var file = Path.GetFullPath(args[0]);
    var output = Path.GetFullPath(args[1]);
    var keyPath = Path.GetFullPath(args[2]);

    if (!File.Exists(file)) throw new FileNotFoundException("Release file not found.", file);
    if (!File.Exists(keyPath)) throw new FileNotFoundException("Private key not found.", keyPath);

    var privateKey = Convert.FromBase64String(File.ReadAllText(keyPath).Trim());
    using var key = ECDsa.Create();
    key.ImportPkcs8PrivateKey(privateKey, out _);

    using var stream = File.OpenRead(file);
    var signature = key.SignData(stream, HashAlgorithmName.SHA256);
    File.WriteAllText(output, Convert.ToBase64String(signature));
    Console.WriteLine("Signed: " + file);
    Console.WriteLine("Signature: " + output);
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
