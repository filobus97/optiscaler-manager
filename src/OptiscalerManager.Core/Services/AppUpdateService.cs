// OptiScaler Manager - a simple, AMD-focused frontend for the OptiScaler mod.
// Copyright (C) 2026 filobus97
//
// Based on OptiScaler Client (Copyright (C) 2026 Agustín Montaña / Agustinm28).
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using OptiscalerManager.Core.Logging;
using OptiscalerManager.Core.Models;

namespace OptiscalerManager.Core.Services
{
    /// <summary>Result of an app self-update check.</summary>
    public sealed record AppUpdateCheck(string CurrentVersion, string? LatestVersion, bool UpdateAvailable);

    /// <summary>
    /// Checks this application's own GitHub repository for a newer release.
    /// Modelled on OptiScaler Client's AppUpdateService: current version from the
    /// assembly's informational version, latest from the repo's releases/latest tag.
    /// The check never throws — network failures report "no update".
    /// </summary>
    public class AppUpdateService
    {
        private readonly RepositoryConfig _repo;

        public AppUpdateService(RepositoryConfig appRepo) => _repo = appRepo;

        /// <summary>URL of the releases page, for the "open releases" UI action.</summary>
        public string ReleasesPageUrl => $"https://github.com/{_repo.RepoOwner}/{_repo.RepoName}/releases";

        /// <summary>
        /// The running app's version, from AssemblyInformationalVersion (the csproj
        /// &lt;Version&gt;), with any "+build" metadata stripped.
        /// </summary>
        public static string GetCurrentVersion()
        {
            var info = Assembly.GetEntryAssembly()?
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;
            return NormalizeVersion(info) ?? "0.0.0";
        }

        /// <summary>Strips a leading "v" and any "+build" suffix; null for blank input.</summary>
        public static string? NormalizeVersion(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var v = raw.Trim();
            if (v.StartsWith("v", StringComparison.OrdinalIgnoreCase)) v = v.Substring(1);
            var plus = v.IndexOf('+');
            if (plus >= 0) v = v.Substring(0, plus);
            return v.Length == 0 ? null : v;
        }

        /// <summary>
        /// True when <paramref name="latest"/> is a strictly newer version than
        /// <paramref name="current"/>. Falls back to inequality when either side
        /// is not parseable as a version.
        /// </summary>
        public static bool IsNewer(string? current, string? latest)
        {
            current = NormalizeVersion(current);
            latest = NormalizeVersion(latest);
            if (latest is null || current is null) return false;

            if (Version.TryParse(current, out var c) && Version.TryParse(latest, out var l))
                return l > c;

            return !string.Equals(current, latest, StringComparison.OrdinalIgnoreCase);
        }

        // ── In-app self-update (close → updater script → relaunch) ────────────

        /// <summary>
        /// The real install directory: where the executable lives. NOT
        /// AppContext.BaseDirectory — with single-file publishing that points at
        /// the extraction temp dir, while update.sh/VERSION sit next to the exe.
        /// </summary>
        public static string? GetInstallDirectory()
        {
            try
            {
                var exe = Environment.ProcessPath;
                return string.IsNullOrEmpty(exe) ? null : Path.GetDirectoryName(exe);
            }
            catch { return null; }
        }

        /// <summary>Name of the bundled updater script for the current OS.</summary>
        public static string UpdaterScriptName =>
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "update.ps1" : "update.sh";

        /// <summary>
        /// True only when running as a published single-file bundle — the shape of
        /// every shipped build. The entry assembly has no on-disk location inside a
        /// single-file bundle, whereas a `dotnet run` / framework-dependent build
        /// reports its .dll path. This keeps the in-app updater from targeting a dev
        /// build folder (where the script is copied to the output dir too).
        /// </summary>
        public static bool IsSingleFilePublish =>
            string.IsNullOrEmpty(Assembly.GetEntryAssembly()?.Location);

        /// <summary>
        /// True when this is a real single-file deployment with the bundled updater
        /// script next to the exe — i.e. the in-app "Update now" flow can run.
        /// </summary>
        public static bool CanSelfUpdate
        {
            get
            {
                if (!IsSingleFilePublish) return false;
                var dir = GetInstallDirectory();
                return dir is not null && File.Exists(Path.Combine(dir, UpdaterScriptName));
            }
        }

