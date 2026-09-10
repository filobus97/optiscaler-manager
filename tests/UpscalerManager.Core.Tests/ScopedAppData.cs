// Upscaler Manager - tests
// Licensed under GPL-3.0-or-later (see repository LICENSE).

using System;
using System.IO;
using UpscalerManager.Core.Services;

namespace UpscalerManager.Core.Tests
{
    /// <summary>
    /// Points the app-data directory at a scratch folder for the lifetime of a test
    /// class, and cleans up afterwards.
    ///
    /// Anything that constructs a Core service touches this directory: the analyzer
    /// writes its cache there, the backup store its per-game backups. Without a
    /// redirect the suite reads and writes the *real* app-data of whoever runs it —
    /// which it did, leaving behind an analysis cache and a backup folder for a
    /// fixture game. Any class that reaches a Core service should hold one of these
    /// and join <see cref="AppDataCollection"/>, since the override is process-wide.
    /// </summary>
    internal sealed class ScopedAppData : IDisposable
    {
        private readonly string _root;
        private readonly string? _previous;

        public ScopedAppData()
        {
            _root = Path.Combine(Path.GetTempPath(), "um_appdata_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _previous = AppDataPaths.RootOverride;
            AppDataPaths.RootOverride = Path.Combine(_root, AppDataPaths.FolderName);
        }

        public void Dispose()
        {
            AppDataPaths.RootOverride = _previous;
            try { Directory.Delete(_root, true); } catch { }
        }
    }
}
