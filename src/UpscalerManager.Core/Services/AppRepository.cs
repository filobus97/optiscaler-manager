// Upscaler Manager - GPL-3.0-or-later. See repository LICENSE.
using System;
using System.Linq;
using UpscalerManager.Core.Models;

namespace UpscalerManager.Core.Services;

/// <summary>
/// Where this app's own releases come from.
///
/// The repository is copied into the user's config on first run and never refreshed
/// from the shipped template, so renaming the repository would leave every existing
/// install checking a name we no longer publish to — silently, since a failed update
/// check just reports "no update". Superseded names are listed here so the stored
/// value is corrected the next time the app starts.
/// </summary>
public static class AppRepository
{
    public static RepositoryConfig Current { get; } =
        new() { RepoOwner = "filobus97", RepoName = "optiscaler-manager" };

    /// <summary>
    /// Repository names this app has published under before. A rename adds the outgoing
    /// name here at the same time as <see cref="Current"/> changes.
    /// </summary>
    private static readonly string[] SupersededNames =
    {
        "Optiscaler-Client",   // configs inherited from the project this was forked from
    };

    /// <summary>
    /// True when a stored config points somewhere we no longer publish: nowhere at all,
    /// or a name we have since moved away from. A repository the user set deliberately
    /// is left alone.
    /// </summary>
    public static bool NeedsRetargeting(RepositoryConfig? stored) =>
        stored is null
        || string.IsNullOrWhiteSpace(stored.RepoOwner)
        || string.IsNullOrWhiteSpace(stored.RepoName)
        || SupersededNames.Contains(stored.RepoName, StringComparer.OrdinalIgnoreCase);
}
