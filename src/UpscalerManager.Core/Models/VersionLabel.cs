// Upscaler Manager - GPL-3.0-or-later. See repository LICENSE.
using System.Linq;

namespace UpscalerManager.Core.Models;

/// <summary>How versions read in the UI.</summary>
public static class VersionLabel
{
    /// <summary>
    /// Drops trailing ".0" groups, which are noise at a glance: "3.7.0.0" reads better
    /// as "3.7".
    ///
    /// Whole groups only. Trimming trailing zero *characters* instead turns 3.7.10.0
    /// into 3.7.1 — a different version, and a wrong one to show next to a game.
    /// </summary>
    public static string Short(string version)
    {
        var parts = version.Split('.').ToList();
        while (parts.Count > 2 && parts[^1] == "0") parts.RemoveAt(parts.Count - 1);
        return string.Join('.', parts);
    }
}
