// OptiScaler Manager - GPL-3.0-or-later. See repository LICENSE.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using OptiscalerManager.Core.Logging;

namespace OptiscalerManager.Core.Input;

/// <summary>
/// Reads game controllers directly from Linux evdev.
///
/// Avalonia has no gamepad backend on Linux (its enums exist, but nothing ever
/// raises gamepad input), so without this a controller does nothing unless the user
/// has configured Steam Input to emit keystrokes. Reading evdev ourselves means the
/// controller works in desktop mode and in Steam Gaming Mode alike, with no external
/// dependency: these are plain file reads.
/// </summary>
public sealed class EvdevGamepadSource : IDisposable
{
    private readonly string _inputRoot;
    private readonly object _sync = new();
    private readonly Dictionary<string, DeviceReader> _readers = new(StringComparer.Ordinal);
    private Timer? _hotplugTimer;
    private bool _disposed;

    /// <summary>Raised (on a background thread) for every logical press/release.</summary>
    public event Action<GamepadInput>? Input;

    /// <summary>Raised when the set of connected controllers changes.</summary>
    public event Action? DevicesChanged;

    /// <summary>True when a device was found but could not be opened (permissions).</summary>
    public bool PermissionDenied { get; private set; }

    public EvdevGamepadSource(string inputRoot = "/dev/input") => _inputRoot = inputRoot;

    /// <summary>Friendly names of the controllers currently being read.</summary>
    public IReadOnlyList<string> ConnectedDevices
    {
        get { lock (_sync) return _readers.Values.Select(r => r.Name).ToList(); }
    }

    public static bool IsSupported => OperatingSystem.IsLinux();

