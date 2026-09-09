using Microsoft.Win32;

namespace TrayTrigger.Services;

/// <summary>
/// Shared registry-access helpers for platform scanners. Extracted once GOG, EA, and Ubisoft
/// each had their own identical <c>OpenLocalMachine32</c> (see docs/adding-a-platform-integration.md).
/// </summary>
public static class RegistryHelper
{
    /// <summary>
    /// Opens HKLM under the 32-bit registry view - every platform integrated so far installs
    /// via a 32-bit installer, so its keys always live under WOW6432Node on 64-bit Windows;
    /// <see cref="RegistryView.Registry32"/> reads that view transparently without needing to
    /// hardcode "WOW6432Node" in any key path.
    /// </summary>
    public static RegistryKey OpenLocalMachine32() =>
        RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
}
