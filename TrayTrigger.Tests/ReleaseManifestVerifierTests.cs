using System.Security.Cryptography;
using System.Text;
using TrayTrigger.Services;

namespace TrayTrigger.Tests;

public class ReleaseManifestVerifierTests
{
    private static readonly byte[] Manifest = Encoding.UTF8.GetBytes("abc123  TrayTrigger-v9.9.9-Setup.exe\n");

    private static (string PublicKey, byte[] Signature) SignWithNewKey(byte[] data)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
                key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
    }

    [Fact]
    public void ASignatureFromATrustedKey_IsAccepted()
    {
        var (publicKey, signature) = SignWithNewKey(Manifest);
        Assert.True(ReleaseManifestVerifier.Verify(Manifest, signature, [publicKey]));
    }

    [Fact]
    public void AnyTrustedKeyWillDo_SoTheBackupKeyCanSignARelease()
    {
        var (ciKey, _) = SignWithNewKey(Manifest);
        var (backupKey, backupSignature) = SignWithNewKey(Manifest);
        Assert.True(ReleaseManifestVerifier.Verify(Manifest, backupSignature, [ciKey, backupKey]));
    }

    [Fact]
    public void ASignatureFromAnotherKey_IsRefused()
    {
        var (_, signature) = SignWithNewKey(Manifest);
        var (otherKey, _) = SignWithNewKey(Manifest);
        Assert.False(ReleaseManifestVerifier.Verify(Manifest, signature, [otherKey]));
    }

    [Fact]
    public void AChangedManifest_IsRefused()
    {
        var (publicKey, signature) = SignWithNewKey(Manifest);
        byte[] tampered = Encoding.UTF8.GetBytes("def456  TrayTrigger-v9.9.9-Setup.exe\n");
        Assert.False(ReleaseManifestVerifier.Verify(tampered, signature, [publicKey]));
    }

    [Fact]
    public void GarbageIsAFailedCheck_NotAnException()
    {
        var (publicKey, signature) = SignWithNewKey(Manifest);
        Assert.False(ReleaseManifestVerifier.Verify(Manifest, [1, 2, 3], [publicKey]));
        Assert.False(ReleaseManifestVerifier.Verify(Manifest, signature, ["not base64!"]));
        Assert.False(ReleaseManifestVerifier.Verify(Manifest, signature, [Convert.ToBase64String([1, 2, 3])]));
        Assert.False(ReleaseManifestVerifier.Verify(Manifest, [], [publicKey]));
        Assert.False(ReleaseManifestVerifier.Verify([], signature, [publicKey]));
        Assert.False(ReleaseManifestVerifier.Verify(Manifest, signature, []));
    }

    [Fact]
    public void EveryCompiledInKey_IsAValidP256PublicKey()
    {
        foreach (string publicKey in ReleaseManifestVerifier.TrustedPublicKeys)
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
            Assert.Equal(256, key.KeySize);
        }
        Assert.Equal(ReleaseManifestVerifier.TrustedPublicKeys.Count, ReleaseManifestVerifier.TrustedPublicKeys.Distinct().Count());
    }

    [Theory]
    [InlineData("TrayTrigger-v1.4.5-Setup.exe", "v1.4.5", true)]
    [InlineData("TrayTrigger-v1.4.5-Setup.exe", "1.4.5", true)]
    [InlineData("traytrigger-V1.4.5-setup.EXE", "v1.4.5", true)]
    [InlineData("TrayTrigger-v1.4.5-beta.1-Setup.exe", "v1.4.5-beta.1", true)]
    // An older, genuinely signed installer re-published under a newer tag.
    [InlineData("TrayTrigger-v1.4.4-Setup.exe", "v9.9.9", false)]
    [InlineData("TrayTrigger-v1.4.5-beta.1-Setup.exe", "v1.4.5", false)]
    [InlineData("Setup.exe", "v1.4.5", false)]
    [InlineData("TrayTrigger-v1.4.5-Setup.exe", null, false)]
    [InlineData("TrayTrigger-v1.4.5-Setup.exe", "  ", false)]
    public void TheInstallerMustBeNamedForItsRelease(string assetName, string? tag, bool expected)
    {
        Assert.Equal(expected, UpdateService.IsInstallerNamedForRelease(assetName, tag));
    }
}
