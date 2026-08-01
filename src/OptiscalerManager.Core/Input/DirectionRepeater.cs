// OptiScaler Manager - GPL-3.0-or-later. See repository LICENSE.
using System;
using System.Collections.Generic;

namespace OptiscalerManager.Core.Input;

/// <summary>
/// Key-style auto-repeat for held directions: hold the stick or D-pad and focus keeps
/// moving. Pure timing logic (the caller supplies "now"), so it is unit-testable.
/// Only directions repeat — Accept/Cancel must be deliberate single presses.
/// </summary>
public sealed class DirectionRepeater
{
    private readonly TimeSpan _initialDelay;
    private readonly TimeSpan _interval;
    private readonly Dictionary<GamepadAction, (DateTime Pressed, DateTime LastFired)> _held = new();

    public DirectionRepeater(TimeSpan? initialDelay = null, TimeSpan? interval = null)
    {
        _initialDelay = initialDelay ?? TimeSpan.FromMilliseconds(400);
        _interval = interval ?? TimeSpan.FromMilliseconds(110);
    }

    public static bool IsDirection(GamepadAction a) =>
        a is GamepadAction.Up or GamepadAction.Down or GamepadAction.Left or GamepadAction.Right;

    /// <summary>Records a direction going down (the initial press is emitted by the caller).</summary>
    public void Press(GamepadAction action, DateTime now)
    {
        if (IsDirection(action)) _held[action] = (now, now);
    }

    public void Release(GamepadAction action) => _held.Remove(action);

    public void Clear() => _held.Clear();

    /// <summary>Returns the held directions that are due to fire another step.</summary>
    public IReadOnlyList<GamepadAction> Tick(DateTime now)
    {
        List<GamepadAction>? due = null;
        foreach (var key in new List<GamepadAction>(_held.Keys))
        {
            var (pressed, lastFired) = _held[key];
            if (now - pressed < _initialDelay) continue;   // still in the pre-repeat pause
            if (now - lastFired < _interval) continue;
            _held[key] = (pressed, now);
            (due ??= new()).Add(key);
        }
        return (IReadOnlyList<GamepadAction>?)due ?? Array.Empty<GamepadAction>();
    }
}
