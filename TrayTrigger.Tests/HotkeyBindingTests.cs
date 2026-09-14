using TrayTrigger.Models;
using TrayTrigger.Services;

namespace TrayTrigger.Tests;

/// <summary>The list <see cref="HotkeyManager.RegisterHotkeys"/> works from: what a game supplies, and the order combos are claimed in.</summary>
public class HotkeyBindingTests
{
    [Fact]
    public void ForGame_CarriesIdNameHotkeyAndGameKind()
    {
        var binding = HotkeyBinding.ForGame(new GameEntry { Id = "g1", Name = "Elden Ring", Hotkey = "Ctrl+Alt+E" });

        Assert.Equal("g1", binding.OwnerId);
        Assert.Equal("Elden Ring", binding.OwnerName);
        Assert.Equal("Ctrl+Alt+E", binding.Hotkey);
        Assert.Equal(HotkeyOwnerKind.Game, binding.Kind);
    }

    [Fact]
    public void RegistrationOrder_PutsGamesBeforeTools_SoAGameKeepsASharedCombo()
    {
        var bindings = new[]
        {
            new HotkeyBinding("t1", "Vortex", "Ctrl+Alt+V", HotkeyOwnerKind.Tool),
            new HotkeyBinding("g1", "Valheim", "Ctrl+Alt+V", HotkeyOwnerKind.Game),
            new HotkeyBinding("t2", "DLSS Swapper", "Ctrl+Alt+D", HotkeyOwnerKind.Tool),
            new HotkeyBinding("g2", "Doom", "Ctrl+Alt+F", HotkeyOwnerKind.Game),
        };

        var ordered = HotkeyBinding.InRegistrationOrder(bindings).Select(b => b.OwnerId).ToList();

        Assert.Equal(["g1", "g2", "t1", "t2"], ordered);
    }

    [Fact]
    public void RegistrationOrder_SkipsEntriesWithoutAHotkey()
    {
        var bindings = new[]
        {
            new HotkeyBinding("g1", "A", "", HotkeyOwnerKind.Game),
            new HotkeyBinding("g2", "B", "   ", HotkeyOwnerKind.Game),
            new HotkeyBinding("g3", "C", "F9", HotkeyOwnerKind.Game),
            new HotkeyBinding("t1", "D", "", HotkeyOwnerKind.Tool),
        };

        Assert.Equal(["g3"], HotkeyBinding.InRegistrationOrder(bindings).Select(b => b.OwnerId));
    }
}
