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
}
