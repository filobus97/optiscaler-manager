// OptiScaler Manager - GPL-3.0-or-later. See repository LICENSE.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace OptiscalerManager.Core.Input;

/// <summary>Linux evdev constants we care about (see linux/input-event-codes.h).</summary>
public static class Evdev
{
    public const ushort EV_KEY = 0x01;
    public const ushort EV_ABS = 0x03;

    // Face buttons, in the kernel's layout-neutral naming.
    public const ushort BTN_SOUTH = 0x130;  // A / cross
    public const ushort BTN_EAST = 0x131;   // B / circle
    public const ushort BTN_TL = 0x136;     // left shoulder
    public const ushort BTN_TR = 0x137;     // right shoulder
    public const ushort BTN_SELECT = 0x13a;
    public const ushort BTN_START = 0x13b;

    public const ushort ABS_X = 0x00;       // left stick X
    public const ushort ABS_Y = 0x01;       // left stick Y
    public const ushort ABS_Z = 0x02;       // trigger, or right stick X on older pads
    public const ushort ABS_RX = 0x03;      // right stick X
    public const ushort ABS_RY = 0x04;      // right stick Y
    public const ushort ABS_RZ = 0x05;      // trigger, or right stick Y on older pads
    public const ushort ABS_HAT0X = 0x10;   // D-pad X (-1 / 0 / +1)
    public const ushort ABS_HAT0Y = 0x11;   // D-pad Y (-1 / 0 / +1)

    /// <summary>
    /// Size of <c>struct input_event</c> on 64-bit Linux:
    /// two 8-byte timeval longs + u16 type + u16 code + s32 value.
    /// (We only publish linux-x64, so this is fixed.)
    /// </summary>
    public const int EventSize = 24;
}

/// <summary>Reported range of an absolute axis (from EVIOCGABS, or a sane default).</summary>
/// <param name="Min">Minimum raw value.</param>
/// <param name="Max">Maximum raw value.</param>
public readonly record struct AxisRange(int Min, int Max)
{
    /// <summary>The range most gamepads report for a 16-bit stick axis.</summary>
    public static readonly AxisRange Default = new(-32768, 32767);

    /// <summary>
    /// Maps a raw axis value to -1..+1 around the range's centre, so the caller can
    /// use one deadzone regardless of whether the pad reports 8-bit or 16-bit axes.
    /// </summary>
    public double Normalize(int value)
    {
        if (Max <= Min) return 0;
        var centre = (Min + Max) / 2.0;
        var halfSpan = (Max - Min) / 2.0;
        return Math.Clamp((value - centre) / halfSpan, -1.0, 1.0);
    }
}

/// <summary>
/// Turns raw evdev frames into logical <see cref="GamepadInput"/> transitions.
/// Pure and deterministic — no I/O — so the whole mapping is unit-testable without
/// a physical controller.
///
/// The D-pad and the left stick are independent sources for the same directions, so
/// the decoder keeps each source's state and reports the *union*: a direction stays
/// pressed while any source holds it, and only releases when every source lets go.
///
/// The right stick is separate: it stays analog and drives scrolling
/// (see <see cref="Scroll"/>), so it never moves focus.
/// </summary>
public sealed class EvdevGamepadDecoder
{
    // Push past this to engage a direction, fall below the lower one to release it.
    // The gap is hysteresis: it stops a stick resting near the edge from chattering.
    private const double EngageThreshold = 0.5;
    private const double ReleaseThreshold = 0.35;

    /// <summary>
    /// Right-stick play ignored before scrolling starts. Larger than the navigation
    /// thresholds because a worn stick that rests slightly off-centre would otherwise
    /// scroll the page on its own, forever.
    /// </summary>
    public const double ScrollDeadzone = 0.2;

    private readonly AxisRange _xRange;
    private readonly AxisRange _yRange;
    private readonly AxisRange _rxRange;
    private readonly AxisRange _ryRange;
    private readonly ushort _scrollX;
    private readonly ushort _scrollY;

    // Current direction contributed by each source (None when centred).
    private GamepadAction _hatX, _hatY, _stickX, _stickY;
    private readonly HashSet<GamepadAction> _buttons = new();
    private readonly HashSet<GamepadAction> _reported = new();

    /// <summary>Current right-stick deflection. Analog, and zero while centred.</summary>
    public GamepadScroll Scroll { get; private set; }

    public EvdevGamepadDecoder(
        AxisRange? xRange = null, AxisRange? yRange = null,
        AxisRange? rxRange = null, AxisRange? ryRange = null,
        (ushort X, ushort Y)? scrollAxes = null)
    {
        _xRange = xRange ?? AxisRange.Default;
        _yRange = yRange ?? AxisRange.Default;
        _rxRange = rxRange ?? AxisRange.Default;
        _ryRange = ryRange ?? AxisRange.Default;
        (_scrollX, _scrollY) = scrollAxes ?? (Evdev.ABS_RX, Evdev.ABS_RY);
    }

