// OptiScaler Manager - GPL-3.0-or-later. See repository LICENSE.
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace OptiscalerManager.Core.Input;

/// <summary>
/// The few evdev ioctls we need to identify a controller.
///
/// Asking the device what it *is* beats trusting udev's naming: virtual pads (the
/// kind Steam creates when it takes over a controller in Gaming Mode) often have no
/// <c>/dev/input/by-id</c> symlink at all, so a name-based scan misses them entirely.
/// </summary>
internal static unsafe partial class EvdevIoctl
{
    [LibraryImport("libc", SetLastError = true)]
    private static partial int ioctl(int fd, nuint request, void* argp);

    // _IOC(dir=2 (read), type='E', nr, size)
    private static nuint Ior(uint nr, uint size) =>
        (nuint)((2u << 30) | (size << 16) | ((uint)'E' << 8) | nr);

    private static nuint EviocgName(uint len) => Ior(0x06, len);
    private static nuint EviocgBit(uint ev, uint len) => Ior(0x20 + ev, len);
    private static nuint EviocgAbs(ushort axis) => Ior(0x40u + axis, 24);

    [StructLayout(LayoutKind.Sequential)]
    private struct AbsInfo
    {
        public int Value, Minimum, Maximum, Fuzz, Flat, Resolution;
    }

    private static int Fd(FileStream s) => s.SafeFileHandle.DangerousGetHandle().ToInt32();

    /// <summary>The device's self-reported name (EVIOCGNAME), or null if unavailable.</summary>
    public static string? TryGetName(FileStream stream)
    {
        try
        {
            var buf = stackalloc byte[256];
            var len = ioctl(Fd(stream), EviocgName(256), buf);
            if (len <= 0) return null;
            var span = new ReadOnlySpan<byte>(buf, len);
            var end = span.IndexOf((byte)0);
            if (end >= 0) span = span[..end];
            var name = Encoding.UTF8.GetString(span).Trim();
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch { return null; }
    }

    /// <summary>
    /// Whether the device reports gamepad buttons (BTN_SOUTH — the "A"/cross every
    /// pad has). Returns null when the device can't be probed at all, so callers can
    /// fall back to a name heuristic instead of silently discarding it.
    /// </summary>
    public static bool? TryIsGamepad(FileStream stream)
    {
        try
        {
            // KEY_MAX is 0x2ff, so 96 bytes covers every key/button bit.
            const int bytes = 96;
            var bits = stackalloc byte[bytes];
            for (var i = 0; i < bytes; i++) bits[i] = 0;

            if (ioctl(Fd(stream), EviocgBit(Evdev.EV_KEY, bytes), bits) < 0) return null;

            const int btn = Evdev.BTN_SOUTH;               // 0x130
            return (bits[btn / 8] & (1 << (btn % 8))) != 0;
        }
        catch { return null; }
    }

    /// <summary>
    /// Which absolute axes the device declares (EVIOCGBIT(EV_ABS)), indexed by axis
    /// code. Null when the device can't be probed, so callers keep their defaults.
    /// </summary>
    public static bool[]? TryGetAbsAxes(FileStream stream)
    {
        try
        {
            // ABS_MAX is 0x3f, so 8 bytes covers every axis bit.
            const int bytes = 8;
            var bits = stackalloc byte[bytes];
            for (var i = 0; i < bytes; i++) bits[i] = 0;

            if (ioctl(Fd(stream), EviocgBit(Evdev.EV_ABS, bytes), bits) < 0) return null;

            var axes = new bool[bytes * 8];
            for (var i = 0; i < axes.Length; i++)
                axes[i] = (bits[i / 8] & (1 << (i % 8))) != 0;
            return axes;
        }
        catch { return null; }
    }

    /// <summary>Reads an axis' real range (EVIOCGABS), falling back to the common 16-bit range.</summary>
    public static AxisRange GetAxisRange(FileStream stream, ushort axis)
    {
        try
        {
            AbsInfo info = default;
            if (ioctl(Fd(stream), EviocgAbs(axis), &info) == 0 && info.Maximum > info.Minimum)
                return new AxisRange(info.Minimum, info.Maximum);
        }
        catch { /* fall through */ }
        return AxisRange.Default;
    }
}
