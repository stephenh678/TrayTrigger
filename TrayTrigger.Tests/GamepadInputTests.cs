using TrayTrigger.Services;

namespace TrayTrigger.Tests;

public class GamepadInputTests
{
    private static PadState Pad(ushort buttons = 0, short lx = 0, short ly = 0) => new(buttons, lx, ly);

    private static GamepadInput Started(PadState? at = null)
    {
        var input = new GamepadInput();
        input.Reset(at ?? Pad(), 0);
        return input;
    }

    [Fact]
    public void A_held_direction_says_it_is_a_repeat()
    {
        var input = Started();
        Assert.Equal(new[] { PadAction.Up }, input.Update(Pad(XInputReader.DPadUp), 10));
        Assert.False(input.LastDirectionRepeated); // the press itself
        Assert.Equal(new[] { PadAction.Up }, input.Update(Pad(XInputReader.DPadUp), 10 + GamepadInput.RepeatDelayMs));
        Assert.True(input.LastDirectionRepeated);
        Assert.Empty(input.Update(Pad(), 1000));
        Assert.False(input.LastDirectionRepeated);
        Assert.Equal(new[] { PadAction.Up }, input.Update(Pad(XInputReader.DPadUp), 1010));
        Assert.False(input.LastDirectionRepeated); // pressed again: a new press
    }

    [Fact]
    public void A_button_acts_once_per_press()
    {
        var input = Started();
        Assert.Equal(new[] { PadAction.Accept }, input.Update(Pad(XInputReader.A), 10));
        Assert.Empty(input.Update(Pad(XInputReader.A), 2000)); // held: no repeat
        Assert.Empty(input.Update(Pad(), 2010));
        Assert.Equal(new[] { PadAction.Accept }, input.Update(Pad(XInputReader.A), 2020));
    }

    [Theory]
    [InlineData(XInputReader.B, PadAction.Back)]
    [InlineData(XInputReader.X, PadAction.Play)]
    [InlineData(XInputReader.Start, PadAction.Menu)]
    [InlineData(XInputReader.LeftShoulder, PadAction.PreviousTab)]
    [InlineData(XInputReader.RightShoulder, PadAction.NextTab)]
    public void Each_button_has_its_action(ushort button, PadAction action) =>
        Assert.Equal(new[] { action }, Started().Update(Pad(button), 10));

    [Fact]
    public void Y_and_View_do_nothing() =>
        Assert.Empty(Started().Update(Pad(XInputReader.Y | XInputReader.Back), 10));

    [Fact]
    public void Held_direction_repeats_after_a_delay_like_a_key()
    {
        var input = Started();
        Assert.Equal(new[] { PadAction.Down }, input.Update(Pad(XInputReader.DPadDown), 0));
        Assert.Empty(input.Update(Pad(XInputReader.DPadDown), GamepadInput.RepeatDelayMs - 1));
        Assert.Equal(new[] { PadAction.Down }, input.Update(Pad(XInputReader.DPadDown), GamepadInput.RepeatDelayMs));
        Assert.Empty(input.Update(Pad(XInputReader.DPadDown), GamepadInput.RepeatDelayMs + 50));
        Assert.Equal(new[] { PadAction.Down }, input.Update(Pad(XInputReader.DPadDown), GamepadInput.RepeatDelayMs + GamepadInput.RepeatIntervalMs));
    }

    [Fact]
    public void Changing_direction_acts_at_once()
    {
        var input = Started();
        input.Update(Pad(XInputReader.DPadDown), 0);
        Assert.Equal(new[] { PadAction.Right }, input.Update(Pad(XInputReader.DPadRight), 30));
    }

    [Fact]
    public void What_is_held_when_reading_starts_waits_for_release()
    {
        // A held in a game, then TrayTrigger comes to the front: nothing until it is let go and pressed again.
        var input = Started(Pad(XInputReader.A | XInputReader.DPadLeft));
        Assert.Empty(input.Update(Pad(XInputReader.A | XInputReader.DPadLeft), 1000));
        Assert.Empty(input.Update(Pad(), 1010));
        Assert.Equal(new[] { PadAction.Left, PadAction.Accept }, input.Update(Pad(XInputReader.A | XInputReader.DPadLeft), 1020));
    }

    [Theory]
    [InlineData(30000, 0, PadAction.Right)]
    [InlineData(-30000, 0, PadAction.Left)]
    [InlineData(0, 30000, PadAction.Up)]     // XInput's Y is up-positive
    [InlineData(0, -30000, PadAction.Down)]
    [InlineData(25000, -30000, PadAction.Down)] // the stronger axis wins
    public void Left_stick_pushed_well_over_is_a_direction(short x, short y, PadAction expected) =>
        Assert.Equal(expected, GamepadInput.DirectionOf(Pad(lx: x, ly: y)));

    [Fact]
    public void Stick_resting_off_centre_is_no_direction() =>
        Assert.Null(GamepadInput.DirectionOf(Pad(lx: 12000, ly: -9000)));

    [Fact]
    public void DPad_wins_over_the_stick() =>
        Assert.Equal(PadAction.Up, GamepadInput.DirectionOf(Pad(XInputReader.DPadUp, lx: 30000)));
}
