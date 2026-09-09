// OptiScaler Client - A frontend for managing OptiScaler installations
// Copyright (C) 2026 Agustín Montaña (Agustinm28)
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
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using OptiscalerManager.Core.Models;

namespace OptiscalerManager.Core.Services
{
    public class GameInstallationService
    {
        private const string BackupFolderName = "OptiScalerBackup"; // kept for legacy uninstall fallback
        private const string ManifestFileName = "optiscaler_manifest.json";

        private readonly BackupStoreService _backupStore = new();
        private static readonly string[] KnownOptiscalerArtifacts =
        {
            // OptiScaler core
            "OptiScaler.ini", "OptiScaler.log", "OptiScaler.dll",
            "setup_linux.sh", "setup_windows.bat",
            @"D3D12_Optiscaler\", @"Licences\",
            "!! README_EXTRACT ALL FILES TO GAME FOLDER !!.txt",
            // "dxgi.dll", "winmm.dll", "d3d12.dll", "dbghelp.dll",
            // "version.dll", "wininet.dll", "winhttp.dll",
            // "nvngx.dll", "libxess.dll", "amdxcffx64.dll",
            // Fakenvapi
            "nvapi64.dll", "fakenvapi.ini", "fakenvapi.log", "fakenvapi.dll",
            // NukemFG
            "dlssg_to_fsr3_amd_is_better.dll",
            // FSR 4 INT8 mod
            "amd_fidelityfx_upscaler_dx12.dll",
            // Custom FSR 4.x driver DLL (user-supplied; installed next to the game exe)
            "amdxcffx64.dll",
            // OptiPatcher
            @"plugins\OptiPatcher.asi"
        };

        private static readonly string[] KnownOptiscalerDirectories =
        {
            "D3D12_Optiscaler",
            "Licenses",
            "plugins",
        };

        // Sensitive files that OptiScaler may place in the game folder but that could also
        // be native game files. Exposed publicly so the UI can present them as opt-in checkboxes.
        public static readonly string[] SensitiveArtifacts =
        {
            "amd_fidelityfx_dx12.dll",
            "amd_fidelityfx_framegeneration_dx12.dll",
            "amd_fidelityfx_vk.dll",
            "dxgi.dll",
            "libxell.dll",
            "libxess.dll",
            "libxess_dx11.dll",
            "libxess_fg.dll",
        };

        // Files that we want to track specifically for backup purposes if they exist in the game folder
        // essentially anything that OptiScaler might replace.
        // We will backup ANYTHING we overwrite, but these are known criticals.
        private readonly string[] _criticalFiles = { "dxgi.dll", "version.dll", "winmm.dll", "nvngx.dll", "nvngx_dlssg.dll", "libxess.dll" };

