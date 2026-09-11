// Upscaler Manager - GPL-3.0-or-later. See repository LICENSE.
using System;
using System.Diagnostics;
using System.IO;
using UpscalerManager.Core.Logging;
using UpscalerManager.Core.Services;

namespace UpscalerManager.App.Infrastructure;

/// <summary>
/// The app's <see cref="ILog"/> sink: every diagnostic goes to a file, and to the
/// console and debugger as well.
///
/// The file is the point. This used to trace to stdout only, which is invisible the
/// moment the app is started from a desktop launcher or from Steam's Gaming Mode —
/// which is how it is normally used. So the one thing needed to work out why an
/// install or a swap went wrong was the one thing a user could not produce, while the
/// README cheerfully told them to attach it.
/// </summary>
public sealed class UiLog : ILog
{
    /// <summary>
    /// Rotated at this size, keeping one previous file. A session writes a few
    /// kilobytes, so this holds a long history while never growing without bound on a
    /// machine where the app is left running.
    /// </summary>
    private const long MaxBytes = 2 * 1024 * 1024;

    private readonly object _gate = new();
    private readonly string? _path;

    public UiLog()
    {
        try
        {
            var dir = LogDirectory;
            Directory.CreateDirectory(dir);
            _path = Path.Combine(dir, "upscaler-manager.log");
            Rotate();
            File.AppendAllText(_path,
                $"{Environment.NewLine}=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} — "
                + $"Upscaler Manager {AppUpdateService.GetCurrentVersion()} on "
                + $"{Environment.OSVersion} ==={Environment.NewLine}");
        }
        catch (Exception ex)
        {
            // No log file is a degraded state, not a failure: the app still runs and
            // still traces to the console.
            _path = null;
            Debug.WriteLine($"[Log] Could not open a log file: {ex.Message}");
        }
    }

    /// <summary>Where the log lives, so Settings can point at it.</summary>
    public static string LogDirectory => Path.Combine(AppDataPaths.Root, "Logs");

    /// <summary>The log file, whether or not it exists yet.</summary>
    public static string LogFile => Path.Combine(LogDirectory, "upscaler-manager.log");

    private void Rotate()
    {
        if (_path is null || !File.Exists(_path)) return;
        try
        {
            if (new FileInfo(_path).Length < MaxBytes) return;
            var previous = _path + ".1";
            if (File.Exists(previous)) File.Delete(previous);
            File.Move(_path, previous);
        }
        catch
        {
            // Rotation is housekeeping; appending to an oversized file is still better
            // than losing the diagnostics.
        }
    }

    public void Write(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        Debug.WriteLine(line);
        Console.WriteLine(line);

        if (_path is null) return;
        lock (_gate)
        {
            // Writes come from background threads — the scan, downloads, installs — so
            // this has to be serialised or lines interleave mid-character.
            try { File.AppendAllText(_path, line + Environment.NewLine); }
            catch { /* logging must never throw into the caller */ }
        }
    }
}