    /// <summary>
    /// Which axes carry the right stick, given the axes a device declares.
    ///
    /// Xbox-style pads put it on ABS_RX/ABS_RY and keep ABS_Z/ABS_RZ for the analog
    /// triggers; older DirectInput-style pads have no RX/RY and use Z/RZ for the stick.
    /// Listening to both sets is not an option — on an Xbox pad that would scroll the
    /// page whenever a trigger is squeezed — so we ask the device which it has and
    /// prefer RX/RY, which is also what a controller presented by Steam Input reports.
    /// </summary>
    public static (ushort X, ushort Y) ScrollAxes(bool[]? declaredAxes)
    {
        if (declaredAxes is null) return (Evdev.ABS_RX, Evdev.ABS_RY);

        bool Has(ushort axis) => axis < declaredAxes.Length && declaredAxes[axis];

        if (Has(Evdev.ABS_RX) && Has(Evdev.ABS_RY)) return (Evdev.ABS_RX, Evdev.ABS_RY);
        if (Has(Evdev.ABS_Z) && Has(Evdev.ABS_RZ)) return (Evdev.ABS_Z, Evdev.ABS_RZ);
        return (Evdev.ABS_RX, Evdev.ABS_RY);
    }

    /// <summary>
    /// Turns a normalized axis into a scroll rate: nothing inside the deadzone, then
    /// squared so small pushes creep and large ones move — the same feel as easing a
    /// scroll wheel.
    /// </summary>
    public static double ScrollAxis(double normalized)
    {
        var magnitude = Math.Abs(normalized);
        if (magnitude <= ScrollDeadzone) return 0;

        var scaled = (magnitude - ScrollDeadzone) / (1 - ScrollDeadzone);
        return Math.Sign(normalized) * scaled * scaled;
    }

    /// <summary>Maps a button keycode to its action (None when we don't use it).</summary>
    public static GamepadAction ButtonAction(ushort code) => code switch
    {
        Evdev.BTN_SOUTH => GamepadAction.Accept,
        Evdev.BTN_START => GamepadAction.Accept,
        Evdev.BTN_EAST => GamepadAction.Cancel,
        Evdev.BTN_SELECT => GamepadAction.Cancel,
        Evdev.BTN_TR => GamepadAction.NextSection,
        Evdev.BTN_TL => GamepadAction.PrevSection,
        _ => GamepadAction.None,
    };

    /// <summary>
    /// Decodes one raw 24-byte evdev frame, returning any resulting transitions.
    /// Frames we don't care about yield nothing.
    /// </summary>
    public IReadOnlyList<GamepadInput> Feed(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < Evdev.EventSize) return Array.Empty<GamepadInput>();
        var type = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(16, 2));
        var code = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(18, 2));
        var value = BinaryPrimitives.ReadInt32LittleEndian(frame.Slice(20, 4));
        return Feed(type, code, value);
    }

    /// <summary>Decodes a decoded evdev triple, returning any resulting transitions.</summary>
    public IReadOnlyList<GamepadInput> Feed(ushort type, ushort code, int value)
    {
        switch (type)
        {
            case Evdev.EV_KEY:
                var action = ButtonAction(code);
                if (action == GamepadAction.None) break;
                // value 2 is the kernel's own auto-repeat — ignored, we do our own.
                if (value == 1) _buttons.Add(action);
                else if (value == 0) _buttons.Remove(action);
                break;

            case Evdev.EV_ABS:
                // The scroll axes are resolved per device, so they can't be switch cases.
                if (code == _scrollX)
                {
                    Scroll = Scroll with { X = ScrollAxis(_rxRange.Normalize(value)) };
                    break;
                }
                if (code == _scrollY)
                {
                    Scroll = Scroll with { Y = ScrollAxis(_ryRange.Normalize(value)) };
                    break;
                }

                switch (code)
                {
                    case Evdev.ABS_HAT0X:
                        _hatX = value < 0 ? GamepadAction.Left : value > 0 ? GamepadAction.Right : GamepadAction.None;
                        break;
                    case Evdev.ABS_HAT0Y:
                        // evdev Y grows downward.
                        _hatY = value < 0 ? GamepadAction.Up : value > 0 ? GamepadAction.Down : GamepadAction.None;
                        break;
                    case Evdev.ABS_X:
                        _stickX = Deflect(_xRange.Normalize(value), _stickX, GamepadAction.Left, GamepadAction.Right);
                        break;
                    case Evdev.ABS_Y:
                        _stickY = Deflect(_yRange.Normalize(value), _stickY, GamepadAction.Up, GamepadAction.Down);
                        break;

                }
                break;
        }

        return Diff();
    }

    /// <summary>Applies the engage/release thresholds, keeping the current direction sticky in between.</summary>
    private static GamepadAction Deflect(double normalized, GamepadAction current, GamepadAction negative, GamepadAction positive)
    {
        var magnitude = Math.Abs(normalized);
        if (current == GamepadAction.None)
            return magnitude >= EngageThreshold
                ? (normalized < 0 ? negative : positive)
                : GamepadAction.None;

        // Already deflected: hold until it falls back inside the release threshold,
        // and allow a straight flip to the opposite direction.
        if (magnitude < ReleaseThreshold) return GamepadAction.None;
        return normalized < 0 ? negative : positive;
    }

    private IReadOnlyList<GamepadInput> Diff()
    {
        var active = new HashSet<GamepadAction>(_buttons);
        foreach (var d in new[] { _hatX, _hatY, _stickX, _stickY })
            if (d != GamepadAction.None) active.Add(d);

        List<GamepadInput>? changes = null;
        foreach (var a in active)
            if (!_reported.Contains(a))
                (changes ??= new()).Add(new GamepadInput(a, true));
        foreach (var a in _reported)
            if (!active.Contains(a))
                (changes ??= new()).Add(new GamepadInput(a, false));

        if (changes is null) return Array.Empty<GamepadInput>();
        _reported.Clear();
        foreach (var a in active) _reported.Add(a);
        return changes;
    }
}
