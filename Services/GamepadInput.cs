using System;
using System.Collections.Generic;

namespace TrayTrigger.Services;

/// <summary>What a controller press asks the Library or Game Details to do.</summary>
public enum PadAction { Up, Down, Left, Right, Accept, Back, Play, Menu, PreviousTab, NextTab }

/// <summary>
/// Turns controller readings into actions: a button acts once when pressed, and a direction (D-pad or left stick)
/// acts when pressed and then repeats while held, as a keyboard's arrow keys do. What is already held when reading
/// starts is ignored until it is let go, so a press made in a game, or the one that brought TrayTrigger forward,
/// does nothing here.
/// </summary>
public sealed class GamepadInput
{
    public const int RepeatDelayMs = 400;
    public const int RepeatIntervalMs = 110;
    // Most of the way over, so a stick that drifts or rests slightly off centre never moves anything.
    private const int StickThreshold = 16000;

    private static readonly (ushort Button, PadAction Action)[] Buttons =
    {
        (XInputReader.A, PadAction.Accept),
        (XInputReader.B, PadAction.Back),
        (XInputReader.X, PadAction.Play),
        (XInputReader.Start, PadAction.Menu),
        (XInputReader.LeftShoulder, PadAction.PreviousTab),
        (XInputReader.RightShoulder, PadAction.NextTab),
    };

    private ushort _held;
    private PadAction? _direction;
    private bool _directionBlocked;
    private long _directionSince, _lastRepeat;

    /// <summary>Starts from this reading: whatever it holds is ignored until released.</summary>
    public void Reset(PadState state, long nowMs)
    {
        _held = state.Buttons;
        _direction = DirectionOf(state);
        _directionBlocked = _direction != null;
        _directionSince = _lastRepeat = nowMs;
    }

    public List<PadAction> Update(PadState state, long nowMs)
    {
        var actions = new List<PadAction>();

        var direction = DirectionOf(state);
        if (direction != _direction)
        {
            _direction = direction;
            _directionBlocked = false;
            _directionSince = _lastRepeat = nowMs;
            if (direction is { } d) actions.Add(d);
        }
        else if (direction is { } held && !_directionBlocked
                 && nowMs - _directionSince >= RepeatDelayMs && nowMs - _lastRepeat >= RepeatIntervalMs)
        {
            _lastRepeat = nowMs;
            actions.Add(held);
        }

        ushort pressed = (ushort)(state.Buttons & ~_held);
        _held = state.Buttons;
        foreach (var (button, action) in Buttons)
            if ((pressed & button) != 0) actions.Add(action);
        return actions;
    }

    /// <summary>The D-pad if pressed, else the left stick when pushed well over, along its stronger axis.</summary>
    public static PadAction? DirectionOf(PadState state)
    {
        ushort b = state.Buttons;
        if ((b & XInputReader.DPadUp) != 0) return PadAction.Up;
        if ((b & XInputReader.DPadDown) != 0) return PadAction.Down;
        if ((b & XInputReader.DPadLeft) != 0) return PadAction.Left;
        if ((b & XInputReader.DPadRight) != 0) return PadAction.Right;

        int x = state.LeftX, y = state.LeftY;
        if (Math.Max(Math.Abs(x), Math.Abs(y)) < StickThreshold) return null;
        if (Math.Abs(x) > Math.Abs(y)) return x > 0 ? PadAction.Right : PadAction.Left;
        return y > 0 ? PadAction.Up : PadAction.Down; // XInput's Y is up-positive
    }
}
