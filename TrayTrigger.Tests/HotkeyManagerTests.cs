using System.Windows.Input;
using TrayTrigger.Services;

namespace TrayTrigger.Tests;

public class HotkeyManagerTests
{
    private static uint VkOf(Key key) => (uint)KeyInterop.VirtualKeyFromKey(key);

    [Theory]
    [InlineData("Ctrl+5")]
    [InlineData("Ctrl+Alt+G")]
    [InlineData("F10")]
    [InlineData("Ctrl+Alt+5")]
    public void ParseHotkey_AcceptsValidCombos(string input)
    {
        Assert.True(HotkeyManager.ParseHotkey(input, out _, out uint vk));
        Assert.NotEqual(0u, vk);
    }

    [Fact]
    public void ParseHotkey_DigitMapsToNumberRowKey_NotEnumOrdinal()
    {
        // "5" must map to Key.D5, not Enum.TryParse's raw underlying value (which would be
        // the unrelated Key.Clear). See H-02.
        Assert.True(HotkeyManager.ParseHotkey("Ctrl+5", out _, out uint vk));
        Assert.Equal(VkOf(Key.D5), vk);
    }

    [Fact]
    public void ParseHotkey_StandaloneFunctionKey_IsAllowedWithoutModifier()
    {
        Assert.True(HotkeyManager.ParseHotkey("F10", out uint mods, out uint vk));
        Assert.Equal(0u, mods);
        Assert.Equal(VkOf(Key.F10), vk);
    }

    [Theory]
    [InlineData("Ctrl+")]
    [InlineData("5")] // bare digit, no modifier, not a function key - must be rejected. See H-01.
    [InlineData("c")] // bare letter, no modifier - must be rejected. See H-01.
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Ctrl+12")] // multi-digit main key has no real key equivalent. See H-02.
    public void ParseHotkey_RejectsInvalidOrUnmodifiedCombos(string input)
    {
        // virtualKey is always left at 0 (its default) on every rejection path. modifiers is
        // NOT reset on the "Ctrl+12" path specifically - the modifier loop runs and sets it
        // before the multi-digit main-key check rejects the string - so only vk is asserted
        // here; that's still fine for callers, since ok=false means neither out param should
        // be trusted regardless of its value.
        Assert.False(HotkeyManager.ParseHotkey(input, out _, out uint vk));
        Assert.Equal(0u, vk);
    }

    /// <summary>
    /// "Cmd+G" used to register as a bare G (then be refused for lacking a modifier), and
    /// "Ctrl+Foo+G" as Ctrl+G with the typo silently dropped. An unknown modifier is a rejection.
    /// </summary>
    [Theory]
    [InlineData("Cmd+G")]
    [InlineData("Ctrl+Foo+G")]
    [InlineData("Meta+F5")]
    public void ParseHotkey_RejectsUnknownModifierTokens(string input)
    {
        Assert.False(HotkeyManager.ParseHotkey(input, out _, out _));
    }

    [Theory]
    [InlineData(ModifierKeys.Control | ModifierKeys.Alt, Key.G, "Ctrl+Alt+G")]
    [InlineData(ModifierKeys.Shift | ModifierKeys.Control, Key.D5, "Ctrl+Shift+5")]
    [InlineData(ModifierKeys.None, Key.F10, "F10")]
    [InlineData(ModifierKeys.Windows | ModifierKeys.Alt, Key.NumPad1, "Alt+Win+NumPad1")]
    public void Format_WritesModifiersInCanonicalOrder(ModifierKeys modifiers, Key key, string expected)
    {
        Assert.Equal(expected, HotkeyManager.Format(modifiers, key));
    }

    [Theory]
    [InlineData(Key.LeftCtrl)]
    [InlineData(Key.RightAlt)]
    [InlineData(Key.LeftShift)]
    [InlineData(Key.LWin)]
    [InlineData(Key.System)]
    [InlineData(Key.None)]
    public void Format_ReturnsNullForAModifierOnItsOwn(Key key)
    {
        Assert.Null(HotkeyManager.Format(ModifierKeys.Control, key));
    }

