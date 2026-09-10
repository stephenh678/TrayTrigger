using System;

namespace TrayTrigger.Models;

public enum TweakCategory
{
    InputAndDisplay,
    CpuAndPower,
    NetworkAndBackground,
    SecurityAndAdvanced
}

public class SystemTweakItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public TweakCategory Category { get; set; }
    public string ShortDescription { get; set; } = "";
    public string WhyItMatters { get; set; } = "";
    public bool IsOptimal { get; set; }
    public string StatusText { get; set; } = "";
    /// <summary>Off by default and skipped by Apply Performance Preset: a real trade-off, the user's call.</summary>
    public bool IsOptIn { get; set; }
    public bool RequiresAdmin { get; set; }
    public bool RequiresReboot { get; set; }
    public bool CanToggle { get; set; } = true;
    public bool HasCustomAction { get; set; }
    public string CustomActionLabel { get; set; } = "";
    public string RegistryKeyPath { get; set; } = "";

    /// <summary>
    /// Status-only rows (Core Isolation): shown with a neutral ON/OFF badge, never counted in the
    /// "optimizations active" score, since "optimal" would mean turning a security feature off.
    /// </summary>
    public bool IsInformational { get; set; }

    /// <summary>
    /// False when the tweak cannot do anything on this machine (Windows 10 for a Windows 11-only
    /// graphics setting, a GPU/driver without HAGS support, no HDR display). The row stays
    /// visible so the user learns why, but its toggle is disabled and the preset skips it.
    /// </summary>
    public bool IsAvailable { get; set; } = true;
    public string UnavailableReason { get; set; } = "";

    /// <summary>Counted in the score and applied by the preset: available, toggleable, not opt-in, not informational.</summary>
    public bool IsRecommended => IsAvailable && CanToggle && !IsOptIn && !IsInformational;
}