        /// <summary>
        /// The command line that runs the updater detached for the given install dir
        /// and app pid: the script waits for the pid, swaps the files in place, and
        /// relaunches the app on any outcome. Pure, for testability.
        /// </summary>
        /// <remarks>
        /// Passes --force: the app has already decided (from its own GitHub check)
        /// that an update is warranted, so the script must not second-guess it from a
        /// possibly-stale on-disk VERSION file (which would no-op, relaunch the same
        /// binary, and re-show the banner — an endless no-op update loop).
        /// </remarks>
        public static (string FileName, string Arguments) BuildSelfUpdateCommand(
            string installDir, int pid, bool isWindows)
        {
            var script = Path.Combine(installDir, isWindows ? "update.ps1" : "update.sh");
            return isWindows
                ? ("powershell",
                   $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -WaitPid {pid} -Relaunch -Force")
                : ("/bin/sh", $"\"{script}\" --wait-pid {pid} --relaunch --force");
        }

        /// <summary>
        /// Launches the bundled updater as a detached process. The caller should shut
        /// the app down immediately afterwards — the script waits for this process to
        /// exit, updates in place, and relaunches the app. Returns null on success or
        /// a human-readable error (in which case the app should stay running).
        /// </summary>
        public static string? StartSelfUpdate()
        {
            var dir = GetInstallDirectory();
            if (dir is null)
                return "Could not determine the install directory.";
            if (!File.Exists(Path.Combine(dir, UpdaterScriptName)))
                return $"The updater script ({UpdaterScriptName}) was not found next to the app.";

            var (file, args) = BuildSelfUpdateCommand(
                dir, Environment.ProcessId, RuntimeInformation.IsOSPlatform(OSPlatform.Windows));
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = file,
                    Arguments = args,
                    WorkingDirectory = dir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                if (Process.Start(psi) is null)
                    return "The updater process could not be started.";
                Log.Write($"[AppUpdate] Updater launched: {file} {args}");
                return null;
            }
            catch (Exception ex)
            {
                Log.Write($"[AppUpdate] Failed to launch updater: {ex.Message}");
                return $"Could not start the updater: {ex.Message}";
            }
        }

        // ── In-process update (download → rename-swap → exec self) ────────────
        // Used on Linux/macOS instead of the detached script so the update survives
        // Steam Gaming Mode / gamescope: the app replaces its own process image in
        // place (same PID) rather than exiting and relying on a detached process that
        // the session teardown could kill and a new window Steam wouldn't foreground.

        /// <summary>The argv marker the relaunched (updated) process is started with.</summary>
        public const string UpdatedMarker = "--updated";

        /// <summary>True on platforms that use the seamless in-process update (Unix).</summary>
        public static bool UsesInProcessUpdate => !RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        /// <summary>The runtime identifier used to match a release asset (e.g. "linux-x64").</summary>
        public static string CurrentRid()
        {
            var os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win"
                   : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx"
                   : "linux";
            var arch = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => "arm64",
                Architecture.X64 => "x64",
                var a => a.ToString().ToLowerInvariant(),
            };
            return $"{os}-{arch}";
        }

        /// <summary>
        /// Selects the release-asset download URL matching <paramref name="rid"/>
        /// (name ending "-&lt;rid&gt;.zip"). Pure, for testability.
        /// </summary>
        public static string? SelectAssetUrl(IEnumerable<(string Name, string Url)> assets, string rid)
        {
            foreach (var a in assets)
                if (a.Name.EndsWith($"-{rid}.zip", StringComparison.OrdinalIgnoreCase))
                    return a.Url;
            return null;
        }

