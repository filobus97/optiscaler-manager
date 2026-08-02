// OptiScaler Manager - GPL-3.0-or-later. See repository LICENSE.
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OptiscalerManager.Core.Input;
using OptiscalerManager.Core.Logging;

namespace OptiscalerManager.App.Services;

/// <summary>
/// Makes a game controller drive the UI.
///
/// Avalonia has no gamepad backend on Linux, so <see cref="EvdevGamepadSource"/> reads
/// the controller directly and this class translates each action into the equivalent
/// key event, tagged <see cref="KeyDeviceType.Gamepad"/>. Everything downstream — XY
/// directional focus, Enter/Esc on dialogs, arrow navigation in the game list — is the
/// keyboard support the app already has, so the controller and the keyboard behave
/// identically by construction.
/// </summary>
public sealed class GamepadNavigator : IDisposable
{
    private readonly EvdevGamepadSource _source = new();
    private readonly DirectionRepeater _repeater = new();
    private DispatcherTimer? _repeatTimer;
    private bool _started;

    /// <summary>Raised when the connected-controller list changes (for the Settings card).</summary>
    public event Action? DevicesChanged;

    public bool IsSupported => EvdevGamepadSource.IsSupported;
    public IReadOnlyList<string> ConnectedDevices => _source.ConnectedDevices;
    public bool PermissionDenied => _source.PermissionDenied;

