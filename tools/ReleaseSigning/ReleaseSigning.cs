// Release signing for TrayTrigger: key generation (run once, by the maintainer) and signing of
// SHA256SUMS.txt (run by release.yml). A .NET 10 file-based app - no project file:
//
//   dotnet run --file tools/ReleaseSigning/ReleaseSigning.cs -- new-keys --backup-file <path> [--repo owner/name]
//   dotnet run --file tools/ReleaseSigning/ReleaseSigning.cs -- sign <SHA256SUMS.txt> [--trusted <ReleaseManifestVerifier.cs>]
//   dotnet run --file tools/ReleaseSigning/ReleaseSigning.cs -- verify <SHA256SUMS.txt> --trusted <ReleaseManifestVerifier.cs>
//
// ECDSA P-256 / SHA-256, DER signatures - what Services/ReleaseManifestVerifier.cs checks and what
// "openssl dgst -sha256 -verify" understands. Private keys are never printed.

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

const string SecretName = "RELEASE_SIGNING_KEY";

try
{
    return args.FirstOrDefault() switch
    {
        "new-keys" => NewKeys(args),
        "sign" => Sign(args),
        "verify" => VerifyCommand(args),
        _ => Usage()
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}

static int Usage()
{
    Console.Error.WriteLine("usage: new-keys --backup-file <path> [--repo owner/name] | sign <manifest> [--trusted <file>] | verify <manifest> --trusted <file>");
    return 2;
}

static string? Option(string[] args, string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

// ---------------------------------------------------------------------------------------------
// new-keys: a CI key (private half straight into the GitHub Actions secret, through gh's stdin)
// and a backup key (private half into a file the maintainer stores offline).
// ---------------------------------------------------------------------------------------------
static int NewKeys(string[] args)
{
    string backupFile = Option(args, "--backup-file") ?? throw new ArgumentException("--backup-file <path> is required.");
    string? repo = Option(args, "--repo");

    backupFile = Path.GetFullPath(backupFile);
    if (File.Exists(backupFile)) throw new InvalidOperationException($"{backupFile} already exists; refusing to overwrite a key.");

    using var ciKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    using var backupKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    // The secret first: if gh fails, nothing has been written anywhere.
    var gh = new ProcessStartInfo("gh") { RedirectStandardInput = true, UseShellExecute = false };
    gh.ArgumentList.Add("secret");
    gh.ArgumentList.Add("set");
    gh.ArgumentList.Add(SecretName);
    if (repo != null)
    {
        gh.ArgumentList.Add("--repo");
        gh.ArgumentList.Add(repo);
    }
    using (var process = Process.Start(gh) ?? throw new InvalidOperationException("Could not start gh."))
    {
        process.StandardInput.Write(ciKey.ExportPkcs8PrivateKeyPem());
        process.StandardInput.Close();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException($"gh secret set failed with exit code {process.ExitCode}. No key was saved.");
    }

    Directory.CreateDirectory(Path.GetDirectoryName(backupFile)!);
    File.WriteAllText(backupFile, backupKey.ExportPkcs8PrivateKeyPem());

    Console.WriteLine();
    Console.WriteLine($"1. The release workflow's private key is now the {SecretName} Actions secret. It exists nowhere else.");
    Console.WriteLine($"2. The backup private key is in: {backupFile}");
    Console.WriteLine("   Move it somewhere offline (password manager, USB stick). Never commit or upload it.");
    Console.WriteLine();
    Console.WriteLine("Public keys - these are not secret. Paste everything between the lines back to Claude:");
    Console.WriteLine("----------------------------------------------------------------");
    Console.WriteLine($"CI:     {Convert.ToBase64String(ciKey.ExportSubjectPublicKeyInfo())}");
    Console.WriteLine($"BACKUP: {Convert.ToBase64String(backupKey.ExportSubjectPublicKeyInfo())}");
    Console.WriteLine("----------------------------------------------------------------");
    return 0;
}

// ---------------------------------------------------------------------------------------------
// sign: writes <manifest>.sig. The key comes from the RELEASE_SIGNING_KEY environment variable
// (the Actions secret), or from --key-file for a release signed by hand with the backup key.
// ---------------------------------------------------------------------------------------------
static int Sign(string[] args)
{
    string manifest = args.ElementAtOrDefault(1) ?? throw new ArgumentException("sign <manifest> is required.");
    string? trustedFile = Option(args, "--trusted");
    string? keyFile = Option(args, "--key-file");

    var trusted = trustedFile != null ? ReadTrustedKeys(trustedFile) : [];
    string? pem = keyFile != null ? File.ReadAllText(keyFile) : Environment.GetEnvironmentVariable(SecretName);

    if (string.IsNullOrWhiteSpace(pem))
    {
        // An app that enforces signatures can't install an unsigned release: fail the release
        // rather than publish one no installed copy will accept.
        if (trusted.Count > 0) throw new InvalidOperationException($"The app enforces release signatures but {SecretName} is not set.");
        Console.WriteLine($"{SecretName} is not set and the app has no trusted keys yet; skipping the signature.");
        return 0;
    }

    using var key = ECDsa.Create();
    key.ImportFromPem(pem);

    string publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    if (trustedFile != null && !trusted.Contains(publicKey))
    {
        throw new InvalidOperationException($"The signing key's public half is not one of the keys in {trustedFile}; installed copies would reject this release.");
    }

    byte[] data = File.ReadAllBytes(manifest);
    byte[] signature = key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
    File.WriteAllBytes(manifest + ".sig", signature);

    Console.WriteLine($"Signed {manifest} -> {manifest}.sig ({signature.Length} bytes) with key {publicKey[..16]}...");
    return 0;
}

static int VerifyCommand(string[] args)
{
    string manifest = args.ElementAtOrDefault(1) ?? throw new ArgumentException("verify <manifest> is required.");
    string trustedFile = Option(args, "--trusted") ?? throw new ArgumentException("--trusted <file> is required.");

    byte[] data = File.ReadAllBytes(manifest);
    byte[] signature = File.ReadAllBytes(manifest + ".sig");
    foreach (string publicKey in ReadTrustedKeys(trustedFile))
    {
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
        if (key.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
        {
            Console.WriteLine($"OK: signed by {publicKey[..16]}...");
            return 0;
        }
    }

    Console.Error.WriteLine("FAILED: not signed by any trusted key.");
    return 1;
}

/// The base64 P-256 SubjectPublicKeyInfo strings in the app's source (they all start "MFkwEwYH").
static List<string> ReadTrustedKeys(string sourceFile) =>
    Regex.Matches(File.ReadAllText(sourceFile), "\"(MFkwEwYH[A-Za-z0-9+/=]{100,140})\"").Select(m => m.Groups[1].Value).ToList();
