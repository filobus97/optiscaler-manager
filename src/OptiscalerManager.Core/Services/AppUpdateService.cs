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
using System.Diagnostics;
using System.IO;
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
