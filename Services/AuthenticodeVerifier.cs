using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace TrayTrigger.Services;

/// <summary>
/// Authenticode checks for downloaded installers. Two questions are answered separately:
/// <list type="bullet">
/// <item><see cref="GetSignerSubject"/>: who signed the file (null when it carries no signature).</item>
/// <item><see cref="IsTrusted"/>: does Windows accept that signature (intact, chains to a trusted
/// root, not expired at signing time) — the same verdict Explorer's Digital Signatures tab gives.</item>
/// </list>
/// The updater combines them as "an update must be signed by whoever signed the running exe":
/// no publisher name is hard-coded, so a certificate rotation with the same subject keeps
/// working, and an unsigned development build never blocks itself.
/// </summary>
public static class AuthenticodeVerifier
{
    /// <summary>
    /// The subject distinguished name of the certificate that signed <paramref name="path"/>,
    /// or null if the file has no embedded Authenticode signature. Does <b>not</b> validate the
    /// signature; pair with <see cref="IsTrusted"/>.
    /// </summary>
    public static string? GetSignerSubject(string path)
    {
        try
        {
            using var cert = X509Certificate2.CreateFromSignedFile(path);
            return cert.Subject;
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or IOException or UnauthorizedAccessException)
        {
            // CryptographicException is the "no signature" case (CRYPT_E_NO_MATCH).
            return null;
        }
    }

    /// <summary>
    /// True when WinVerifyTrust accepts the file's Authenticode signature. Revocation is checked
    /// from the local cache only, so a machine without internet access (or with a slow CRL
    /// server) doesn't stall the update dialog.
    /// </summary>
    public static bool IsTrusted(string path)
    {
        if (!OperatingSystem.IsWindows()) return false;

        var fileInfo = new WINTRUST_FILE_INFO
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            pcwszFilePath = path,
            hFile = IntPtr.Zero,
            pgKnownSubject = IntPtr.Zero
        };

        IntPtr fileInfoPtr = Marshal.AllocHGlobal((int)fileInfo.cbStruct);
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPtr, false);

            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                pPolicyCallbackData = IntPtr.Zero,
                pSIPClientData = IntPtr.Zero,
                dwUIChoice = WTD_UI_NONE,
                fdwRevocationChecks = WTD_REVOKE_NONE,
                dwUnionChoice = WTD_CHOICE_FILE,
                pFile = fileInfoPtr,
                dwStateAction = WTD_STATEACTION_VERIFY,
                hWVTStateData = IntPtr.Zero,
                pwszURLReference = IntPtr.Zero,
                dwProvFlags = WTD_CACHE_ONLY_URL_RETRIEVAL,
                dwUIContext = 0
            };

            Guid action = WINTRUST_ACTION_GENERIC_VERIFY_V2;
            int result = WinVerifyTrust(IntPtr.Zero, ref action, ref data);

            // Release the state handle WinVerifyTrust allocated for the VERIFY action.
            data.dwStateAction = WTD_STATEACTION_CLOSE;
            WinVerifyTrust(IntPtr.Zero, ref action, ref data);

            return result == 0;
        }
        catch (Exception ex)
        {
            LoggingService.Warn("Authenticode", $"WinVerifyTrust failed for {path}: {ex.Message}");
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(fileInfoPtr);
        }
    }

    #region WinTrust interop

    private static readonly Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_NONE = 0;
    private const uint WTD_CHOICE_FILE = 1;
    private const uint WTD_STATEACTION_VERIFY = 1;
    private const uint WTD_STATEACTION_CLOSE = 2;
    private const uint WTD_CACHE_ONLY_URL_RETRIEVAL = 0x1000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, ref WINTRUST_DATA pWVTData);

    #endregion
}
