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
using System.Net.Http;
using System.Reflection;
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
