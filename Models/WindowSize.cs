namespace TrayTrigger.Models;

/// <summary>A window's size (device-independent units) as the player last left it. See <see cref="Services.WindowSizeMemory"/>.</summary>
public sealed class WindowSize
{
    public double Width { get; set; }
    public double Height { get; set; }
}
