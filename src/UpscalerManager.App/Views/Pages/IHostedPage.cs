// Upscaler Manager - GPL-3.0-or-later. See repository LICENSE.
using System;

namespace UpscalerManager.App.Views.Pages;

/// <summary>
/// A screen shown inside the main window instead of in a window of its own.
///
/// Everything used to be a modal window. That is a problem in Steam's Gaming Mode,
/// where gamescope composites one application surface and extra top-level windows are
/// unreliable — and it was also the source of the controller bugs, because the
/// navigator had to guess which window should receive input. With one window there is
/// nothing to guess.
/// </summary>
public interface IHostedPage
{
    /// <summary>Shown in the page's header bar.</summary>
    string Title { get; }

    /// <summary>
    /// Set by the host. Call it to leave the page; the argument is the page's result
    /// (for the install page, whether the user confirmed).
    /// </summary>
    Action<bool>? RequestClose { get; set; }

    /// <summary>Puts focus somewhere sensible so the first controller press works.</summary>
    void FocusFirst();
}