    /// <summary>Every spelling a hand-typed value could have collapses to one, so the card and the tray agree.</summary>
    [Theory]
    [InlineData("ctrl + alt+g", "Ctrl+Alt+G")]
    [InlineData("SHIFT+CONTROL+5", "Ctrl+Shift+5")]
    [InlineData("Ctrl+Alt+G", "Ctrl+Alt+G")]
    [InlineData("f10", "F10")]
    public void Normalize_RewritesInCanonicalForm(string input, string expected)
    {
        Assert.Equal(expected, HotkeyManager.Normalize(input));
    }

    [Theory]
    [InlineData("Cmd+G")]
    [InlineData("c")]
    [InlineData("")]
    public void Normalize_ReturnsNullForWhatDoesNotParse(string input)
    {
        Assert.Null(HotkeyManager.Normalize(input));
    }

    /// <summary>Combos that would take a shell function away from the whole machine are refused outright.</summary>
    [Theory]
    [InlineData(ModifierKeys.Alt, Key.F4)]
    [InlineData(ModifierKeys.Alt, Key.Tab)]
    [InlineData(ModifierKeys.Alt | ModifierKeys.Shift, Key.Tab)]
    [InlineData(ModifierKeys.Control, Key.Escape)]
    [InlineData(ModifierKeys.Control | ModifierKeys.Shift, Key.Escape)]
    [InlineData(ModifierKeys.Control | ModifierKeys.Alt, Key.Delete)]
    [InlineData(ModifierKeys.Windows, Key.G)]
    [InlineData(ModifierKeys.Control | ModifierKeys.Windows, Key.D)]
    [InlineData(ModifierKeys.Alt, Key.Snapshot)]
    public void IsReservedByWindows_RefusesShellCombos(ModifierKeys modifiers, Key key)
    {
        Assert.True(HotkeyManager.IsReservedByWindows(modifiers, key));
    }

    [Theory]
    [InlineData(ModifierKeys.Control | ModifierKeys.Alt, Key.G)]
    [InlineData(ModifierKeys.Control | ModifierKeys.Alt, Key.F4)] // only bare Alt+F4 is the shell's
    [InlineData(ModifierKeys.Control, Key.Tab)]
    [InlineData(ModifierKeys.None, Key.F10)]
    public void IsReservedByWindows_LeavesOrdinaryCombosAlone(ModifierKeys modifiers, Key key)
    {
        Assert.False(HotkeyManager.IsReservedByWindows(modifiers, key));
    }

    /// <summary>One modifier plus a letter, digit or Enter is warned about; two modifiers or a function key are not.</summary>
    [Theory]
    [InlineData(ModifierKeys.Control, Key.S, true)]
    [InlineData(ModifierKeys.Alt, Key.D, true)]
    [InlineData(ModifierKeys.Shift, Key.D5, true)]
    [InlineData(ModifierKeys.Control, Key.Enter, true)]
    [InlineData(ModifierKeys.Control | ModifierKeys.Alt, Key.S, false)]
    [InlineData(ModifierKeys.Control | ModifierKeys.Shift, Key.G, false)]
    [InlineData(ModifierKeys.Control, Key.F5, false)]
    [InlineData(ModifierKeys.None, Key.F9, false)]
    public void IsCommonAppShortcut_FlagsSingleModifierPlusLetterOrDigit(ModifierKeys modifiers, Key key, bool expected)
    {
        Assert.Equal(expected, HotkeyManager.IsCommonAppShortcut(modifiers, key));
    }

    /// <summary>Format and ParseHotkey are inverses: what the recorder writes, registration reads back to the same keys.</summary>
    [Theory]
    [InlineData(ModifierKeys.Control | ModifierKeys.Alt, Key.G)]
    [InlineData(ModifierKeys.Shift, Key.D0)]
    [InlineData(ModifierKeys.None, Key.F24)]
    [InlineData(ModifierKeys.Control, Key.OemPeriod)]
    public void Format_RoundTripsThroughParse(ModifierKeys modifiers, Key key)
    {
        string text = HotkeyManager.Format(modifiers, key)!;
        Assert.True(HotkeyManager.ParseHotkey(text, out _, out uint vk));
        Assert.Equal(VkOf(key), vk);
        Assert.Equal(text, HotkeyManager.Normalize(text));
    }
}