        /// <summary>
        /// Resolves (assetUrl, tag) for this platform's latest release asset. Honours
        /// the OSM_UPDATE_URL / OSM_UPDATE_TAG test overrides (a local file path / tag),
        /// matching the script's overrides so the flow can be harnessed offline.
        /// </summary>
        private async Task<(string? Url, string? Tag)> GetLatestAssetAsync()
        {
            var overrideUrl = Environment.GetEnvironmentVariable("OSM_UPDATE_URL");
            if (!string.IsNullOrEmpty(overrideUrl))
                return (overrideUrl, Environment.GetEnvironmentVariable("OSM_UPDATE_TAG") ?? "v0.0.0-test");

            var url = $"https://api.github.com/repos/{_repo.RepoOwner}/{_repo.RepoName}/releases/latest";
            using var response = await NetworkService.GetHttpClient().GetAsync(url);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = doc.RootElement;

            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            var assets = new List<(string, string)>();
            if (root.TryGetProperty("assets", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var el in arr.EnumerateArray())
                    if (el.TryGetProperty("name", out var n) && el.TryGetProperty("browser_download_url", out var u))
                        assets.Add((n.GetString() ?? "", u.GetString() ?? ""));

            return (SelectAssetUrl(assets, CurrentRid()), tag);
        }

        /// <summary>
        /// Downloads this platform's latest release, swaps every file into the install
        /// dir in place, then re-execs the app at the SAME PID. Returns a human-readable
        /// error on failure (the app stays running, untouched); on success it does not
        /// return. Report callbacks surface progress to the UI.
        /// </summary>
        public async Task<string?> RunInProcessUpdateAsync(Action<string>? report = null)
        {
            var self = Environment.ProcessPath;
            var dir = string.IsNullOrEmpty(self) ? null : Path.GetDirectoryName(self);
            if (self is null || dir is null)
                return "Could not determine the install directory.";

            // Stage INSIDE the install dir so every File.Move is a same-filesystem
            // rename(2) — atomic, and able to replace the running binary (a cross-fs
            // move would fall back to copy, which fails with ETXTBSY on the live exe).
            var staging = Path.Combine(dir, ".osm-update");
            try
            {
                report?.Invoke("Finding the latest release…");
                var (assetUrl, _) = await GetLatestAssetAsync();
                if (string.IsNullOrEmpty(assetUrl))
                    return "No download was found for this platform.";

                try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
                Directory.CreateDirectory(staging);

                report?.Invoke("Downloading the update…");
                var zip = Path.Combine(staging, "pkg.zip");
                await DownloadToFileAsync(assetUrl, zip);

                report?.Invoke("Extracting…");
                var extract = Path.Combine(staging, "extract");
                System.IO.Compression.ZipFile.ExtractToDirectory(zip, extract, overwriteFiles: true);

                report?.Invoke("Applying the update…");
                SwapFilesIntoPlace(extract, dir, Path.GetFileName(self));

                // Clean the staging dir now — after exec we're gone.
                try { Directory.Delete(staging, true); } catch { }

                report?.Invoke("Restarting…");
                // Preserve the original launch args (minus any prior marker) and append
                // ours, so anything Steam passed survives the restart and the process
                // comes back knowing it was just updated.
                var relaunchArgs = Environment.GetCommandLineArgs()
                    .Skip(1)
                    .Where(a => a != UpdatedMarker)
                    .Append(UpdatedMarker)
                    .ToArray();
                var errno = NativeExec.Exec(self, relaunchArgs); // no return on success
                return $"The updated app could not be restarted (errno {errno}). Please reopen it — it is already updated.";
            }
            catch (Exception ex)
            {
                try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
                Log.Write($"[AppUpdate] In-process update failed: {ex.Message}");
                return $"Update failed: {ex.Message}";
            }
        }

        private static async Task DownloadToFileAsync(string url, string dest)
        {
            // Support the file:// (or bare path) test override as well as https.
            if (url.StartsWith("file://", StringComparison.OrdinalIgnoreCase) || File.Exists(url))
            {
                var src = url.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
                    ? new Uri(url).LocalPath : url;
                File.Copy(src, dest, overwrite: true);
                return;
            }

            using var resp = await NetworkService.GetHttpClient().GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            await using var http = await resp.Content.ReadAsStreamAsync();
            await using var file = File.Create(dest);
            await http.CopyToAsync(file);
        }

        /// <summary>
        /// Renames each extracted file over the install dir. The running executable is
        /// swapped LAST, so a mid-way failure never leaves us re-execing a half-written
        /// binary (every other file is same-filesystem-atomic).
        /// </summary>
        private static void SwapFilesIntoPlace(string extractDir, string installDir, string exeName)
        {
            string? exeSource = null;
            foreach (var src in Directory.GetFiles(extractDir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(extractDir, src);
                if (string.Equals(rel, exeName, StringComparison.Ordinal)) { exeSource = src; continue; }
                MoveOverwrite(src, Path.Combine(installDir, rel));
            }
            if (exeSource is not null)
                MoveOverwrite(exeSource, Path.Combine(installDir, exeName));
        }

        private static void MoveOverwrite(string src, string dest)
        {
            var destDir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);
            File.Move(src, dest, overwrite: true); // rename(2) on the same filesystem
        }

        /// <summary>
        /// Queries the repo's latest release. Returns UpdateAvailable=false on any
        /// failure (offline, rate limit, missing repo config) — the check is best-effort.
        /// </summary>
        public async Task<AppUpdateCheck> CheckAsync()
        {
            var current = GetCurrentVersion();

            if (string.IsNullOrWhiteSpace(_repo.RepoOwner) || string.IsNullOrWhiteSpace(_repo.RepoName))
                return new AppUpdateCheck(current, null, false);

            try
            {
                var url = $"https://api.github.com/repos/{_repo.RepoOwner}/{_repo.RepoName}/releases/latest";
                using var response = await NetworkService.GetHttpClient().GetAsync(url);
                response.EnsureSuccessStatusCode();

                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var latest = doc.RootElement.TryGetProperty("tag_name", out var tag)
                    ? NormalizeVersion(tag.GetString())
                    : null;

                return new AppUpdateCheck(current, latest, IsNewer(current, latest));
            }
            catch (Exception ex)
            {
                Log.Write($"[AppUpdate] Check failed: {ex.Message}");
                return new AppUpdateCheck(current, null, false);
            }
        }
    }
}
