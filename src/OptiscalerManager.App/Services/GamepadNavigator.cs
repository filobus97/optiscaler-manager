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

        _source.Input += OnInput;
        _source.DevicesChanged += () => Dispatcher.UIThread.Post(() => DevicesChanged?.Invoke());
        _source.Start();

        // Drives auto-repeat for held directions.
        _repeatTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Input, (_, _) =>
        {
            foreach (var action in _repeater.Tick(DateTime.UtcNow))
                SendKey(action);
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

    private void SendKey(GamepadAction action)
    {
        var mapped = MapToKey(action);
        if (mapped is not { } m) return;

        var window = ActiveWindow();
        if (window is null) return;

        // Deliver to whatever has focus, falling back to the window so a fresh screen
        // with nothing focused still responds (the handler will move focus into it).
        var target = window.FocusManager?.GetFocusedElement() as Interactive ?? window;

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

    private bool _loggedInactiveDrop;

    /// <summary>
    /// The window that should receive controller input, or null when the app is not
    /// focused. Returning null matters: the reader keeps running while the app sits in
    /// the background, and without this check a controller being used in a *game*
    /// would still be navigating — and pressing — this app's UI behind it.
    /// </summary>
    private Window? ActiveWindow()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return null;

        // Dialogs are modal, so the active one must win over the main window.
        var active = desktop.Windows.FirstOrDefault(w => w.IsActive);
        if (active is null && !_loggedInactiveDrop)
        {
            _loggedInactiveDrop = true;
            Log.Write("[Gamepad] Input ignored while the app is not focused (logged once).");
        }
        if (active is not null) _loggedInactiveDrop = false;
        return active;
    }

    public void Dispose()
    {
        _repeatTimer?.Stop();
        _repeater.Clear();
        _source.Dispose();
    }
}
