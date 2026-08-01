// OptiScaler Manager - GPL-3.0-or-later. See repository LICENSE.

namespace OptiscalerManager.Core.Input;

/// <summary>
/// A logical, device-independent controller action. The evdev layer decodes raw
/// hardware events into these; the UI layer turns them into navigation.
/// </summary>
public enum GamepadAction
{
    None = 0,
    Up,
    Down,
    Left,
    Right,
    /// <summary>Confirm / activate the focused control (A / cross).</summary>
    Accept,
    /// <summary>Back out / close the dialog (B / circle).</summary>
    Cancel,
    /// <summary>Focus the next control in tab order (right shoulder).</summary>
    NextSection,
    /// <summary>Focus the previous control in tab order (left shoulder).</summary>
    PrevSection,
}

/// <summary>A press or release of a logical action.</summary>
/// <param name="Action">The action.</param>
/// <param name="Pressed">True on press, false on release.</param>
public readonly record struct GamepadInput(GamepadAction Action, bool Pressed);
