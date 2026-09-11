using System.IO;
using TrayTrigger.Services;

namespace TrayTrigger.Tests;

/// <summary>Authenticode signer lookup and WinVerifyTrust wrapper, exercised against a
/// Microsoft-signed system binary (always present and always trusted) and an unsigned file.</summary>
public class AuthenticodeVerifierTests
{
    private static string Kernel32 => Path.Combine(Environment.SystemDirectory, "kernel32.dll");

    [Fact]
    public void SignedSystemFile_HasMicrosoftSubject_AndIsTrusted()
    {
        string? subject = AuthenticodeVerifier.GetSignerSubject(Kernel32);
        Assert.NotNull(subject);
        Assert.Contains("Microsoft", subject);
        Assert.True(AuthenticodeVerifier.IsTrusted(Kernel32));
    }

    [Fact]
    public void UnsignedFile_HasNoSubject_AndIsNotTrusted()
    {
        string path = Path.Combine(Path.GetTempPath(), "TrayTriggerUnsigned_" + Guid.NewGuid().ToString("N") + ".exe");
        try
        {
            // Not even a PE header: "unsigned" must be reported, not thrown.
            File.WriteAllText(path, "definitely not a signed executable");
            Assert.Null(AuthenticodeVerifier.GetSignerSubject(path));
            Assert.False(AuthenticodeVerifier.IsTrusted(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MissingFile_IsNotTrusted()
    {
        string path = Path.Combine(Path.GetTempPath(), "TrayTriggerMissing_" + Guid.NewGuid().ToString("N") + ".exe");
        Assert.Null(AuthenticodeVerifier.GetSignerSubject(path));
        Assert.False(AuthenticodeVerifier.IsTrusted(path));
    }

    [Fact]
    public void TamperedCopyOfSignedFile_IsNotTrusted()
    {
        string path = Path.Combine(Path.GetTempPath(), "TrayTriggerTampered_" + Guid.NewGuid().ToString("N") + ".dll");
        try
        {
            byte[] bytes = File.ReadAllBytes(Kernel32);
            // Flip a byte in the middle of the image; the signature block at the end is untouched,
            // so the signer is still readable but the hash no longer matches.
            bytes[bytes.Length / 2] ^= 0xFF;
            File.WriteAllBytes(path, bytes);

            Assert.NotNull(AuthenticodeVerifier.GetSignerSubject(path));
            Assert.False(AuthenticodeVerifier.IsTrusted(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
