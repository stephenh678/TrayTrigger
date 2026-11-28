using System;
using System.Runtime.InteropServices;

namespace TrayTrigger.Services;

/// <summary>What one read of the game controllers found: the buttons held on any of them, and the left stick of the first that has it pushed.</summary>
public readonly record struct PadState(ushort Buttons, short LeftX, short LeftY);

/// <summary>
/// Reads Xbox-style controllers (and the built-in controls of handhelds that present as one) through XInput, the
/// way Windows games do. Reading is passive: it takes nothing away from a game, Steam Input or Game Bar.
/// </summary>
public sealed class XInputReader
{
    public const ushort DPadUp = 0x0001, DPadDown = 0x0002, DPadLeft = 0x0004, DPadRight = 0x0008;
    public const ushort Start = 0x0010, Back = 0x0020, LeftShoulder = 0x0100, RightShoulder = 0x0200;
    public const ushort A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000;

    private const uint ErrorSuccess = 0;
    private const int Slots = 4;
    // XInputGetState on an empty slot is slow, so an empty slot is looked at again only this often.
    private static readonly TimeSpan EmptySlotRecheck = TimeSpan.FromSeconds(2);

    private readonly DateTime[] _nextLook = new DateTime[Slots];
    private bool _unavailable;

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad
    {
        public ushort Buttons;
        public byte LeftTrigger, RightTrigger;
        public short ThumbLX, ThumbLY, ThumbRX, ThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint PacketNumber;
        public XInputGamepad Gamepad;
    }

    // From System32 only, never a copy beside TrayTrigger.exe.
    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint XInputGetState(uint userIndex, out XInputState state);

    /// <summary>The controllers' state, or null when none is connected (or this Windows has no XInput).</summary>
    public PadState? Read()
    {
        if (_unavailable) return null;
        var now = DateTime.UtcNow;
        bool any = false;
        ushort buttons = 0;
        short lx = 0, ly = 0;
        for (uint slot = 0; slot < Slots; slot++)
        {
            if (now < _nextLook[slot]) continue;
            uint result;
            XInputState state;
            try
            {
                result = XInputGetState(slot, out state);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                LoggingService.Swallowed("Controller", ex, "reading controllers through XInput; controller navigation is off for this run");
                _unavailable = true;
                return null;
            }
            if (result != ErrorSuccess)
            {
                _nextLook[slot] = now + EmptySlotRecheck;
                continue;
            }
            any = true;
            buttons |= state.Gamepad.Buttons;
            if (lx == 0 && ly == 0 && (Math.Abs((int)state.Gamepad.ThumbLX) > 8000 || Math.Abs((int)state.Gamepad.ThumbLY) > 8000))
            {
                lx = state.Gamepad.ThumbLX;
                ly = state.Gamepad.ThumbLY;
            }
        }
        return any ? new PadState(buttons, lx, ly) : null;
    }
}