    public void Start()
    {
        if (_started || !IsSupported) return;
        _started = true;

        // Sees every real key press in the app, so we can notice when our presses are
        // being duplicated by an external mapping. Class handlers are process-wide.
        if (!_watchingForDuplicates)
        {
            _watchingForDuplicates = true;
            InputElement.KeyDownEvent.AddClassHandler<TopLevel>(
                (_, e) => NoteExternalKey(e), RoutingStrategies.Tunnel, handledEventsToo: true);
        }

        _source.Input += OnInput;
        _source.Scroll += scroll => Dispatcher.UIThread.Post(() => _scroll = scroll);
        _source.DevicesChanged += () => Dispatcher.UIThread.Post(() => DevicesChanged?.Invoke());
        _source.Start();

        // Drives auto-repeat for held directions, and the scroll stick, which is
        // continuous and so has to be applied on a clock rather than per event.
        _repeatTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Input, (_, _) =>
        {
            var now = DateTime.UtcNow;
            foreach (var action in _repeater.Tick(now))
                SendKey(action);
            ApplyScroll(now);
        });
        _repeatTimer.Start();
        Log.Write("[Gamepad] Controller navigation started.");
    }

    // Arrives on the reader's background thread.
    private void OnInput(GamepadInput input) => Dispatcher.UIThread.Post(() =>
    {
        if (input.Pressed)
        {
            SendKey(input.Action);
            _repeater.Press(input.Action, DateTime.UtcNow);
        }
        else
        {
            _repeater.Release(input.Action);
        }
    });

    private static (Key Key, KeyModifiers Modifiers)? MapToKey(GamepadAction action) => action switch
    {
        GamepadAction.Up => (Key.Up, KeyModifiers.None),
        GamepadAction.Down => (Key.Down, KeyModifiers.None),
        GamepadAction.Left => (Key.Left, KeyModifiers.None),
        GamepadAction.Right => (Key.Right, KeyModifiers.None),
        GamepadAction.Accept => (Key.Enter, KeyModifiers.None),
        GamepadAction.Cancel => (Key.Escape, KeyModifiers.None),
        GamepadAction.NextSection => (Key.Tab, KeyModifiers.None),
        GamepadAction.PrevSection => (Key.Tab, KeyModifiers.Shift),
        _ => null,
    };

    // How close two presses of the same key have to be to be the same press arriving twice.
    private static readonly TimeSpan DuplicateWindow = TimeSpan.FromMilliseconds(250);
    private const int DuplicatesBeforeBackingOff = 3;

    private static bool _watchingForDuplicates;
    private (Key Key, DateTime At)? _lastSent;
    private (Key Key, DateTime At)? _lastExternal;
    private int _duplicateCount;

    /// <summary>
    /// True when something else (Steam Input, typically) is already turning this
    /// controller into key presses, so we have stopped doing it as well.
    /// </summary>
    public bool DeferringToExternalMapping { get; private set; }

    /// <summary>
    /// Watches for a real key press that matches one we just synthesized. Steam Input
    /// maps the pad to the keyboard, and we read the same pad directly, so every press
    /// arrives twice — which walks two rows at a time and double-fires buttons.
    ///
    /// Detection is deliberately narrow: it needs the *same key* within a few hundred
    /// milliseconds, several times over. A layout that emits something else (mouse
    /// clicks, say) never matches, so we never back off when we are the only thing
    /// driving the UI.
    /// </summary>
    private void NoteExternalKey(KeyEventArgs e)
    {
        // Ours are tagged Gamepad; anything else came from the real input pipeline.
        if (DeferringToExternalMapping || e.KeyDeviceType == KeyDeviceType.Gamepad) return;

        var now = DateTime.UtcNow;
        _lastExternal = (e.Key, now);
        if (_lastSent is { } sent && sent.Key == e.Key && now - sent.At <= DuplicateWindow)
            CountDuplicate();
    }

    private void CountDuplicate()
    {
        if (++_duplicateCount < DuplicatesBeforeBackingOff) return;

        DeferringToExternalMapping = true;
        Log.Write("[Gamepad] Every press is arriving twice — something else (Steam Input?) is " +
                  "already mapping this controller to the keyboard. Leaving navigation to it; " +
                  "the scroll stick keeps working.");
        Dispatcher.UIThread.Post(() => DevicesChanged?.Invoke());
    }

    private void SendKey(GamepadAction action)
    {
        if (DeferringToExternalMapping) return;

        var mapped = MapToKey(action);
        if (mapped is not { } m) return;

        var window = ActiveWindow();
        if (window is null) return;

        // The external mapping may land either side of ours, so check both orders.
        var now = DateTime.UtcNow;
        if (_lastExternal is { } ext && ext.Key == m.Key && now - ext.At <= DuplicateWindow)
            CountDuplicate();
        _lastSent = (m.Key, now);

        if (window.FocusManager?.GetFocusedElement() is not Interactive target)
        {
            // Nothing is focused: either the screen just opened, or the control that had
            // focus was disabled mid-action (pressing Rescan disables Rescan). Place
            // focus and stop there — a press must never activate whatever it happens to
            // land on, or a stray Enter here would open the install dialog.
            SeedFocus(window);
            return;
        }

        target.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = m.Key,
            KeyModifiers = m.Modifiers,
            KeyDeviceType = KeyDeviceType.Gamepad,
            Source = target,
        });
        target.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyUpEvent,
            Key = m.Key,
            KeyModifiers = m.Modifiers,
            KeyDeviceType = KeyDeviceType.Gamepad,
            Source = target,
        });
    }

    /// <summary>How fast the scroll stick moves the page at full deflection.</summary>
    private const double MaxScrollPixelsPerSecond = 1600;

    private GamepadScroll _scroll;
    private DateTime _lastScrollTick;

    /// <summary>
    /// Scrolls the page under the scroll stick. Unlike the D-pad this is continuous, so
    /// it moves by elapsed time rather than per event — holding the stick scrolls
    /// smoothly, and how far you push sets the speed.
    /// </summary>
    private void ApplyScroll(DateTime now)
    {
        var elapsed = now - _lastScrollTick;
        _lastScrollTick = now;

        if (_scroll.IsIdle) return;
        // A long gap means we were idle or the app was busy; don't lurch.
        if (elapsed <= TimeSpan.Zero || elapsed > TimeSpan.FromMilliseconds(250)) return;

        if (ActiveWindow() is not { } window) return;
        if (FindScrollViewer(window) is not { } viewer) return;

        var step = MaxScrollPixelsPerSecond * elapsed.TotalSeconds;
        var maxX = Math.Max(0, viewer.Extent.Width - viewer.Viewport.Width);
        var maxY = Math.Max(0, viewer.Extent.Height - viewer.Viewport.Height);
        viewer.Offset = new Vector(
            Math.Clamp(viewer.Offset.X + (_scroll.X * step), 0, maxX),
            Math.Clamp(viewer.Offset.Y + (_scroll.Y * step), 0, maxY));
    }

    /// <summary>
    /// The scroller the stick should move: the one holding the focused control, so the
    /// stick always scrolls what you are looking at. Falls back to the first scrollable
    /// one in the window when focus is outside any of them.
    /// </summary>
    private static ScrollViewer? FindScrollViewer(Window window)
    {
        for (var v = window.FocusManager?.GetFocusedElement() as Visual; v is not null; v = v.GetVisualParent())
            if (v is ScrollViewer inner && CanScroll(inner))
                return inner;

        return window.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault(CanScroll);
    }

    private static bool CanScroll(ScrollViewer viewer) =>
        viewer.Extent.Height > viewer.Viewport.Height || viewer.Extent.Width > viewer.Viewport.Width;

    /// <summary>
    /// Moves focus onto a window's first control. Used when a screen opens with nothing
    /// focused, so directional input has somewhere to start from.
    /// </summary>
    private static Interactive? SeedFocus(Window window)
    {
        var first = window.GetVisualDescendants()
            .OfType<InputElement>()
            .FirstOrDefault(x => x is { Focusable: true, IsEffectivelyEnabled: true, IsEffectivelyVisible: true });

        return first is not null && first.Focus(NavigationMethod.Directional) ? first : null;
    }

    private bool _sawActivation;
    private bool _loggedPermissiveMode;

    /// <summary>
    /// The window that should receive controller input.
    ///
    /// Normally that is the active window, and nothing when the app is unfocused — the
    /// reader keeps running in the background, and without that check a controller
    /// being used in a *game* would still be navigating, and pressing, this app's UI
    /// behind it.
    ///
    /// But some backends (Avalonia's Wayland support is still experimental) may never
    /// report a window as active. Rather than silently swallowing every press there,
    /// we only enforce the focus rule once we have seen activation work at least once;
    /// until then we keep going with the topmost window so the controller is usable.
    /// </summary>
    private Window? ActiveWindow()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return null;

        // Dialogs are modal, so the active one must win over the main window.
        var active = desktop.Windows.LastOrDefault(w => w.IsActive);
        if (active is not null)
        {
            _sawActivation = true;
            return active;
        }

        if (_sawActivation) return null;   // activation works here: the app really is unfocused

        if (!_loggedPermissiveMode)
        {
            _loggedPermissiveMode = true;
            Log.Write("[Gamepad] This backend never reports window activation — " +
                      "routing input to the topmost window so the controller still works.");
        }

        // Windows are listed in the order they opened, so the last visible one is the
        // modal dialog on top. Targeting MainWindow here would send Settings' input to
        // the screen behind it. The fallbacks matter: an empty list must still leave the
        // controller working rather than silently dead.
        return desktop.Windows.LastOrDefault(w => w.IsVisible)
               ?? desktop.Windows.LastOrDefault()
               ?? desktop.MainWindow;
    }

    public void Dispose()
    {
        _repeatTimer?.Stop();
        _repeater.Clear();
        _source.Dispose();
    }
}
