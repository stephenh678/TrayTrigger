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
    public bool RequiresAdmin { get; set; }
    public bool RequiresReboot { get; set; }
    public bool CanToggle { get; set; } = true;
    public bool HasCustomAction { get; set; }
    public string CustomActionLabel { get; set; } = "";
    public string RegistryKeyPath { get; set; } = "";
}
