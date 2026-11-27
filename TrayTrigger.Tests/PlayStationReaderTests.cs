using TrayTrigger.Services;
using TrayTrigger.Views;

namespace TrayTrigger.Tests;

public class PlayStationReaderTests
{
    private const ushort DualSense = 0x0CE6, DualShock4 = 0x09CC;

    // A DualSense over Bluetooth before anything switched it to full reports, as read from a real one: sticks
    // centred, D-pad centred (8), nothing pressed.
    private static byte[] BluetoothSimple(byte face = 0x08, byte more = 0, byte lx = 0x80, byte ly = 0x83)
    {
        var r = new byte[78];
        r[0] = 0x01; r[1] = lx; r[2] = ly; r[3] = 0x80; r[4] = 0x84; r[5] = face; r[6] = more;
        return r;
    }

    [Fact]
    public void Resting_controller_reads_as_nothing_pressed()
    {
        Assert.Equal(new PadState(0, 0, 0), PlayStationReader.Parse(BluetoothSimple(), DualSense, bluetooth: true));
    }

    [Theory]
    [InlineData(0x28, XInputReader.A)]                    // Cross, D-pad centred
    [InlineData(0x48, XInputReader.B)]                    // Circle
    [InlineData(0x18, XInputReader.X)]                    // Square
    [InlineData(0x88, XInputReader.Y)]                    // Triangle
    [InlineData(0x00, XInputReader.DPadUp)]
    [InlineData(0x02, XInputReader.DPadRight)]
    [InlineData(0x04, XInputReader.DPadDown)]
    [InlineData(0x06, XInputReader.DPadLeft)]
    [InlineData(0x03, XInputReader.DPadDown | XInputReader.DPadRight)]
    public void Face_buttons_and_dpad_map_to_the_xbox_layout(byte face, ushort expected)
    {
        Assert.Equal(expected, PlayStationReader.Parse(BluetoothSimple(face), DualSense, true)!.Value.Buttons);
    }

    [Theory]
    [InlineData(0x01, XInputReader.LeftShoulder)]
    [InlineData(0x02, XInputReader.RightShoulder)]
    [InlineData(0x10, XInputReader.Back)]  // Create
    [InlineData(0x20, XInputReader.Start)] // Options
    public void Shoulders_and_options_map_to_the_xbox_layout(byte more, ushort expected)
    {
        Assert.Equal(expected, PlayStationReader.Parse(BluetoothSimple(more: more), DualSense, true)!.Value.Buttons);
    }

    [Fact]
    public void Stick_up_is_positive_and_small_moves_are_ignored()
    {
        var up = PlayStationReader.Parse(BluetoothSimple(ly: 0), DualSense, true)!.Value;
        Assert.Equal(short.MaxValue, up.LeftY);
        var right = PlayStationReader.Parse(BluetoothSimple(lx: 0xFF), DualSense, true)!.Value;
        Assert.True(right.LeftX > 30000);
        Assert.Equal(0, PlayStationReader.Parse(BluetoothSimple(lx: 0x90), DualSense, true)!.Value.LeftX);
    }

    [Fact]
    public void Full_reports_read_from_their_own_offsets()
    {
        var usb = new byte[64];
        usb[0] = 0x01; usb[1] = usb[2] = usb[3] = usb[4] = 0x80; usb[8] = 0x28; usb[9] = 0x02;
        Assert.Equal(XInputReader.A | XInputReader.RightShoulder, PlayStationReader.Parse(usb, DualSense, bluetooth: false)!.Value.Buttons);

        var bt = new byte[78];
        bt[0] = 0x31; bt[2] = bt[3] = bt[4] = bt[5] = 0x80; bt[9] = 0x48;
        Assert.Equal(XInputReader.B, PlayStationReader.Parse(bt, DualSense, true)!.Value.Buttons);

        var ds4 = new byte[78];
        ds4[0] = 0x11; ds4[3] = ds4[4] = ds4[5] = ds4[6] = 0x80; ds4[7] = 0x18;
        Assert.Equal(XInputReader.X, PlayStationReader.Parse(ds4, DualShock4, true)!.Value.Buttons);
    }

    [Fact]
    public void Wireless_adapter_with_no_controller_is_not_read_as_one()
    {
        const ushort adapter = 0x0BA0;
        var report = new byte[64];
        report[0] = 0x01; // zeros otherwise: read as a controller, that would be D-pad Up and the stick up-left
        report[31] = 0x04; // the adapter's "no controller connected"
        Assert.Null(PlayStationReader.Parse(report, adapter, bluetooth: false));

        report[31] = 0;
        report[1] = report[2] = report[3] = report[4] = 0x80;
        report[5] = 0x28; // Cross, D-pad centred
        Assert.Equal(new PadState(XInputReader.A, 0, 0), PlayStationReader.Parse(report, adapter, false));
    }

    [Fact]
    public void Other_reports_are_ignored()
    {
        Assert.Null(PlayStationReader.Parse(new byte[] { 0x05, 0, 0 }, DualSense, true));
        Assert.Null(PlayStationReader.Parse(new byte[] { 0x01, 0x80 }, DualSense, true));
    }

    [Fact]
    public void Both_kinds_of_controller_are_read_as_one()
    {
        Assert.Null(GamepadNavigator.Combine(() => null, () => null)());
        var ps = new PadState(XInputReader.A, 0, 20000);
        Assert.Equal(ps, GamepadNavigator.Combine(() => null, () => ps)());
        var both = GamepadNavigator.Combine(() => new PadState(XInputReader.B, 0, 0), () => ps)();
        Assert.Equal(new PadState(XInputReader.A | XInputReader.B, 0, 20000), both);
    }
}