        public void InstallOptiScaler(Game game, string cachePath, string injectionDllName = "dxgi.dll",
                                     bool installFakenvapi = false, string fakenvapiCachePath = "",
                                     bool installNukemFG = false, string nukemFGCachePath = "",
                                     string? optiscalerVersion = null,
                                     string? overrideGameDir = null,
                                     OptiScalerProfile? profile = null)
        {
            Log.Write($"[Install] Starting OptiScaler installation for game: {game.Name}");
            Log.Write($"[Install] Version: {optiscalerVersion}, Injection: {injectionDllName}");
            Log.Write($"[Install] Cache path: {cachePath}");

            if (!Directory.Exists(cachePath))
                throw new DirectoryNotFoundException("Updates cache directory not found. Please download OptiScaler first.");

            // Verify cache is not empty
            var cacheFiles = Directory.GetFiles(cachePath, "*.*", SearchOption.AllDirectories);
            if (cacheFiles.Length == 0)
                throw new Exception("Cache directory is empty. Download update again.");

            Log.Write($"[Install] Cache contains {cacheFiles.Length} files");

            // Determine game directory intelligently (rules for base exe, Phoenix override, or user modal)
            string? gameDir;
            if (overrideGameDir != null)
            {
                gameDir = overrideGameDir;
                Log.Write($"[Install] Using override game directory: {gameDir}");
            }
            else
            {
                gameDir = DetermineInstallDirectory(game);
                if (string.IsNullOrEmpty(gameDir) || !Directory.Exists(gameDir))
                {
                    throw new Exception("Could not automatically detect the game directory. Please use Manual Install.");
                }
                Log.Write($"[Install] Detected game directory: {gameDir}");
            }

            if (string.IsNullOrEmpty(gameDir) || !Directory.Exists(gameDir))
                throw new Exception("Installation cancelled or valid directory not found.");

            // storeKey is always the stable game root (game.InstallPath) so that lookup is
            // consistent after app restarts, regardless of which subdirectory was chosen as gameDir.
            var storeKey = game.InstallPath;

            Log.Write($"[Install] External backup store: {_backupStore.GetBackupRoot(storeKey)}");

            // ── Capture prior manifest BEFORE overwriting (critical for update scenarios) ─────────
            // When updating an existing install, SaveManifest will overwrite the committed manifest
            // before any files are processed. Without this capture, the BackupFile calls below would
            // overwrite original game file backups with OptiScaler's own DLLs, corrupting uninstall.
            // Read the manifest ONCE to avoid redundant file reads (HasValidBackup + LoadManifest).
            var priorManifest = _backupStore.LoadManifest(storeKey);
            bool hasValidBackup = priorManifest != null &&
                string.Equals(priorManifest.OperationStatus, "committed", StringComparison.OrdinalIgnoreCase);
            if (!hasValidBackup) priorManifest = null; // only trust committed manifests

            // Files the game originally owned (backed up during first install) — must NOT be overwritten.
            var priorBackedUpOriginals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // Files created by a previous OptiScaler install — must be deleted (not restored) on uninstall.
            var priorCreatedByOptiScaler = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (priorManifest != null)
            {
                foreach (var r in priorManifest.FilesOverwritten)
                    priorBackedUpOriginals.Add(r.RelativePath);
                foreach (var f in priorManifest.BackedUpFiles)
                    priorBackedUpOriginals.Add(f); // legacy v1 fallback
                foreach (var r in priorManifest.FilesCreated)
                    priorCreatedByOptiScaler.Add(r.RelativePath);
                // Legacy v1: files listed in InstalledFiles but not backed up were created by OptiScaler.
                foreach (var f in priorManifest.InstalledFiles)
                    if (!priorBackedUpOriginals.Contains(f))
                        priorCreatedByOptiScaler.Add(f);
                Log.Write($"[Install] Update mode — preserving {priorBackedUpOriginals.Count} original game file backup(s), tracking {priorCreatedByOptiScaler.Count} OptiScaler-created file(s)");
            }

            // ── Pre-install: detect and remove residues from previous dirty installs ──
            // If there is no valid external backup for this gameDir, any known OptiScaler
            // artifacts found in the game folder are residues (orphaned files from a previous
            // install that was never properly uninstalled). Back them up as original files
            // would corrupt the next uninstall, so we delete them first.
            if (!hasValidBackup)
            {
                var componentService = new ComponentManagementService();
                var cacheDirsForResidue = new List<string>();
                if (!string.IsNullOrEmpty(optiscalerVersion))
                {
                    var p = componentService.GetOptiScalerCachePath(optiscalerVersion);
                    if (Directory.Exists(p)) cacheDirsForResidue.Add(p);
                }
                var fakeDir = componentService.GetFakenvapiCachePath();
                if (Directory.Exists(fakeDir)) cacheDirsForResidue.Add(fakeDir);
                var nukemDir = componentService.GetNukemFGCachePath();
                if (Directory.Exists(nukemDir)) cacheDirsForResidue.Add(nukemDir);
                var customFsr4Dir = componentService.GetCustomFsr4CachePath();
                if (Directory.Exists(customFsr4Dir)) cacheDirsForResidue.Add(customFsr4Dir);
                var customSdkDir = componentService.GetCustomFsrSdkCachePath();
                if (Directory.Exists(customSdkDir)) cacheDirsForResidue.Add(customSdkDir);

                var residues = _backupStore.FindResiduesInGameDir(gameDir, KnownOptiscalerArtifacts, cacheDirsForResidue);
                foreach (var residue in residues)
                {
                    var residuePath = Path.Combine(gameDir, residue);
                    try
                    {
                        File.Delete(residuePath);
                        Log.Write($"[Install] Deleted residue from previous install: {residue}");
                    }
                    catch (Exception ex)
                    {
                        Log.Write($"[Install] Could not delete residue '{residue}': {ex.Message}");
                    }
                }
            }

            // Create installation manifest — OptiscalerVersion is the authoritative source for the UI
            var manifest = new InstallationManifest
            {
                OperationId = Guid.NewGuid().ToString("N"),
                OperationStatus = "in_progress",
                StartedAtUtc = DateTime.UtcNow.ToString("O"),
                InjectionMethod = injectionDllName,
                InstallDate = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                OptiscalerVersion = optiscalerVersion,
                IncludesOptiscaler = true,
                IncludesFakenvapi = installFakenvapi,
                IncludesNukemFG = installNukemFG,
                // Store the EXACT directory used (already resolved for Phoenix/UE5 games).
                // Uninstall will read this directly, avoiding re-detection issues.
                InstalledGameDirectory = gameDir
            };

            manifest.PreInstallKeyFiles = CapturePreInstallKeySnapshot(gameDir, injectionDllName);
            manifest.ExpectedFinalMarkers.Add(injectionDllName);
            manifest.AppliedProfileName = profile?.Name;

            // For updates, carry over directories installed by the prior run so they are removed
            // on uninstall even though they already existed when this install started.
            if (priorManifest != null)
            {
                foreach (var dir in priorManifest.InstalledDirectories)
                    if (!manifest.InstalledDirectories.Contains(dir))
                        manifest.InstalledDirectories.Add(dir);
            }

            // Persist immediately as in-progress so crashes can be recovered later.
            _backupStore.SaveManifest(storeKey, manifest);

            try
            {

            // Find the main OptiScaler DLL (OptiScaler.dll or nvngx.dll for older versions)
            string? optiscalerMainDll = null;
            foreach (var file in cacheFiles)
            {
                var fileName = Path.GetFileName(file);
                if (fileName.Equals("OptiScaler.dll", StringComparison.OrdinalIgnoreCase) ||
                    fileName.Equals("nvngx.dll", StringComparison.OrdinalIgnoreCase))
                {
                    optiscalerMainDll = file;
                    Log.Write($"[Install] Found main OptiScaler DLL: {fileName}");
                    break;
                }
            }

            if (optiscalerMainDll == null)
                throw new Exception("Installation failed because the downloaded package is corrupt or incomplete (missing OptiScaler.dll). Please go to Settings -> Manage Cache, delete this version, and try the installation again.");

            // Step 1: Install the main OptiScaler DLL with the selected injection method name
            var injectionDllPath = Path.Combine(gameDir, injectionDllName);
            Log.Write($"[Install] Installing main DLL as: {injectionDllName}");
            var injectionExisted = File.Exists(injectionDllPath);

            // Backup existing file if it exists (into external store).
            // During an update, skip re-backing-up files that already have an original backup
            // (priorBackedUpOriginals) or that were created by a previous OptiScaler install
            // (priorCreatedByOptiScaler) — doing so would overwrite the original game file.
            bool injIsOriginal = priorBackedUpOriginals.Contains(injectionDllName);
            bool injIsOptiCreated = priorCreatedByOptiScaler.Contains(injectionDllName);
            string? injectionPreHash = null;
            if (injectionExisted && !injIsOriginal && !injIsOptiCreated)
            {
                injectionPreHash = ComputeSha256(injectionDllPath); // only hash when we actually need it
                _backupStore.BackupFile(storeKey, gameDir, injectionDllName);
                manifest.BackedUpFiles.Add(injectionDllName);
                Log.Write($"[Install] Backed up existing file: {injectionDllName}");
            }

            // Copy OptiScaler.dll as the injection DLL
            File.Copy(optiscalerMainDll, injectionDllPath, true);
            manifest.InstalledFiles.Add(injectionDllName);
            // existedBefore=false for OptiScaler-created files forces them into FilesCreated (delete on uninstall).
            // existedBefore=true for game-original files keeps them in FilesOverwritten (restore on uninstall).
            TrackManifestFileMutation(
                manifest,
                relativePath: injectionDllName,
                existedBefore: injectionExisted && !injIsOptiCreated,
                preInstallHash: injectionPreHash,
                postInstallHash: ComputeSha256(injectionDllPath));
            Log.Write($"[Install] Installed main OptiScaler DLL");

            // Step 2: Copy all other files (configs, dependencies, etc.)
            Log.Write($"[Install] Copying additional files...");
            var additionalFileCount = 0;

            foreach (var sourcePath in cacheFiles)
            {
                var fileName = Path.GetFileName(sourcePath);

                // Skip the main OptiScaler DLL as we already handled it
                if (fileName.Equals("OptiScaler.dll", StringComparison.OrdinalIgnoreCase) ||
                    fileName.Equals("nvngx.dll", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var relativePath = Path.GetRelativePath(cachePath, sourcePath);
                var destPath = Path.Combine(gameDir, relativePath);
                var destDir = Path.GetDirectoryName(destPath);

                // Track created directories
                if (destDir != null && !Directory.Exists(destDir))
                {
                    Directory.CreateDirectory(destDir);
                    Log.Write($"[Install] Created directory: {Path.GetRelativePath(gameDir, destDir)}");

                    // Add to manifest (relative to game directory)
                    var relativeDir = Path.GetRelativePath(gameDir, destDir);
                    if (!manifest.InstalledDirectories.Contains(relativeDir))
                    {
                        manifest.InstalledDirectories.Add(relativeDir);
                    }
                }

                // Backup existing file if needed (into external store).
                // During an update, skip files already protected by a prior install's backup.
                bool existedBefore = File.Exists(destPath);
                bool fileIsOriginal = priorBackedUpOriginals.Contains(relativePath);
                bool fileIsOptiCreated = priorCreatedByOptiScaler.Contains(relativePath);
                string? preHash = null;
                if (existedBefore && !fileIsOriginal && !fileIsOptiCreated)
                {
                    preHash = ComputeSha256(destPath); // only hash when we actually need it
                    _backupStore.BackupFile(storeKey, gameDir, relativePath);
                    manifest.BackedUpFiles.Add(relativePath);
                    Log.Write($"[Install] Backed up existing file: {relativePath}");
                }

                File.Copy(sourcePath, destPath, true);
                manifest.InstalledFiles.Add(relativePath);
                TrackManifestFileMutation(
                    manifest,
                    relativePath: relativePath,
                    existedBefore: existedBefore && !fileIsOptiCreated,
                    preInstallHash: (!fileIsOriginal && !fileIsOptiCreated) ? preHash : null,
                    postInstallHash: ComputeSha256(destPath));
                additionalFileCount++;
            }

            Log.Write($"[Install] Copied {additionalFileCount} additional files");

            // Step 2.4: Keep the settings the player already has.
            //
            // Step 2 above copies the release's own OptiScaler.ini over the game folder.
            // That file counts as "OptiScaler-created", so the backup-before-overwrite
            // guard deliberately skips it — right for DLLs, wrong for a config the
            // player has changed, including every setting OptiScaler itself writes back
            // when they adjust something in the in-game overlay. Without this,
            // reinstalling (a new OptiScaler version, a different backend) silently
            // resets their configuration with no backup to restore from.
            //
            // So when reinstalling over an existing install and the caller has not
            // supplied a profile of its own, snapshot what is on disk and let it win
            // over the release defaults.
            var effectiveProfile = profile;
            if (priorManifest != null && (effectiveProfile == null || effectiveProfile.IniSettings.Count == 0))
            {
                var existingIni = Path.Combine(gameDir, "OptiScaler.ini");
                if (File.Exists(existingIni))
                {
                    try
                    {
                        var preserved = new ProfileManagementService()
                            .CreateProfileFromIni(existingIni, "__PreservedOnReinstall");
                        if (preserved.IniSettings.Count > 0)
                        {
                            effectiveProfile = preserved;
                            var keys = preserved.IniSettings.Sum(sec => sec.Value.Count);
                            Log.Write($"[Install] Preserving {keys} existing OptiScaler.ini setting(s) across the reinstall");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Write($"[Install] Could not read the existing OptiScaler.ini to preserve it: {ex.Message}");
                    }
                }
            }

            // Step 2.5: Generate OptiScaler.ini from profile if provided (skip for Default profile)
            if (effectiveProfile != null && effectiveProfile.IniSettings.Count > 0)
            {
                try
                {
                    var profileService = new ProfileManagementService();
                    profileService.WriteOptiScalerIniToFile(gameDir, effectiveProfile);
                    Log.Write($"[Install] Generated OptiScaler.ini from profile: {effectiveProfile.Name}");
                }
                catch (Exception ex)
                {
                    Log.Write($"[Install] Warning: Failed to generate OptiScaler.ini from profile: {ex.Message}");
                }
            }
            else if (profile != null && profile.Name == "Default")
            {
                Log.Write($"[Install] Using Default profile - OptiScaler will use its default configuration");
            }

            // Step 3: Install Fakenvapi if requested (AMD/Intel only)
            if (installFakenvapi && !string.IsNullOrEmpty(fakenvapiCachePath) && Directory.Exists(fakenvapiCachePath))
            {
                Log.Write($"[Install] Installing Fakenvapi...");
                var fakeFiles = Directory.GetFiles(fakenvapiCachePath, "*.*", SearchOption.AllDirectories);
                var fakeFileCount = 0;

                foreach (var sourcePath in fakeFiles)
                {
                    var fileName = Path.GetFileName(sourcePath);

                    // Only copy nvapi64.dll and fakenvapi.ini
                    if (fileName.Equals("nvapi64.dll", StringComparison.OrdinalIgnoreCase) ||
                        fileName.Equals("fakenvapi.ini", StringComparison.OrdinalIgnoreCase))
                    {
                        var destPath = Path.Combine(gameDir, fileName);
                        var existedBefore = File.Exists(destPath);

                        // Backup if exists (into external store).
                        // During an update, skip files already protected by a prior install's backup.
                        bool fakeIsOriginal = priorBackedUpOriginals.Contains(fileName);
                        bool fakeIsOptiCreated = priorCreatedByOptiScaler.Contains(fileName);
                        string? preHash = null;
                        if (existedBefore && !fakeIsOriginal && !fakeIsOptiCreated)
                        {
                            preHash = ComputeSha256(destPath); // only hash when we actually need it
                            _backupStore.BackupFile(storeKey, gameDir, fileName);
                            manifest.BackedUpFiles.Add(fileName);
                            Log.Write($"[Install] Backed up existing Fakenvapi file: {fileName}");
                        }

                        File.Copy(sourcePath, destPath, true);
                        manifest.InstalledFiles.Add(fileName);
                        TrackManifestFileMutation(
                            manifest,
                            relativePath: fileName,
                            existedBefore: existedBefore && !fakeIsOptiCreated,
                            preInstallHash: (!fakeIsOriginal && !fakeIsOptiCreated) ? preHash : null,
                            postInstallHash: ComputeSha256(destPath));
                        fakeFileCount++;
                        Log.Write($"[Install] Installed Fakenvapi file: {fileName}");
                    }
                }

                Log.Write($"[Install] Installed {fakeFileCount} Fakenvapi files");
                if (fakeFileCount > 0)
                {
                    manifest.IncludesFakenvapi = true;
                    manifest.ExpectedFinalMarkers.Add("nvapi64.dll");
                }
                else
                {
                    throw new Exception("Installation failed because the Fakenvapi package is corrupt or incomplete.");
                }
            }

            // Step 4: Install NukemFG if requested
            if (installNukemFG && !string.IsNullOrEmpty(nukemFGCachePath) && Directory.Exists(nukemFGCachePath))
            {
                Log.Write($"[Install] Installing NukemFG...");
                var nukemFiles = Directory.GetFiles(nukemFGCachePath, "*.*", SearchOption.AllDirectories);
                var nukemFileCount = 0;

                foreach (var sourcePath in nukemFiles)
                {
                    var fileName = Path.GetFileName(sourcePath);

                    // ONLY copy dlssg_to_fsr3_amd_is_better.dll
                    // DO NOT copy nvngx.dll (200kb) - it will break the mod!
                    if (fileName.Equals("dlssg_to_fsr3_amd_is_better.dll", StringComparison.OrdinalIgnoreCase))
                    {
                        var destPath = Path.Combine(gameDir, fileName);
                        var existedBefore = File.Exists(destPath);

                        // Backup if exists (into external store).
                        // During an update, skip files already protected by a prior install's backup.
                        bool nukemIsOriginal = priorBackedUpOriginals.Contains(fileName);
                        bool nukemIsOptiCreated = priorCreatedByOptiScaler.Contains(fileName);
                        string? preHash = null;
                        if (existedBefore && !nukemIsOriginal && !nukemIsOptiCreated)
                        {
                            preHash = ComputeSha256(destPath); // only hash when we actually need it
                            _backupStore.BackupFile(storeKey, gameDir, fileName);
                            manifest.BackedUpFiles.Add(fileName);
                            Log.Write($"[Install] Backed up existing NukemFG file: {fileName}");
                        }

                        File.Copy(sourcePath, destPath, true);
                        manifest.InstalledFiles.Add(fileName);
                        TrackManifestFileMutation(
                            manifest,
                            relativePath: fileName,
                            existedBefore: existedBefore && !nukemIsOptiCreated,
                            preInstallHash: (!nukemIsOriginal && !nukemIsOptiCreated) ? preHash : null,
                            postInstallHash: ComputeSha256(destPath));
                        nukemFileCount++;
                        Log.Write($"[Install] Installed NukemFG file: {fileName}");

                        // Select Nukem's mod as the FG input. OptiScaler 0.9.x reads
                        // [FrameGen] FGInput; the legacy sectionless FGType key is dead.
                        // Enabled is required as well: OptiScaler defaults it to false,
                        // so setting only the input deploys the DLL but never actually
                        // turns frame generation on. FGOutput follows FGInput=nukems
                        // automatically inside OptiScaler.
                        ModifyOptiScalerIniKey(gameDir, "FrameGen", "Enabled", "true");
                        ModifyOptiScalerIniKey(gameDir, "FrameGen", "FGInput", "nukems");
                        Log.Write($"[Install] Set [FrameGen] Enabled=true, FGInput=nukems for NukemFG");
                    }
                }

                Log.Write($"[Install] Installed {nukemFileCount} NukemFG files");
                if (nukemFileCount > 0)
                {
                    manifest.IncludesNukemFG = true;
                    manifest.ExpectedFinalMarkers.Add("dlssg_to_fsr3_amd_is_better.dll");
                }
                else
                {
                    throw new Exception("Installation failed because the NukemFG package is corrupt or incomplete.");
                }
            }

            // Save manifest to external store
            manifest.ExpectedFinalMarkers = manifest.ExpectedFinalMarkers.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            manifest.OperationStatus = "committed";
            manifest.FinishedAtUtc = DateTime.UtcNow.ToString("O");
            _backupStore.SaveManifest(storeKey, manifest);
            Log.Write($"[Install] Saved installation manifest to external store");

            // Immediately update the game object so the UI reflects the correct state
            // without waiting for the next full scan/analysis cycle.
            game.IsOptiscalerInstalled = true;
            if (!string.IsNullOrEmpty(optiscalerVersion))
                game.OptiscalerVersion = optiscalerVersion;

            // Post-Install: Re-analyze to refresh DLSS/FSR/XeSS fields.
            // AnalyzeGame will also confirm OptiscalerVersion via the manifest.
            Log.Write($"[Install] Re-analyzing game to update component information...");
            var analyzer = new GameAnalyzerService();
            GameAnalyzerService.InvalidateCacheForPath(game.InstallPath);
            analyzer.AnalyzeGame(game, forceRefresh: true);
            GameAnalyzerService.FlushCacheToDisk();

            Log.Write($"[Install] OptiScaler installation completed successfully for {game.Name}");
            Log.Write($"[Install] Total files installed: {manifest.InstalledFiles.Count}");
            Log.Write($"[Install] Total files backed up: {manifest.BackedUpFiles.Count}");
            }
            catch (Exception ex)
            {
                Log.Write($"[Install] Installation failed. Starting rollback. Error: {ex.Message}");

                manifest.OperationStatus = "failed";
                manifest.FinishedAtUtc = DateTime.UtcNow.ToString("O");
                _backupStore.SaveManifest(storeKey, manifest);

                var backupFilesDir = _backupStore.GetFilesDir(storeKey);
                var rollbackSummary = RollbackFailedInstall(gameDir, backupFilesDir, manifest);
                Log.Write($"[Install] Rollback completed. Restored={rollbackSummary.Restored}, Deleted={rollbackSummary.Deleted}");

                // Clean up the external store entry for this failed install
                _backupStore.DeleteBackup(storeKey);

                throw new Exception($"{ex.Message}", ex);
            }
        }

        public void UninstallOptiScaler(Game game)
        {
            // ── Determine candidate root directory ───────────────────────────────
            // We need a starting point to search for the manifest.
            string? rootDir = null;

            if (!string.IsNullOrEmpty(game.ExecutablePath))
                rootDir = Path.GetDirectoryName(game.ExecutablePath);

            if (string.IsNullOrEmpty(rootDir) && !string.IsNullOrEmpty(game.InstallPath))
                rootDir = game.InstallPath;

            if (string.IsNullOrEmpty(rootDir) || !Directory.Exists(rootDir))
                throw new Exception($"Invalid game directory: ExecutablePath='{game.ExecutablePath}', InstallPath='{game.InstallPath}'");

            // ── Load manifest: prefer external backup store, fall back to legacy in-folder ──
            string? gameDir = null;
            string? storeKey = null; // slug key for backup store (= game.InstallPath for new installs)
            InstallationManifest? manifest = null;
            bool usingExternalStore = false;

            // Priority 1: Try to resolve gameDir from external backup store.
            var candidateDirs = new List<string>();
            if (!string.IsNullOrEmpty(game.ExecutablePath) && File.Exists(game.ExecutablePath))
                candidateDirs.Add(Path.GetDirectoryName(game.ExecutablePath)!);
            if (!string.IsNullOrEmpty(game.InstallPath) && Directory.Exists(game.InstallPath))
            {
                candidateDirs.Add(game.InstallPath);
                var phoenixCandidate = Path.Combine(game.InstallPath, "Phoenix", "Binaries", "Win64");
                if (Directory.Exists(phoenixCandidate))
                    candidateDirs.Add(phoenixCandidate);
            }

            foreach (var candidate in candidateDirs.Where(d => !string.IsNullOrEmpty(d)))
            {
                if (_backupStore.HasValidBackup(candidate!))
                {
                    storeKey = candidate;
                    manifest = _backupStore.LoadManifest(candidate!);
                    usingExternalStore = true;
                    // Resolve actual game directory from the manifest so file operations target
                    // the correct subdirectory (e.g. ..\Game\ for Elden Ring, not the root).
                    var installedDir = manifest?.InstalledGameDirectory;
                    gameDir = !string.IsNullOrEmpty(installedDir) && Directory.Exists(installedDir)
                        ? installedDir
                        : candidate;
                    Log.Write($"[Uninstall] Found external backup for '{game.Name}' at store key: {candidate}, game dir: {gameDir}");
                    break;
                }
            }

            // Priority 2: Fall back to searching for legacy in-folder manifest
            if (!usingExternalStore)
            {
                string? legacyManifestPath = null;
                try
                {
                    var searchOptions = new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        IgnoreInaccessible = true,
                        MatchCasing = MatchCasing.CaseInsensitive
                    };
                    var manifests = Directory.GetFiles(rootDir, ManifestFileName, searchOptions);
                    if (manifests.Length > 0)
                        legacyManifestPath = manifests[0];
                }
                catch (Exception ex)
                {
                    Log.Write($"[Uninstall] Legacy manifest search failed: {ex.Message}");
                }

                if (legacyManifestPath != null && File.Exists(legacyManifestPath))
                {
                    try
                    {
                        var manifestJson = File.ReadAllText(legacyManifestPath);
                        manifest = JsonSerializer.Deserialize(manifestJson, OptimizerContext.Default.InstallationManifest);
                    }
                    catch (Exception ex)
                    {
                        Log.Write($"[Uninstall] Corrupt legacy manifest at '{legacyManifestPath}': {ex.Message}");
                    }

                    if (manifest?.InstalledGameDirectory != null && Directory.Exists(manifest.InstalledGameDirectory))
                        gameDir = manifest.InstalledGameDirectory;
                    else if (legacyManifestPath != null)
                        gameDir = Path.GetDirectoryName(Path.GetDirectoryName(legacyManifestPath));
                }
            }

            // Priority 3: last-resort re-detection
            if (string.IsNullOrEmpty(gameDir))
                gameDir = DetectCorrectInstallDirectory(rootDir);

            if (string.IsNullOrEmpty(gameDir) || !Directory.Exists(gameDir))
                throw new Exception($"Could not determine installation directory for '{game.Name}'.");

            var backupDir = _backupStore.GetFilesDir(storeKey ?? gameDir);
            var legacyBackupDir = Path.Combine(gameDir, BackupFolderName);

            // If legacy folder exists but no external backup, migrate now (in case startup migration was skipped)
            if (!usingExternalStore && Directory.Exists(legacyBackupDir))
            {
                var legacyMPath = Path.Combine(legacyBackupDir, ManifestFileName);
                if (File.Exists(legacyMPath))
                {
                    try
                    {
                        _backupStore.MigrateFromLegacy(legacyMPath);
                        if (_backupStore.HasValidBackup(gameDir))
                        {
                            manifest = _backupStore.LoadManifest(gameDir);
                            usingExternalStore = true;
                            storeKey = gameDir; // legacy migration uses gameDir as its slug key
                            backupDir = _backupStore.GetFilesDir(gameDir);
                            Log.Write($"[Uninstall] On-demand migration completed for '{game.Name}'");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Write($"[Uninstall] On-demand migration failed, using legacy folder directly: {ex.Message}");
                        backupDir = legacyBackupDir; // fall back to legacy folder as source
                    }
                }
            }

            if (manifest != null)
            {
                // ── Manifest-based uninstallation (precise) ───────────────────────

                // Step 1: Delete files created by install.
                // If v2 tracking is unavailable, fall back to legacy InstalledFiles list.
                var filesToDelete = manifest.FilesCreated.Count > 0
                    ? manifest.FilesCreated.Select(f => f.RelativePath)
                    : manifest.InstalledFiles;

                foreach (var installedFile in filesToDelete.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        var filePath = Path.Combine(gameDir, installedFile);
                        if (File.Exists(filePath))
                            File.Delete(filePath);
                    }
                    catch (Exception ex) { Log.Write($"[Uninstall] Failed to delete '{installedFile}': {ex.Message}"); }
                }

                // Step 2: Restore overwritten files from backup (external store or legacy folder).
                // If v2 tracking is unavailable, fall back to legacy BackedUpFiles list.
                if (manifest.FilesOverwritten.Count > 0)
                {
                    foreach (var overwritten in manifest.FilesOverwritten)
                    {
                        try
                        {
                            if (!_backupStore.RestoreFile(storeKey ?? gameDir, gameDir, overwritten.RelativePath, overwritten.BackupRelativePath))
                            {
                                // Fallback: try legacy in-folder backup
                                var legacyBackupPath = Path.Combine(legacyBackupDir,
                                    overwritten.BackupRelativePath ?? overwritten.RelativePath);
                                if (File.Exists(legacyBackupPath))
                                    File.Copy(legacyBackupPath, Path.Combine(gameDir, overwritten.RelativePath), overwrite: true);
                            }
                        }
                        catch (Exception ex) { Log.Write($"[Uninstall] Failed to restore '{overwritten.RelativePath}': {ex.Message}"); }
                    }
                }
                else
                {
                    foreach (var backedUpFile in manifest.BackedUpFiles)
                    {
                        try
                        {
                            if (!_backupStore.RestoreFile(storeKey ?? gameDir, gameDir, backedUpFile))
                            {
                                // Fallback: try legacy in-folder backup
                                var legacyBackupPath = Path.Combine(legacyBackupDir, backedUpFile);
                                if (File.Exists(legacyBackupPath))
                                    File.Copy(legacyBackupPath, Path.Combine(gameDir, backedUpFile), overwrite: true);
                            }
                        }
                        catch (Exception ex) { Log.Write($"[Uninstall] Failed to restore backup '{backedUpFile}': {ex.Message}"); }
                    }
                }

                // Step 3: Remove installed (now-empty) subdirectories, deepest first
                foreach (var installedDir in manifest.InstalledDirectories.OrderByDescending(d => d.Length))
                {
                    try
                    {
                        var dirPath = Path.Combine(gameDir, installedDir);
                        if (Directory.Exists(dirPath) && !Directory.EnumerateFileSystemEntries(dirPath).Any())
                            Directory.Delete(dirPath, false);
                    }
                    catch (Exception ex) { Log.Write($"[Uninstall] Failed to remove directory '{installedDir}': {ex.Message}"); }
                }

                // Step 3b: Unconditionally remove known OptiScaler directories.
                // These may still exist if they contained files not tracked in the manifest
                // (e.g. after an update where the directory already existed pre-install).
                foreach (var knownDir in KnownOptiscalerDirectories)
                {
                    var dirPath = Path.Combine(gameDir, knownDir);
                    try
                    {
                        if (Directory.Exists(dirPath))
                        {
                            Directory.Delete(dirPath, true);
                            Log.Write($"[Uninstall] Removed known OptiScaler directory: {knownDir}");
                        }
                    }
                    catch (Exception ex) { Log.Write($"[Uninstall] Failed to remove known directory '{knownDir}': {ex.Message}"); }
                }
            }
            else
            {
                // ── Legacy fallback (no manifest present) ─────────────────────────
                // Covers installations created before the manifest system was introduced.

                // Collect all directories to scan: gameDir + Phoenix subdir if present
                var dirsToScan = new List<string> { gameDir };
                var phoenixDir = DetectCorrectInstallDirectory(gameDir);
                if (!phoenixDir.Equals(gameDir, StringComparison.OrdinalIgnoreCase))
                    dirsToScan.Add(phoenixDir);

                // Restore backed-up files first
                foreach (var dir in dirsToScan)
                {
                    var innerLegacyBackupDir = Path.Combine(dir, BackupFolderName);
                    if (Directory.Exists(innerLegacyBackupDir))
                    {
                        foreach (var backupFile in Directory.GetFiles(innerLegacyBackupDir, "*.*", SearchOption.AllDirectories))
                        {
                            try
                            {
                                var relativePath = Path.GetRelativePath(innerLegacyBackupDir, backupFile);
                                if (relativePath.Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase))
                                    continue;

                                var destPath = Path.Combine(dir, relativePath);
                                File.Copy(backupFile, destPath, overwrite: true);
                            }
                            catch (Exception ex) { Log.Write($"[Uninstall] Failed to restore legacy backup file: {ex.Message}"); }
                        }

                        try { Directory.Delete(innerLegacyBackupDir, true); }
                        catch (Exception ex) { Log.Write($"[Uninstall] Failed to delete legacy backup dir: {ex.Message}"); }
                    }
                }

                foreach (var dir in dirsToScan)
                {
                    var innerLegacyBackupDir2 = Path.Combine(dir, BackupFolderName);
                    foreach (var fileName in KnownOptiscalerArtifacts)
                    {
                        var filePath = Path.Combine(dir, fileName);
                        if (!File.Exists(filePath)) continue;

                        try
                        {
                            // Always delete OptiScaler config/log
                            if (fileName.StartsWith("OptiScaler", StringComparison.OrdinalIgnoreCase))
                            {
                                File.Delete(filePath);
                                continue;
                            }

                            // For DLLs: only delete if there was no original backup
                            // (backup dir was already deleted above, so !Directory.Exists is true
                            // when there was no backup — safe to delete)
                            var backupPath = Path.Combine(legacyBackupDir, fileName);
                            if (!File.Exists(backupPath) && !Directory.Exists(legacyBackupDir))
                                File.Delete(filePath);
                        }
                        catch (Exception ex) { Log.Write($"[Uninstall] Failed to clean legacy artifact '{fileName}': {ex.Message}"); }
                    }
                }
            }

            // Clean up runtime-generated files that no game would have
            // (these are created when the game runs with OptiScaler, not during install)
            foreach (var runtimeFile in new[] { "OptiScaler.log", "fakenvapi.log", "fakenvapi.ini" })
            {
                var runtimePath = Path.Combine(gameDir, runtimeFile);
                try { if (File.Exists(runtimePath)) File.Delete(runtimePath); }
                catch (Exception ex) { Log.Write($"[Uninstall] Could not delete runtime file '{runtimeFile}': {ex.Message}"); }
            }

            // Remove external backup store entry
            _backupStore.DeleteBackup(storeKey ?? gameDir);

            // Remove legacy OptiScalerBackup/ folder if it still exists
            // (first uninstall after migration from v1.0.4, or if migration was skipped)
            if (Directory.Exists(legacyBackupDir))
            {
                try { Directory.Delete(legacyBackupDir, true); }
                catch (Exception ex) { Log.Write($"[Uninstall] Could not remove legacy backup directory: {ex.Message}"); }
            }

            // Clear game state immediately so the UI reflects the uninstallation
            game.IsOptiscalerInstalled = false;
            game.OptiscalerVersion = null;
            game.Fsr4ExtraVersion = null;
            game.CustomFsr4DllVersion = null;
            game.CustomFsrSdkVersion = null;

            // Re-analyze to refresh DLSS/FSR/XeSS detection after files were removed/restored
            var analyzer = new GameAnalyzerService();
            GameAnalyzerService.InvalidateCacheForPath(game.InstallPath);
            analyzer.AnalyzeGame(game, forceRefresh: true);
            GameAnalyzerService.FlushCacheToDisk();
        }

        public bool RecoverIncompleteInstallIfNeeded(string installRoot)
        {
            if (string.IsNullOrWhiteSpace(installRoot) || !Directory.Exists(installRoot))
                return false;

            string? manifestPath = null;
            try
            {
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    MatchCasing = MatchCasing.CaseInsensitive
                };

                manifestPath = Directory.GetFiles(installRoot, ManifestFileName, options).FirstOrDefault();
            }
            catch
            {
                return false;
            }

            if (string.IsNullOrEmpty(manifestPath) || !File.Exists(manifestPath))
                return false;

            InstallationManifest? manifest;
            try
            {
                var manifestJson = File.ReadAllText(manifestPath);
                manifest = JsonSerializer.Deserialize(manifestJson, OptimizerContext.Default.InstallationManifest);
            }
            catch
            {
                return false;
            }

            if (manifest == null)
                return false;

            if (string.Equals(manifest.OperationStatus, "committed", StringComparison.OrdinalIgnoreCase))
                return false;

            var gameDir = !string.IsNullOrWhiteSpace(manifest.InstalledGameDirectory) &&
                          Directory.Exists(manifest.InstalledGameDirectory)
                ? manifest.InstalledGameDirectory
                : Path.GetDirectoryName(Path.GetDirectoryName(manifestPath));

            if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir))
                return false;

