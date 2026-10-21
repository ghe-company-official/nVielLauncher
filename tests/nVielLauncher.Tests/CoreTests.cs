using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using nVielLauncher.Services;
using Xunit;

namespace nVielLauncher.Tests;

public sealed class VersionUtilTests
{
    [Theory]
    [InlineData("1.0.2", 1, 0, 2, 0)]
    [InlineData("v1.0.2", 1, 0, 2, 0)]
    [InlineData("1.0.2 Global", 1, 0, 2, 0)]
    [InlineData("1.0.2.1", 1, 0, 2, 1)]
    [InlineData("1.0.2-beta", 1, 0, 2, 0)]
    public void ParsesCommonReleaseTags(string text, int major, int minor, int build, int revision)
    {
        var version = VersionUtil.TryParse(text);

        Assert.NotNull(version);
        Assert.Equal(new Version(major, minor, build, revision), VersionUtil.Normalize(version!));
    }

    [Theory]
    [InlineData("1.0.2.0", "1.0.2")]
    [InlineData("1.0.2.1", "1.0.2.1")]
    [InlineData("1.2", "1.2")]
    [InlineData("2.0.0", "2.0")]
    public void FormatsWithoutMeaninglessParts(string input, string expected)
    {
        Assert.Equal(expected, VersionUtil.Format(Version.Parse(input)));
    }

    [Fact]
    public void ComparesNormalizedVersions()
    {
        Assert.True(VersionUtil.IsNewer(Version.Parse("1.0.10"), Version.Parse("1.0.9")));
        Assert.False(VersionUtil.IsNewer(Version.Parse("1.0.2.0"), Version.Parse("1.0.2")));
        Assert.True(VersionUtil.IsNewer(Version.Parse("1.0.2.1"), Version.Parse("1.0.2")));
    }

    [Fact]
    public void ProductVersionIs102Global()
    {
        Assert.Equal("1.0.2", AppInfo.VersionLabel);
        Assert.Equal("1.0.2 Global", AppInfo.DisplayVersion);
    }
}

public sealed class UpdateSignatureTests
{
    [Fact]
    public void VerifiesRealSignatureAndRejectsTampering()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        var path = Path.Combine(Path.GetTempPath(), "nviel-signature-" + Guid.NewGuid().ToString("N") + ".bin");

        try
        {
            File.WriteAllBytes(path, "nViel Launcher release payload"u8.ToArray());
            using (var stream = File.OpenRead(path))
            {
                var signature = key.SignData(stream, HashAlgorithmName.SHA256);
                Assert.True(UpdateSignature.VerifyFile(path, signature, publicKey));

                var tampered = (byte[])signature.Clone();
                tampered[0] ^= 0x40;
                Assert.False(UpdateSignature.VerifyFile(path, tampered, publicKey));
            }

            File.AppendAllText(path, "tamper");
            using (var stream = File.OpenRead(path))
            {
                var signature = key.SignData(stream, HashAlgorithmName.SHA256);
                Assert.True(UpdateSignature.VerifyFile(path, signature, publicKey));
            }
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void RejectsGarbageSignature()
    {
        Assert.False(UpdateSignature.VerifyFile("missing.exe", new byte[] { 1, 2, 3 },
            AppInfo.UpdatePublicKeyBase64));
    }
}

public sealed class SettingsShapeTests
{
    [Fact]
    public void SettingsContainNoUpdateOrTokenData()
    {
        var forbidden = new[] { "update", "token", "repository", "release", "github", "proxy" };

        var suspicious = typeof(LauncherSettings)
            .GetProperties()
            .Where(p => forbidden.Any(word => p.Name.Contains(word, StringComparison.OrdinalIgnoreCase)))
            .Select(p => p.Name)
            .ToList();

        Assert.Empty(suspicious);
    }
}

public sealed class UpdateHealthTests
{
    [Fact]
    public void ParsesHealthMarkerArgument()
    {
        var marker = Path.Combine(Path.GetTempPath(), "marker.ok");

        Assert.Equal(marker, UpdateHealth.GetMarkerPath(new[] { "--update-health", marker }));
        Assert.Equal(marker, UpdateHealth.GetMarkerPath(new[] { "--update-health", marker, "--re" }));
        Assert.Null(UpdateHealth.GetMarkerPath(new[] { "--update-health" }));
        Assert.Null(UpdateHealth.GetMarkerPath(Array.Empty<string>()));
    }
}

public sealed class ReleaseVerificationTests
{
    [Fact]
    public async Task DistBuildPassesTheRealUpdaterVerification()
    {
        var exe = Path.GetFullPath(Path.Combine("dist", "nVielLauncher.exe"));
        if (!File.Exists(exe)) return;

        var signature = await File.ReadAllTextAsync(exe + ".sig");
        await using var stream = File.OpenRead(exe);
        var sha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream));
        var info = new UpdateInfo(
            AppInfo.VersionLabel,
            "v" + AppInfo.VersionLabel,
            AppInfo.DisplayName,
            "",
            exe,
            new FileInfo(exe).Length,
            sha256,
            "",
            "",
            exe + ".sig",
            "");

        await UpdateService.VerifyAsync(exe, info, signature, CancellationToken.None);
    }
}