    /// <summary>Starts reading, and watches for controllers plugged in later.</summary>
    public void Start()
    {
        if (!IsSupported) return;
        Rescan();
        _hotplugTimer = new Timer(_ => Rescan(), null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
    }

    /// <summary>
    /// Finds controller event devices. udev symlinks every joystick/gamepad as
    /// <c>*-event-joystick</c>, which identifies them without needing ioctl probes
    /// of every input device.
    /// </summary>
    public static IEnumerable<string> DiscoverDevicePaths(string inputRoot)
    {
        foreach (var sub in new[] { "by-id", "by-path" })
        {
            var dir = Path.Combine(inputRoot, sub);
            if (!Directory.Exists(dir)) continue;
            foreach (var path in Directory.EnumerateFileSystemEntries(dir))
                if (path.EndsWith("-event-joystick", StringComparison.Ordinal))
                    yield return path;
        }
    }

    /// <summary>Turns "usb-Microsoft_X-Box_360_pad-event-joystick" into something readable.</summary>
    public static string FriendlyName(string devicePath)
    {
        var name = Path.GetFileName(devicePath);
        if (name.EndsWith("-event-joystick", StringComparison.Ordinal))
            name = name[..^"-event-joystick".Length];
        foreach (var prefix in new[] { "usb-", "bluetooth-", "pci-" })
            if (name.StartsWith(prefix, StringComparison.Ordinal)) { name = name[prefix.Length..]; break; }
        name = name.Replace('_', ' ').Trim();
        return string.IsNullOrWhiteSpace(name) ? "Controller" : name;
    }

    private void Rescan()
    {
        if (_disposed) return;
        try
        {
            var found = DiscoverDevicePaths(_inputRoot).ToList();
            var changed = false;

            lock (_sync)
            {
                // Drop readers whose device disappeared or died.
                foreach (var gone in _readers.Where(kv => !found.Contains(kv.Key) || kv.Value.Faulted)
                                             .Select(kv => kv.Key).ToList())
                {
                    _readers[gone].Dispose();
                    _readers.Remove(gone);
                    changed = true;
                }

                foreach (var path in found)
                {
                    if (_readers.ContainsKey(path)) continue;
                    // Two symlinks (by-id and by-path) can point at one device; only read it once.
                    var target = ResolveTarget(path);
                    if (_readers.Values.Any(r => r.Target == target)) continue;

                    try
                    {
                        var reader = new DeviceReader(path, target, OnInput);
                        _readers[path] = reader;
                        changed = true;
                        Log.Write($"[Gamepad] Reading controller: {reader.Name} ({path})");
                    }
                    catch (UnauthorizedAccessException)
                    {
                        PermissionDenied = true;
                        Log.Write($"[Gamepad] No permission to read {path} (add your user to the 'input' group).");
                    }
                    catch (Exception ex)
                    {
                        Log.Write($"[Gamepad] Could not open {path}: {ex.Message}");
                    }
                }
            }

            if (changed) DevicesChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Write($"[Gamepad] Rescan failed: {ex.Message}");
        }
    }

    private static string ResolveTarget(string path)
    {
        try { return Path.GetFullPath(File.ResolveLinkTarget(path, true)?.FullName ?? path); }
        catch { return path; }
    }

    private void OnInput(GamepadInput input) => Input?.Invoke(input);

    public void Dispose()
    {
        _disposed = true;
        _hotplugTimer?.Dispose();
        lock (_sync)
        {
            foreach (var r in _readers.Values) r.Dispose();
            _readers.Clear();
        }
    }

    /// <summary>One background reader per controller.</summary>
    private sealed class DeviceReader : IDisposable
    {
        private readonly FileStream _stream;
        private readonly EvdevGamepadDecoder _decoder;
        private readonly Action<GamepadInput> _sink;
        private readonly Thread _thread;
        private volatile bool _stop;

        public string Name { get; }
        public string Target { get; }
        public bool Faulted { get; private set; }

        public DeviceReader(string path, string target, Action<GamepadInput> sink)
        {
            Name = FriendlyName(path);
            Target = target;
            _sink = sink;
            _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            _decoder = new EvdevGamepadDecoder(
                EvdevAxis.TryGetRange(_stream, Evdev.ABS_X),
                EvdevAxis.TryGetRange(_stream, Evdev.ABS_Y));

            _thread = new Thread(Loop) { IsBackground = true, Name = $"gamepad:{Name}" };
            _thread.Start();
        }

        private void Loop()
        {
            var buffer = new byte[Evdev.EventSize * 32];
            try
            {
                while (!_stop)
                {
                    var read = _stream.Read(buffer, 0, buffer.Length);
                    if (read <= 0) break;
                    for (var offset = 0; offset + Evdev.EventSize <= read; offset += Evdev.EventSize)
                        foreach (var change in _decoder.Feed(buffer.AsSpan(offset, Evdev.EventSize)))
                            _sink(change);
                }
            }
            catch (Exception ex)
            {
                if (!_stop) Log.Write($"[Gamepad] {Name} read stopped: {ex.Message}");
            }
            finally { Faulted = true; }
        }

        public void Dispose()
        {
            _stop = true;
            try { _stream.Dispose(); } catch { }  // unblocks the pending read
        }
    }
}

/// <summary>Reads an absolute axis' real range via EVIOCGABS, so stick deadzones are correct.</summary>
internal static partial class EvdevAxis
{
    [StructLayout(LayoutKind.Sequential)]
    private struct AbsInfo
    {
        public int Value, Minimum, Maximum, Fuzz, Flat, Resolution;
    }

    [LibraryImport("libc", SetLastError = true)]
    private static partial int ioctl(int fd, nuint request, ref AbsInfo info);

    /// <summary>EVIOCGABS(axis) = _IOR('E', 0x40 + axis, struct input_absinfo).</summary>
    private static nuint EviocgAbs(ushort axis) =>
        (nuint)((2u << 30) | (24u << 16) | ((uint)'E' << 8) | (0x40u + axis));

    public static AxisRange TryGetRange(FileStream stream, ushort axis)
    {
        try
        {
            var info = default(AbsInfo);
            var fd = stream.SafeFileHandle.DangerousGetHandle().ToInt32();
            if (ioctl(fd, EviocgAbs(axis), ref info) == 0 && info.Maximum > info.Minimum)
                return new AxisRange(info.Minimum, info.Maximum);
        }
        catch { /* fall through to the default range */ }
        return AxisRange.Default;
    }
}