            var backupDir = Path.Combine(gameDir, BackupFolderName);
            Log.Write($"[Recovery] Found incomplete install manifest (status={manifest.OperationStatus}). Starting recovery for: {gameDir}");

            var rollbackSummary = RollbackFailedInstall(gameDir, backupDir, manifest);
            ValidateAndHealPostUninstall(gameDir, backupDir, manifest);

            Log.Write($"[Recovery] Completed. Restored={rollbackSummary.Restored}, Deleted={rollbackSummary.Deleted}");
            return true;
        }

        private List<KeyFileSnapshot> CapturePreInstallKeySnapshot(string gameDir, string injectionDllName)
        {
            var keys = new HashSet<string>(_criticalFiles, StringComparer.OrdinalIgnoreCase)
            {
                injectionDllName,
                "OptiScaler.ini",
                "nvapi64.dll",
                "dlssg_to_fsr3_amd_is_better.dll",
                "amd_fidelityfx_upscaler_dx12.dll",
                "amdxcffx64.dll"
            };

            var snapshots = new List<KeyFileSnapshot>();
            foreach (var relPath in keys)
            {
                var fullPath = Path.Combine(gameDir, relPath);
                var existed = File.Exists(fullPath);
                snapshots.Add(new KeyFileSnapshot
                {
                    RelativePath = relPath,
                    Existed = existed,
                    Sha256 = existed ? ComputeSha256(fullPath) : null
                });
            }

            return snapshots.OrderBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static void TrackManifestFileMutation(
            InstallationManifest manifest,
            string relativePath,
            bool existedBefore,
            string? preInstallHash,
            string? postInstallHash)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                return;

            if (existedBefore)
            {
                var record = manifest.FilesOverwritten.FirstOrDefault(
                    x => x.RelativePath.Equals(relativePath, StringComparison.OrdinalIgnoreCase));

                if (record == null)
                {
                    record = new ManifestFileRecord
                    {
                        RelativePath = relativePath,
                        BackupRelativePath = relativePath
                    };
                    manifest.FilesOverwritten.Add(record);
                }

                record.ExistedBefore = true;
                record.PreInstallSha256 = preInstallHash;
                record.PostInstallSha256 = postInstallHash;
            }
            else
            {
                var record = manifest.FilesCreated.FirstOrDefault(
                    x => x.RelativePath.Equals(relativePath, StringComparison.OrdinalIgnoreCase));

                if (record == null)
                {
                    record = new ManifestFileRecord
                    {
                        RelativePath = relativePath,
                        BackupRelativePath = null
                    };
                    manifest.FilesCreated.Add(record);
                }

                record.ExistedBefore = false;
                record.PreInstallSha256 = null;
                record.PostInstallSha256 = postInstallHash;
            }
        }

        private static string? ComputeSha256(string filePath)
        {
            try
            {
                using var sha = SHA256.Create();
                using var stream = File.OpenRead(filePath);
                var hash = sha.ComputeHash(stream);
                return Convert.ToHexString(hash);
            }
            catch
            {
                return null;
            }
        }

        private static void SaveManifest(string manifestPath, InstallationManifest manifest)
        {
            var manifestJson = JsonSerializer.Serialize(manifest, OptimizerContext.Default.InstallationManifest);
            File.WriteAllText(manifestPath, manifestJson);
        }

        private RollbackResult RollbackFailedInstall(string gameDir, string backupDir, InstallationManifest manifest)
        {
            var result = new RollbackResult();

            foreach (var record in manifest.FilesCreated)
            {
                var fullPath = Path.Combine(gameDir, record.RelativePath);
                if (TryDeleteFileIfExists(fullPath))
                    result.Deleted++;
            }

            foreach (var record in manifest.FilesOverwritten)
            {
                if (!record.ExistedBefore)
                    continue;

                if (TryRestoreFromBackup(gameDir, backupDir, record.RelativePath, record.BackupRelativePath))
                    result.Restored++;
            }

            if (manifest.FilesCreated.Count == 0)
            {
                foreach (var rel in manifest.InstalledFiles.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var fullPath = Path.Combine(gameDir, rel);
                    if (TryDeleteFileIfExists(fullPath))
                        result.Deleted++;
                }
            }

            if (manifest.FilesOverwritten.Count == 0)
            {
                foreach (var rel in manifest.BackedUpFiles.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (TryRestoreFromBackup(gameDir, backupDir, rel, rel))
                        result.Restored++;
                }
            }

            return result;
        }

        private void ValidateAndHealPostUninstall(string gameDir, string backupDir, InstallationManifest? manifest)
        {
            var preInstallState = BuildPreInstallStateMap(manifest);
            var deletedResidues = 0;
            var restoredFiles = 0;

            // 1) Ensure files created by install are removed.
            if (manifest != null)
            {
                foreach (var record in manifest.FilesCreated)
                {
                    var fullPath = Path.Combine(gameDir, record.RelativePath);
                    if (TryDeleteFileIfExists(fullPath))
                    {
                        deletedResidues++;
                        Log.Write($"[Uninstall][Validate] Removed residue created by install: {record.RelativePath}");
                    }
                }

                // 2) Ensure overwritten files were restored.
                foreach (var record in manifest.FilesOverwritten)
                {
                    if (!record.ExistedBefore)
                        continue;

                    var targetPath = Path.Combine(gameDir, record.RelativePath);
                    var currentHash = File.Exists(targetPath) ? ComputeSha256(targetPath) : null;
                    var hashMismatch = !string.IsNullOrEmpty(record.PreInstallSha256) &&
                                       !string.Equals(record.PreInstallSha256, currentHash, StringComparison.OrdinalIgnoreCase);

                    if (!File.Exists(targetPath) || hashMismatch)
                    {
                        if (TryRestoreFromBackup(gameDir, backupDir, record.RelativePath, record.BackupRelativePath))
                        {
                            restoredFiles++;
                            Log.Write($"[Uninstall][Validate] Restored overwritten file from backup: {record.RelativePath}");
                        }
                    }
                }
            }

            // 3) Fallback sweep over known artifacts.
            // Only delete if we can confirm OptiScaler created the file (it's in FilesCreated).
            // Never delete files that were backed up/restored (game-native) or files
            // that OptiScaler never touched (not in the manifest at all).
            var backedUpFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (manifest != null)
            {
                foreach (var f in manifest.FilesOverwritten)
                    backedUpFiles.Add(f.RelativePath);
                foreach (var f in manifest.BackedUpFiles)
                    backedUpFiles.Add(f);
            }

            foreach (var relativePath in KnownOptiscalerArtifacts)
            {
                var fullPath = Path.Combine(gameDir, relativePath);
                if (!File.Exists(fullPath))
                    continue;

                // Skip files that were backed up — they belong to the game
                if (backedUpFiles.Contains(relativePath))
                    continue;

                // If we have a pre-install snapshot showing the file existed, try to restore it
                if (preInstallState.TryGetValue(relativePath, out var snapshot) && snapshot.Existed)
                {
                    var currentHash = ComputeSha256(fullPath);
                    var expectedHash = snapshot.Sha256;
                    var mismatch = !string.IsNullOrEmpty(expectedHash) &&
                                   !string.Equals(expectedHash, currentHash, StringComparison.OrdinalIgnoreCase);

                    if (mismatch && TryRestoreFromBackup(gameDir, backupDir, relativePath, relativePath))
                    {
                        restoredFiles++;
                        Log.Write($"[Uninstall][Validate] Restored key file to pre-install state: {relativePath}");
                    }
                    continue;
                }

                // Only delete if this file was created by OptiScaler (not a game file)
                var wasCreatedByInstall = manifest?.FilesCreated
                    .Any(f => f.RelativePath.Equals(relativePath, StringComparison.OrdinalIgnoreCase)) ?? false;

                if (wasCreatedByInstall)
                {
                    if (TryDeleteFileIfExists(fullPath))
                    {
                        deletedResidues++;
                        Log.Write($"[Uninstall][Validate] Removed known residue: {relativePath}");
                    }
                }
            }

            // NOTE: Backup directory cleanup is now handled by ForceRemoveAllArtifacts.

            Log.Write($"[Uninstall][Validate] Validation completed. Restored={restoredFiles}, ResiduesRemoved={deletedResidues}");
        }

        private void ForceRemoveAllArtifacts(string gameDir, IEnumerable<string>? extraFilesToDelete = null)
        {
            var dirsToScan = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { gameDir };
            var phoenixDir = DetectCorrectInstallDirectory(gameDir);
            if (!phoenixDir.Equals(gameDir, StringComparison.OrdinalIgnoreCase))
                dirsToScan.Add(phoenixDir);

            var deletedCount = 0;

            foreach (var dir in dirsToScan)
            {
                // Delete every known artifact file unconditionally
                foreach (var artifact in KnownOptiscalerArtifacts)
                {
                    var fullPath = Path.Combine(dir, artifact);
                    try
                    {
                        if (File.Exists(fullPath))
                        {
                            File.Delete(fullPath);
                            deletedCount++;
                            Log.Write($"[Uninstall][ForceClean] Deleted file: {artifact}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Write($"[Uninstall][ForceClean] Could not delete '{artifact}': {ex.Message}");
                    }
                }

                // Delete every known OptiScaler directory unconditionally (including contents)
                foreach (var knownDir in KnownOptiscalerDirectories)
                {
                    var fullPath = Path.Combine(dir, knownDir);
                    try
                    {
                        if (Directory.Exists(fullPath))
                        {
                            Directory.Delete(fullPath, true);
                            Log.Write($"[Uninstall][ForceClean] Deleted directory: {knownDir}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Write($"[Uninstall][ForceClean] Could not delete directory '{knownDir}': {ex.Message}");
                    }
                }

                // Remove legacy OptiScalerBackup directory
                var backupDir = Path.Combine(dir, BackupFolderName);
                try
                {
                    if (Directory.Exists(backupDir))
                    {
                        Directory.Delete(backupDir, true);
                        Log.Write("[Uninstall][ForceClean] Removed backup directory.");
                    }
                }
                catch (Exception ex)
                {
                    Log.Write($"[Uninstall][ForceClean] Could not remove backup directory: {ex.Message}");
                }

                // Delete user-selected sensitive files
                if (extraFilesToDelete != null)
                {
                    foreach (var sensitiveFile in extraFilesToDelete)
                    {
                        var fullPath = Path.Combine(dir, sensitiveFile);
                        try
                        {
                            if (File.Exists(fullPath))
                            {
                                File.Delete(fullPath);
                                deletedCount++;
                                Log.Write($"[Uninstall][ForceClean] Deleted sensitive file: {sensitiveFile}");
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Write($"[Uninstall][ForceClean] Could not delete sensitive file '{sensitiveFile}': {ex.Message}");
                        }
                    }
                }
            }

            Log.Write($"[Uninstall][ForceClean] Final sweep completed. Files removed: {deletedCount}");
        }

        private sealed class RollbackResult
        {
            public int Restored { get; set; }
            public int Deleted { get; set; }
        }

        private static Dictionary<string, KeyFileSnapshot> BuildPreInstallStateMap(InstallationManifest? manifest)
        {
            var map = new Dictionary<string, KeyFileSnapshot>(StringComparer.OrdinalIgnoreCase);
            if (manifest?.PreInstallKeyFiles == null)
                return map;

            foreach (var snapshot in manifest.PreInstallKeyFiles)
            {
                if (string.IsNullOrWhiteSpace(snapshot.RelativePath))
                    continue;

                map[snapshot.RelativePath] = snapshot;
            }

            return map;
        }

        private static bool TryRestoreFromBackup(string gameDir, string backupDir, string relativePath, string? backupRelativePath)
        {
            try
            {
                var effectiveBackupRelative = string.IsNullOrWhiteSpace(backupRelativePath)
                    ? relativePath
                    : backupRelativePath;

                var backupPath = Path.Combine(backupDir, effectiveBackupRelative);
                if (!File.Exists(backupPath))
                    return false;

                var destinationPath = Path.Combine(gameDir, relativePath);
                var destinationDir = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrEmpty(destinationDir) && !Directory.Exists(destinationDir))
                    Directory.CreateDirectory(destinationDir);

                File.Copy(backupPath, destinationPath, overwrite: true);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryDeleteFileIfExists(string fullPath)
        {
            try
            {
                if (!File.Exists(fullPath))
                    return false;

                File.Delete(fullPath);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Determines the correct installation directory for games based on user rules.
        /// </summary>
        public string? DetermineInstallDirectory(Game game)
        {
            if (string.IsNullOrEmpty(game.InstallPath) || !Directory.Exists(game.InstallPath))
            {
                // If InstallPath is missing, try ExecutablePath
                if (!string.IsNullOrEmpty(game.ExecutablePath) && File.Exists(game.ExecutablePath))
                    return Path.GetDirectoryName(game.ExecutablePath);

                return null;
            }

            // Rule 1: Try to extract in the same folder as the main .exe, scan to find it.
            string[] allExes = Array.Empty<string>();
            try
            {
                allExes = Directory.GetFiles(game.InstallPath, "*.exe", SearchOption.AllDirectories);
            }
            catch (Exception ex)
            {
                Log.Write($"[Install] Could not enumerate executables in '{game.InstallPath}': {ex.Message}");
            }

            string? bestMatchDir = null;

            // Rule 2: Unreal Engine ships its real executables under "<Project>/Binaries/Win64".
            // When any exist, only they are candidates — an engine-mandated layout beats the
            // name/size/DLL heuristics below, which can be swayed by a launcher stub in the
            // root. This used to be hardcoded to the "Phoenix" project name; that is just one
            // instance of the same convention.
            var unrealExes = allExes.Where(IsUnrealShippingPath).ToArray();
            if (unrealExes.Length == 1)
                return Path.GetDirectoryName(unrealExes[0]);
            if (unrealExes.Length > 1)
                allExes = unrealExes;

            if (allExes.Length > 0)
            {
                // Try to match by name or context
                int bestScore = -1;
                string? bestExe = null;

                var gameNameLetters = new string(game.Name.Where(char.IsLetterOrDigit).ToArray());

                foreach (var exePath in allExes)
                {
                    var fileName = Path.GetFileNameWithoutExtension(exePath);

                    // Filter out known non-game executables
                    if (fileName.Contains("Crash", StringComparison.OrdinalIgnoreCase) ||
                        fileName.Contains("Redist", StringComparison.OrdinalIgnoreCase) ||
                        fileName.Contains("Setup", StringComparison.OrdinalIgnoreCase) ||
                        fileName.Contains("Launcher", StringComparison.OrdinalIgnoreCase) ||
                        fileName.Contains("UnrealCEFSubProcess", StringComparison.OrdinalIgnoreCase) ||
                        fileName.Contains("Prerequisites", StringComparison.OrdinalIgnoreCase))
                        continue;

                    int score = 0;
                    var exeLetters = new string(fileName.Where(char.IsLetterOrDigit).ToArray());

                    if (!string.IsNullOrEmpty(exeLetters) && !string.IsNullOrEmpty(gameNameLetters))
                    {
                        if (exeLetters.Contains(gameNameLetters, StringComparison.OrdinalIgnoreCase) ||
                            gameNameLetters.Contains(exeLetters, StringComparison.OrdinalIgnoreCase))
                        {
                            score += 15;
                        }
                    }

                    if (IsUnrealShippingPath(exePath))
                    {
                        score += 5;
                    }

                    try
                    {
                        // Main game executables are usually decently sized (> 5MB)
                        var fileInfo = new FileInfo(exePath);
                        if (fileInfo.Length > 5 * 1024 * 1024)
                        {
                            score += 10;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Write($"[Install] Could not read file info for '{exePath}': {ex.Message}");
                    }

                    var exeDir = Path.GetDirectoryName(exePath);
                    if (exeDir != null)
                    {
                        try
                        {
                            var dlls = Directory.GetFiles(exeDir, "*.dll", SearchOption.TopDirectoryOnly);
                            foreach (var dll in dlls)
                            {
                                var dllName = Path.GetFileName(dll).ToLowerInvariant();
                                if (dllName.Contains("amd") || dllName.Contains("fsr") || dllName.Contains("nvngx") || dllName.Contains("dlss") || dllName.Contains("sl.interposer") || dllName.Contains("xess"))
                                {
                                    score += 25; // High confidence if scaling DLLs are nearby
                                    break;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Write($"[Install] Could not enumerate DLLs in '{exeDir}': {ex.Message}");
                        }
                    }

                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestExe = exePath;
                    }
                }

                if (bestExe != null)
                {
                    bestMatchDir = Path.GetDirectoryName(bestExe);
                }

                // Fallback: If no match by name, check known ExecutablePath
                if (bestMatchDir == null)
                {
                    if (!string.IsNullOrEmpty(game.ExecutablePath) && File.Exists(game.ExecutablePath))
                    {
                        bestMatchDir = Path.GetDirectoryName(game.ExecutablePath);
                    }
                    else
                    {
                        var binariesExes = allExes.Where(IsUnrealShippingPath).ToList();
                        if (binariesExes.Count == 1)
                        {
                            bestMatchDir = Path.GetDirectoryName(binariesExes[0]);
                        }
                    }
                }
            }
            else if (allExes.Length == 0 && !string.IsNullOrEmpty(game.ExecutablePath) && File.Exists(game.ExecutablePath))
            {
                // Fallback if Directory.GetFiles fails but we have an ExecutablePath
                bestMatchDir = Path.GetDirectoryName(game.ExecutablePath);
            }

            if (bestMatchDir != null && Directory.Exists(bestMatchDir))
            {
                return bestMatchDir;
            }

            // Fallback to the main install path, if nothing else works
            return game.InstallPath;
        }


        /// <summary>
        /// Detects the correct installation directory fallback for older uninstalls.
        /// </summary>
        private string DetectCorrectInstallDirectory(string baseDir)
        {
            // Check for UE5 Phoenix structure: Phoenix/Binaries/Win64
            var phoenixPath = Path.Combine(baseDir, "Phoenix", "Binaries", "Win64");
            if (Directory.Exists(phoenixPath))
            {
                return phoenixPath;
            }

            // Check for generic UE structure: GameName/Binaries/Win64
            var binariesPath = Path.Combine(baseDir, "Binaries", "Win64");
            if (Directory.Exists(binariesPath))
            {
                return binariesPath;
            }

            // Return original path if no special structure detected
            return baseDir;
        }

        // ── Custom FSR 4.x amdxcffx64.dll (bring-your-own DLL) ────────────────────

        /// <summary>
        /// First OptiScaler version that loads amdxcffx64.dll from the game folder
        /// (before checking the driver store). Introduced in v0.7.7-pre9; the first
        /// stable release containing it is v0.7.8.
        /// </summary>
        public static readonly Version MinOptiScalerVersionForCustomFsr4 = new(0, 7, 8);

        /// <summary>
        /// Returns true when the given OptiScaler version string is known to support
        /// loading a custom amdxcffx64.dll from the game folder. Unparseable versions
        /// (e.g. user-imported custom builds) return true — we can't judge those.
        /// </summary>
        public static bool SupportsCustomFsr4Dll(string? optiscalerVersion)
        {
            if (string.IsNullOrWhiteSpace(optiscalerVersion))
                return true;

            var clean = new string(optiscalerVersion.TrimStart('v', 'V')
                .TakeWhile(c => char.IsDigit(c) || c == '.').ToArray()).TrimEnd('.');
            if (!Version.TryParse(clean, out var parsed))
                return true; // custom/unknown builds: don't block

            if (parsed >= MinOptiScalerVersionForCustomFsr4)
                return true;

            // 0.7.7 pre-releases from pre9 onward also support it
            if (parsed == new Version(0, 7, 7))
            {
                var match = System.Text.RegularExpressions.Regex.Match(
                    optiscalerVersion, @"pre[\s\-]?(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (match.Success && int.TryParse(match.Groups[1].Value, out var pre))
                    return pre >= 9;
            }

            return false;
        }

        /// <summary>
        /// Installs a user-imported custom FSR SDK package (amd_fidelityfx_upscaler_dx12.dll
        /// plus any companion DLLs imported with it, e.g. frame generation) into the game,
        /// replacing the copies shipped with the installed OptiScaler release. Same
        /// backup/manifest/ini handling as the amdxcffx64.dll component. Mutually
        /// exclusive with the downloadable "FSR4 INT8 Extras" component, which installs
        /// the same upscaler file (enforced in the UI).
        /// </summary>
        public void InstallCustomFsrSdk(Game game, string cacheDir, string versionLabel, string? overrideGameDir = null,
                                       IReadOnlyCollection<string>? injectNames = null)
        {
            if (!Directory.Exists(cacheDir))
                throw new DirectoryNotFoundException($"The imported FSR SDK package was not found in the local cache: {cacheDir}");

            // Eligible names: the canonical FSR swap set, plus any user-chosen custom
            // DLLs (injectNames) — those are deliberate bring-your-own additions (e.g.
            // amdxcffx64.dll) and may introduce new files. Anything else in the cache
            // dir (stray support libraries from older imports) is never installed.
            var eligible = new HashSet<string>(ComponentManagementService.FsrSdkDllNames, StringComparer.OrdinalIgnoreCase);
            var injectable = new HashSet<string>(injectNames ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            eligible.UnionWith(injectable);

            var packageFiles = Directory.GetFiles(cacheDir, "*.dll", SearchOption.TopDirectoryOnly)
                .Select(f => (name: Path.GetFileName(f), path: f))
                .Where(f => eligible.Contains(f.name))
                .ToList();
            if (packageFiles.Count == 0 || !packageFiles.Any(f => f.name.Equals("amd_fidelityfx_upscaler_dx12.dll", StringComparison.OrdinalIgnoreCase)))
                throw new FileNotFoundException("The imported FSR SDK package is missing amd_fidelityfx_upscaler_dx12.dll.");

            // Replicate OptiScaler's own architecture: OptiScaler's release ships a
            // coherent, working FSR DLL set next to the game exe. A custom SDK must only
            // SWAP those files IN PLACE with same-name equivalents — never introduce DLL
            // names OptiScaler didn't lay down (e.g. a second loader variant), which can
            // change which loader OptiScaler picks and silently drop FSR 4 support.
            var gameDirForFilter = overrideGameDir;
            if (string.IsNullOrEmpty(gameDirForFilter))
            {
                var manifest = _backupStore.LoadManifest(game.InstallPath);
                gameDirForFilter = !string.IsNullOrEmpty(manifest?.InstalledGameDirectory) && Directory.Exists(manifest!.InstalledGameDirectory)
                    ? manifest.InstalledGameDirectory
                    : DetermineInstallDirectory(game);
            }
            if (string.IsNullOrEmpty(gameDirForFilter) || !Directory.Exists(gameDirForFilter))
                throw new Exception("Could not determine the game directory for the custom SDK install.");

            var files = new List<(string name, string path)>();
            foreach (var f in packageFiles)
            {
                if (File.Exists(Path.Combine(gameDirForFilter, f.name)))
                {
                    files.Add(f);
                }
                else if (injectable.Contains(f.name))
                {
                    // User-chosen custom DLL that doesn't exist yet: deliberately ADD it
                    // (manifest-tracked, so Revert removes it). This is how amdxcffx64.dll
                    // works — OptiScaler loads it from the game folder.
                    Log.Write($"[CustomFsrSdk] Adding {f.name}: your custom DLL (not part of the base install).");
                    files.Add(f);
                }
                else
                {
                    Log.Write($"[CustomFsrSdk] Skipping {f.name}: not part of the existing OptiScaler install (in-place swap only).");
                }
            }

            // FFX loader equivalence: OptiScaler's release ships the SDK's
            // amd_fidelityfx_loader_dx12.dll RENAMED to amd_fidelityfx_dx12.dll (see
            // OptiScaler v0.9.3 packaging) — the two names are the same component.
            // When the package carries one name and the game folder has the other,
            // swap the existing file with the package's equivalent so the loader and
            // upscaler stay a matched pair. Without this, an SDK loader would be
            // skipped (or worse, a stale differently-named copy would keep shadowing
            // the fresh one — OptiScaler tries loader_dx12 before dx12).
            const string dx12Name = "amd_fidelityfx_dx12.dll";
            const string loaderName = "amd_fidelityfx_loader_dx12.dll";
            var pkgDx12 = packageFiles.FirstOrDefault(f => f.name.Equals(dx12Name, StringComparison.OrdinalIgnoreCase)).path;
            var pkgLoader = packageFiles.FirstOrDefault(f => f.name.Equals(loaderName, StringComparison.OrdinalIgnoreCase)).path;

            void MapLoaderEquivalent(string targetName, string? sourcePath, string sourceName)
            {
                if (sourcePath is null) return;
                if (!File.Exists(Path.Combine(gameDirForFilter!, targetName))) return;
                if (files.Any(f => f.name.Equals(targetName, StringComparison.OrdinalIgnoreCase))) return;
                Log.Write($"[CustomFsrSdk] Swapping {targetName} with the package's {sourceName} (same component, renamed by OptiScaler's packaging).");
                files.Add((targetName, sourcePath));
            }

            // Game has dx12 (OptiScaler's renamed loader), package only has loader_dx12:
            if (pkgDx12 is null) MapLoaderEquivalent(dx12Name, pkgLoader, loaderName);
            // Game has a stray loader_dx12, package only has dx12:
            if (pkgLoader is null) MapLoaderEquivalent(loaderName, pkgDx12, dx12Name);

            if (!files.Any(f => f.name.Equals("amd_fidelityfx_upscaler_dx12.dll", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException(
                    "No amd_fidelityfx_upscaler_dx12.dll found in the game folder to swap. Install OptiScaler first — the custom SDK only replaces OptiScaler's own FSR files in place.");

            InstallUserDllSet(game, files, versionLabel, overrideGameDir);
        }

        /// <summary>
        /// Copies one file into a game that already has a tracked OptiScaler install, and
        /// records it in the manifest so revert can undo it.
        ///
        /// The bookkeeping is the subtle part and is why every add-on shares this: never
        /// re-backup a file already protected by an original backup, and never mistake a
        /// file we placed earlier for a game original. <paramref name="previouslyOurs"/>
        /// covers the case where an OptiScaler update rebuilt the manifest and lost this
        /// component's records while its DLLs are still on disk.
        /// </summary>
        private void CopyFileIntoTrackedInstall(
            InstallationManifest manifest, string storeKey, string gameDir,
            string fileName, string sourcePath, bool previouslyOurs, string logTag)
        {
            var priorBackedUp = manifest.FilesOverwritten.Any(r => r.RelativePath.Equals(fileName, StringComparison.OrdinalIgnoreCase))
                             || manifest.BackedUpFiles.Contains(fileName, StringComparer.OrdinalIgnoreCase);
            var priorCreated = manifest.FilesCreated.Any(r => r.RelativePath.Equals(fileName, StringComparison.OrdinalIgnoreCase))
                            || previouslyOurs;
            var backupInStore = File.Exists(Path.Combine(_backupStore.GetFilesDir(storeKey), fileName));

            var destPath = Path.Combine(gameDir, fileName);
            var existedBefore = File.Exists(destPath);
            string? preHash = null;

            if (existedBefore && !priorBackedUp && !priorCreated)
            {
                preHash = ComputeSha256(destPath);
                _backupStore.BackupFile(storeKey, gameDir, fileName);
                manifest.BackedUpFiles.Add(fileName);
                Log.Write($"[{logTag}] Backed up existing {fileName}");
            }

            // An original backup surviving in the store (from a previous install cycle)
            // must keep its "overwritten" record so uninstall restores it.
            var treatAsOriginal = (existedBefore && !priorCreated) || priorBackedUp || (priorCreated && backupInStore);
            if (treatAsOriginal && !manifest.BackedUpFiles.Contains(fileName, StringComparer.OrdinalIgnoreCase) && backupInStore)
                manifest.BackedUpFiles.Add(fileName);

            File.Copy(sourcePath, destPath, overwrite: true);
            if (!manifest.InstalledFiles.Contains(fileName, StringComparer.OrdinalIgnoreCase))
                manifest.InstalledFiles.Add(fileName);
            TrackManifestFileMutation(
                manifest,
                relativePath: fileName,
                existedBefore: treatAsOriginal,
                preInstallHash: preHash,
                postInstallHash: ComputeSha256(destPath));
        }

        /// <summary>
        /// Installs a single add-on file into a game that already has OptiScaler, tracked
        /// in the manifest. Used by add-ons that run *after* the main install has been
        /// committed, so they have to reopen the manifest and save it again.
        ///
        /// <paramref name="markManifest"/> records which component this file belongs to.
        /// </summary>
        public void InstallTrackedFile(
            Game game, string sourcePath, string fileName, string gameDir,
            Action<InstallationManifest>? markManifest = null, string logTag = "AddOn")
        {
            if (!File.Exists(sourcePath))
                throw new FileNotFoundException($"The file to install was not found: {fileName}", sourcePath);

            var storeKey = game.InstallPath;
            var manifest = _backupStore.LoadManifest(storeKey);
            if (manifest == null)
            {
                // No tracked install to attach to: place the file anyway rather than
                // failing the install, and let the known-artifact list cover revert.
                File.Copy(sourcePath, Path.Combine(gameDir, fileName), overwrite: true);
                Log.Write($"[{logTag}] Installed {fileName} without a manifest to record it in.");
                return;
            }

            CopyFileIntoTrackedInstall(manifest, storeKey, gameDir, fileName, sourcePath,
                previouslyOurs: !string.IsNullOrEmpty(game.Fsr4ExtraVersion), logTag);

            markManifest?.Invoke(manifest);
            if (!manifest.ExpectedFinalMarkers.Contains(fileName, StringComparer.OrdinalIgnoreCase))
                manifest.ExpectedFinalMarkers.Add(fileName);
            _backupStore.SaveManifest(storeKey, manifest);
        }

        /// <summary>
        /// Install core for the bring-your-own-DLL overlay: the imported files replace
        /// the copies the installed OptiScaler release shipped, with the originals
        /// backed up so a revert puts them back.
        /// </summary>
        private void InstallUserDllSet(Game game, List<(string name, string path)> files, string versionLabel, string? overrideGameDir)
        {
            const string logTag = "CustomFsrSdk";

            foreach (var (name, path) in files)
                if (!File.Exists(path))
                    throw new FileNotFoundException($"The imported {name} was not found in the local cache.", path);

            var storeKey = game.InstallPath;
            var manifest = _backupStore.LoadManifest(storeKey);
            if (manifest == null || !string.Equals(manifest.OperationStatus, "committed", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("OptiScaler must be installed for this game before adding this custom DLL.");

            var gameDir = overrideGameDir
                ?? (!string.IsNullOrEmpty(manifest.InstalledGameDirectory) && Directory.Exists(manifest.InstalledGameDirectory)
                    ? manifest.InstalledGameDirectory
                    : DetermineInstallDirectory(game));

            if (string.IsNullOrEmpty(gameDir) || !Directory.Exists(gameDir))
                throw new Exception("Could not determine the game directory for the custom DLL install.");

            Log.Write($"[{logTag}] Installing v{versionLabel} ({files.Count} file(s)) into {gameDir}");

            // Mirror the backup rules of the main install: never re-backup a file that is
            // already protected by an original backup, and never treat a file we installed
            // ourselves as a game original. The per-game version field covers OptiScaler
            // updates, where the manifest is rebuilt and loses this component's records
            // while the DLLs we placed earlier are still on disk. For the SDK set, an
            // installed FSR4 INT8 Extras version also means the upscaler file is not a
            // game original (Extras installs the same file).
            var previouslyOurs = !string.IsNullOrEmpty(game.CustomFsrSdkVersion)
                                 || !string.IsNullOrEmpty(game.Fsr4ExtraVersion);

            foreach (var (dllName, sourcePath) in files)
                CopyFileIntoTrackedInstall(manifest, storeKey, gameDir, dllName, sourcePath, previouslyOurs, logTag);

            // Engage FSR4: Fsr4Update lets OptiScaler upgrade FSR 3.x to FSR 4, and
            // [Upscalers] selects the FSR upscaler in the first place — without it the
            // DX12 default is XeSS and none of the FSR keys are even read.
            ModifyOptiScalerIniKey(gameDir, "FSR", "Fsr4Update", "true");
            SelectFsr4Upscaler(gameDir, true);

            manifest.IncludesCustomFsrSdk = true;
            manifest.CustomFsrSdkVersion = versionLabel;
            foreach (var (dllName, _) in files)
                if (!manifest.ExpectedFinalMarkers.Contains(dllName, StringComparer.OrdinalIgnoreCase))
                    manifest.ExpectedFinalMarkers.Add(dllName);
            _backupStore.SaveManifest(storeKey, manifest);

            game.CustomFsrSdkVersion = versionLabel;
            // The custom SDK replaces whatever the Extras component installed.
            game.Fsr4ExtraVersion = null;

            Log.Write($"[{logTag}] Installed v{versionLabel} and updated OptiScaler.ini " +
                      "([FSR] Fsr4Update=true, and [Upscalers] set to the FSR upscaler)");
        }

        /// <summary>
        /// Sets a key inside an arbitrary [section] of OptiScaler.ini, creating the
        /// file, the section, or the key as needed. Unlike ModifyOptiScalerIni (which
        /// only handles [General]), this is section-aware.
        /// </summary>
        /// <summary>
        /// The <c>[Upscalers]</c> codes that select the FSR 3.1/4 upscaler for each API.
        /// </summary>
        public sealed record Fsr4UpscalerCodes(string Dx12, string Dx11, string Vulkan);

        // What each naming era calls the FSR 3.1/4 upscaler. Older OptiScaler releases
        // use "fsr31"/"fsr31_12"; newer ones renamed these to "ffx"/"ffx_12".
        private static readonly Fsr4UpscalerCodes LegacyCodes = new("fsr31", "fsr31_12", "fsr31_12");
        private static readonly Fsr4UpscalerCodes ModernCodes = new("ffx", "ffx_12", "ffx_12");

        /// <summary>
        /// Works out which <c>[Upscalers]</c> codes this OptiScaler release understands.
        ///
        /// The names changed between releases: what used to be <c>fsr31</c> is now
        /// <c>ffx</c>. This matters more than a cosmetic rename — on a release that
        /// expects <c>ffx</c>, the value <c>fsr31</c> is not a DX12 option at all and
        /// falls through to FSR 2.1.2, so guessing would silently downgrade the game.
        ///
        /// Every release documents its own valid codes in the comments above these keys,
        /// so we read them from the ini that shipped with it instead of mapping version
        /// numbers. Returns null when that cannot be determined, so callers can leave the
        /// keys alone rather than risk writing one this build does not accept.
        /// </summary>
        public static Fsr4UpscalerCodes? DetectFsr4UpscalerCodes(string gameDir)
        {
            var iniPath = Path.Combine(gameDir, "OptiScaler.ini");
            if (!File.Exists(iniPath)) return null;

            try
            {
                var documented = new StringBuilder();
                var inSection = false;

                foreach (var raw in File.ReadAllLines(iniPath))
                {
                    var line = raw.Trim();
                    if (line.StartsWith("["))
                    {
                        // Only the [Upscalers] comments describe these codes; "ffx"
                        // appears elsewhere in the file for unrelated settings.
                        inSection = line.Equals("[Upscalers]", StringComparison.OrdinalIgnoreCase);
                        continue;
                    }
                    if (inSection && line.StartsWith(";"))
                        documented.Append(line).Append('\n');
                }

                var text = documented.ToString();
                if (Regex.IsMatch(text, @"\bffx(_12)?\b", RegexOptions.IgnoreCase)) return ModernCodes;
                if (Regex.IsMatch(text, @"\bfsr31(_12)?\b", RegexOptions.IgnoreCase)) return LegacyCodes;
            }
            catch (Exception ex)
            {
                Log.Write($"[FSR4] Could not read the upscaler codes from OptiScaler.ini: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Selects (or stops forcing) the FSR 3.1/4 upscaler in <c>[Upscalers]</c>.
        ///
        /// This is the key that actually decides which upscaler runs. Without it the
        /// DX12 default is XeSS, and every FSR setting is then configuring an upscaler
        /// that is not running — which is exactly what made "select FSR 4 for me" look
        /// like it did nothing.
        /// </summary>
        public static void SelectFsr4Upscaler(string gameDir, bool select)
        {
            if (!select)
            {
                // Hand the choice back to OptiScaler rather than leaving a stale value
                // behind from an earlier install.
                ModifyOptiScalerIniKey(gameDir, "Upscalers", "Dx12Upscaler", "auto");
                ModifyOptiScalerIniKey(gameDir, "Upscalers", "Dx11Upscaler", "auto");
                ModifyOptiScalerIniKey(gameDir, "Upscalers", "VulkanUpscaler", "auto");
                return;
            }

            var codes = DetectFsr4UpscalerCodes(gameDir);
            if (codes is null)
            {
                Log.Write("[FSR4] OptiScaler.ini does not document its upscaler codes — leaving " +
                          "[Upscalers] alone so an unsupported value cannot downgrade the upscaler.");
                return;
            }

            ModifyOptiScalerIniKey(gameDir, "Upscalers", "Dx12Upscaler", codes.Dx12);
            ModifyOptiScalerIniKey(gameDir, "Upscalers", "Dx11Upscaler", codes.Dx11);
            ModifyOptiScalerIniKey(gameDir, "Upscalers", "VulkanUpscaler", codes.Vulkan);
            Log.Write($"[FSR4] Selected the FSR 4 upscaler ([Upscalers] Dx12Upscaler={codes.Dx12}).");
        }

        /// <summary>
        /// Whether an executable sits under Unreal Engine's shipping layout,
        /// <c>&lt;Project&gt;/Binaries/Win64</c>.
        ///
        /// Compares path segments rather than matching the literal string
        /// "Binaries\\Win64": enumeration returns '\\' on Windows but '/' on Linux, so a
        /// substring check never matches on our primary platform — the heuristics that
        /// relied on it were silently dead there.
        /// </summary>
        private static bool IsUnrealShippingPath(string exePath)
        {
            // Split on both separators rather than using Path: a path can arrive in
            // Windows form even while running on Linux (game manifests inside a Proton
            // prefix), and Path.GetDirectoryName does not treat '\\' as a separator there.
            var parts = exePath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 3
                && parts[^2].Equals("Win64", StringComparison.OrdinalIgnoreCase)
                && parts[^3].Equals("Binaries", StringComparison.OrdinalIgnoreCase);
        }

        public static void ModifyOptiScalerIniKey(string gameDir, string section, string key, string value)
        {
            var iniPath = Path.Combine(gameDir, "OptiScaler.ini");
            var sectionHeader = $"[{section}]";

            if (!File.Exists(iniPath))
            {
                File.WriteAllText(iniPath, $"{sectionHeader}\n{key}={value}\n");
                return;
            }

            try
            {
                var lines = File.ReadAllLines(iniPath).ToList();
                int sectionStart = -1;
                int sectionEnd = lines.Count; // exclusive

                for (int i = 0; i < lines.Count; i++)
                {
                    var line = lines[i].Trim();
                    if (!line.StartsWith("[")) continue;

                    if (sectionStart < 0)
                    {
                        if (line.Equals(sectionHeader, StringComparison.OrdinalIgnoreCase))
                            sectionStart = i;
                    }
                    else
                    {
                        sectionEnd = i;
                        break;
                    }
                }

                if (sectionStart < 0)
                {
                    // Section missing — append it at the end.
                    if (lines.Count > 0 && lines[^1].Trim().Length > 0)
                        lines.Add(string.Empty);
                    lines.Add(sectionHeader);
                    lines.Add($"{key}={value}");
                }
                else
                {
                    bool keyFound = false;
                    for (int i = sectionStart + 1; i < sectionEnd; i++)
                    {
                        var trimmed = lines[i].TrimStart();
                        if (trimmed.StartsWith(";") || trimmed.StartsWith("#")) continue;
                        var eq = trimmed.IndexOf('=');
                        if (eq <= 0) continue;
                        if (trimmed[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                        {
                            lines[i] = $"{key}={value}";
                            keyFound = true;
                            break;
                        }
                    }

                    if (!keyFound)
                        lines.Insert(sectionStart + 1, $"{key}={value}");
                }

                File.WriteAllLines(iniPath, lines);
            }
            catch (Exception ex)
            {
                Log.Write($"[Ini] Failed to set [{section}] {key}={value}: {ex.Message}");
            }
        }

        /// <summary>
        /// Modifies a setting in OptiScaler.ini
        /// </summary>
        private void ModifyOptiScalerIni(string gameDir, string key, string value)
        {
            var iniPath = Path.Combine(gameDir, "OptiScaler.ini");

            if (!File.Exists(iniPath))
            {
                // Create a basic ini file if it doesn't exist
                File.WriteAllText(iniPath, $"[General]\n{key}={value}\n");
                return;
            }

            try
            {
                var lines = File.ReadAllLines(iniPath).ToList();
                bool keyFound = false;
                bool inGeneralSection = false;

                for (int i = 0; i < lines.Count; i++)
                {
                    var line = lines[i].Trim();

                    // Check if we're in [General] section
                    if (line.Equals("[General]", StringComparison.OrdinalIgnoreCase))
                    {
                        inGeneralSection = true;
                        continue;
                    }

                    // Check if we've moved to another section
                    if (line.StartsWith("[") && !line.Equals("[General]", StringComparison.OrdinalIgnoreCase))
                    {
                        if (inGeneralSection && !keyFound)
                        {
                            // Insert the key before the next section
                            lines.Insert(i, $"{key}={value}");
                            keyFound = true;
                            break;
                        }
                        inGeneralSection = false;
                    }

                    // If we're in General section and found the key, update it
                    if (inGeneralSection && line.StartsWith($"{key}=", StringComparison.OrdinalIgnoreCase))
                    {
                        lines[i] = $"{key}={value}";
                        keyFound = true;
                        break;
                    }
                }

                // If key wasn't found, add it to the end of [General] section or create it
                if (!keyFound)
                {
                    if (inGeneralSection)
                    {
                        lines.Add($"{key}={value}");
                    }
                    else
                    {
                        // Add [General] section if it doesn't exist
                        lines.Add("[General]");
                        lines.Add($"{key}={value}");
                    }
                }

                File.WriteAllLines(iniPath, lines);
            }
            catch (Exception ex)
            {
                Log.Write($"[Install] Failed to modify OptiScaler.ini, creating new: {ex.Message}");
                File.WriteAllText(iniPath, $"[General]\n{key}={value}\n");
            }
        }
    }
}
