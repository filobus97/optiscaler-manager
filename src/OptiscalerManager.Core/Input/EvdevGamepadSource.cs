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

    /// <summary>Raised (on a background thread) when the scroll stick's deflection changes.</summary>
    public event Action<GamepadScroll>? Scroll;

    /// <summary>Raised when the set of connected controllers changes.</summary>
    public event Action? DevicesChanged;

    /// <summary>True when a device was found but could not be opened (permissions).</summary>
    public bool PermissionDenied { get; private set; }

    /// <summary>Where input devices live. OSM_INPUT_ROOT overrides it (diagnostics/tests).</summary>
    public static string DefaultInputRoot =>
        Environment.GetEnvironmentVariable("OSM_INPUT_ROOT") is { Length: > 0 } r ? r : "/dev/input";

    public EvdevGamepadSource(string? inputRoot = null) => _inputRoot = inputRoot ?? DefaultInputRoot;

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
    /// Every input node worth probing: the raw <c>event*</c> devices plus udev's
    /// <c>*-event-joystick</c> symlinks. The raw nodes matter because virtual pads —
    /// including the one Steam presents when it takes over a controller in Gaming
    /// Mode — often have no by-id/by-path symlink at all.
    /// </summary>
    public static IEnumerable<string> DiscoverCandidatePaths(string inputRoot)
    {
        if (Directory.Exists(inputRoot))
            foreach (var path in Directory.EnumerateFileSystemEntries(inputRoot))
                if (Path.GetFileName(path).StartsWith("event", StringComparison.Ordinal))
                    yield return path;

        foreach (var sub in new[] { "by-id", "by-path" })
        {
            var dir = Path.Combine(inputRoot, sub);
            if (!Directory.Exists(dir)) continue;
            foreach (var path in Directory.EnumerateFileSystemEntries(dir))
                if (path.EndsWith("-event-joystick", StringComparison.Ordinal))
                    yield return path;
        }
    }

    /// <summary>Kept for callers that only want udev's joystick symlinks.</summary>
    public static IEnumerable<string> DiscoverDevicePaths(string inputRoot) =>
        DiscoverCandidatePaths(inputRoot).Where(p => p.EndsWith("-event-joystick", StringComparison.Ordinal));

    /// <summary>
    /// Decides whether an opened device is a controller by asking it (EVIOCGBIT for
    /// BTN_SOUTH). When the device can't be probed — a test fixture, an odd node — we
    /// fall back to udev's naming so nothing that used to work stops working.
    /// </summary>
    public static bool LooksLikeGamepad(FileStream stream, string path) =>
        EvdevIoctl.TryIsGamepad(stream)
        ?? path.EndsWith("-event-joystick", StringComparison.Ordinal);

    /// <summary>The device's self-reported name, for diagnostics. Null if unavailable.</summary>
    public static string? TryGetDeviceName(FileStream stream) => EvdevIoctl.TryGetName(stream);

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
            var found = DiscoverCandidatePaths(_inputRoot).ToList();
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
                        var reader = DeviceReader.TryOpen(path, target, OnInput, OnScroll);
                        if (reader is null) continue;   // not a controller (keyboard, mouse, touchpad…)
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

    private void OnScroll(GamepadScroll scroll) => Scroll?.Invoke(scroll);

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
        private readonly Action<GamepadScroll> _scrollSink;
        private readonly Thread _thread;
        private volatile bool _stop;

        public string Name { get; }
        public string Target { get; }
        public bool Faulted { get; private set; }

        /// <summary>Opens the device if it is a controller; returns null when it isn't.</summary>
        public static DeviceReader? TryOpen(string path, string target,
            Action<GamepadInput> sink, Action<GamepadScroll> scrollSink)
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (!LooksLikeGamepad(stream, path))
            {
                stream.Dispose();   // a keyboard/mouse/touchpad — leave it alone
                return null;
            }
            return new DeviceReader(stream, path, target, sink, scrollSink);
        }

        private DeviceReader(FileStream stream, string path, string target,
            Action<GamepadInput> sink, Action<GamepadScroll> scrollSink)
        {
            _stream = stream;
            Target = target;
            _sink = sink;
            _scrollSink = scrollSink;
            // The device's own name beats parsing a udev symlink, and works for the
            // virtual pads that have no symlink to parse.
            Name = EvdevIoctl.TryGetName(stream) ?? FriendlyName(path);
            _decoder = new EvdevGamepadDecoder(
                EvdevIoctl.GetAxisRange(stream, Evdev.ABS_X),
                EvdevIoctl.GetAxisRange(stream, Evdev.ABS_Y),
                EvdevIoctl.GetAxisRange(stream, Evdev.ABS_RX),
                EvdevIoctl.GetAxisRange(stream, Evdev.ABS_RY));

            _thread = new Thread(Loop) { IsBackground = true, Name = $"gamepad:{Name}" };
            _thread.Start();
        }

        private void Loop()
        {
            var buffer = new byte[Evdev.EventSize * 32];
            try
            {
                var lastScroll = _decoder.Scroll;
                while (!_stop)
                {
                    var read = _stream.Read(buffer, 0, buffer.Length);
                    if (read <= 0) break;
                    for (var offset = 0; offset + Evdev.EventSize <= read; offset += Evdev.EventSize)
                        foreach (var change in _decoder.Feed(buffer.AsSpan(offset, Evdev.EventSize)))
                            _sink(change);

                    // Report the scroll stick once per batch: evdev sends X and Y as
                    // separate events, so per-event reporting would jitter diagonals.
                    if (_decoder.Scroll != lastScroll)
                    {
                        lastScroll = _decoder.Scroll;
                        _scrollSink(lastScroll);
                    }
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
