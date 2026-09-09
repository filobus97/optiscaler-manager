<#
    OptiScaler Manager - in-place updater for Windows.
    GPL-3.0-or-later. See repository LICENSE.

    Updates the installed OptiScaler Manager to the latest GitHub release, in
    place, WITHOUT touching your data: all settings, imported DLLs, .ini profiles,
    backups and the download cache live under %APPDATA%\OptiscalerManager, not in
    the install folder — so replacing the program here leaves them intact.

    Usage:
        powershell -ExecutionPolicy Bypass -File update.ps1 [-Force] [-Dir <install-dir>]
                                                            [-WaitPid <pid>] [-Relaunch]

      -Force          reinstall even if already on the latest version
      -Dir <path>     install dir to update (default: the folder this script is in)
      -WaitPid <pid>  wait (up to 60s) for that process to exit before updating —
                      used by the app's in-app "Update now" button
      -Relaunch       start the app again when the updater finishes (on ANY outcome,
                      so a failed download still brings the app back)

    The app must be closed while updating (a running .exe is locked by Windows);
    without -WaitPid the script will offer to close it for you.
#>
[CmdletBinding()]
param(
    [switch]$Force,
    [string]$Dir,
    [int]$WaitPid = 0,
    [switch]$Relaunch
)

$ErrorActionPreference = 'Stop'
$Repo = 'filobus97/optiscaler-manager'

# Fallback only. The executable is discovered from the payload rather than assumed, so
# renaming the application does not strand users: the updater shipped with the *old*
# release performs the renaming update, and a hardcoded name would relaunch the very
# version it just replaced.
$LegacyExeName = 'OptiscalerManager.exe'
$script:ExeName = $LegacyExeName

# The application executable in a folder: the one .exe that is not something we ship
# beside it.
function Find-AppExe([string]$Folder) {
    if (-not (Test-Path $Folder)) { return $null }
    $candidate = Get-ChildItem -Path $Folder -Filter *.exe -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -notmatch '^(update|unins)' } |
        Select-Object -First 1
    if ($candidate) { return $candidate.Name }
    return $null
}

# Install dir = -Dir, else this script's folder. Resolved first so the relaunch
# in the outer finally always knows where the exe lives.
if (-not $Dir) { $Dir = Split-Path -Parent $PSCommandPath }

# The body never calls `exit`: it throws on error and returns when done, so the
# outer finally (relaunch) is guaranteed to run on every outcome.
function Invoke-Update {
    if (-not (Test-Path $Dir)) { throw "Install dir not found: $Dir" }

    # In-app flow: the app spawns us and then quits — wait for it to be gone
    # (its .exe is locked while running).
    if ($WaitPid -gt 0) {
        Write-Host "Waiting for the app (pid $WaitPid) to exit..."
        try {
            Wait-Process -Id $WaitPid -Timeout 60 -ErrorAction Stop
        } catch [Microsoft.PowerShell.Commands.ProcessCommandException] {
            # Already gone — fine.
        } catch {
            throw "Process $WaitPid is still running after 60s; aborting update."
        }
        Write-Host 'App closed.'
    }

    # Only win-x64 is published; on Windows-on-ARM it runs under emulation.
    $Rid = 'win-x64'
    Write-Host "Platform: $Rid"
    Write-Host "Install dir: $Dir"

    Write-Host 'Checking the latest release...'
    $headers = @{ 'User-Agent' = 'OptiscalerManager-Updater'; 'Accept' = 'application/vnd.github+json' }
    try {
        $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases/latest" -Headers $headers
    } catch {
        throw "Could not reach GitHub. Check your connection (or GitHub rate limits) and retry. $_"
    }

    $latest = $release.tag_name
    if (-not $latest) { throw 'Could not determine the latest version from GitHub.' }
    $latestNum = $latest -replace '^v', ''
    Write-Host "Latest release: $latest"

    $verFile = Join-Path $Dir 'VERSION'
    $current = if (Test-Path $verFile) { (Get-Content $verFile -Raw).Trim() } else { '' }
    if ($current) { Write-Host "Installed version: $current" }

    if (-not $Force -and $current -and ($current -eq $latestNum)) {
        Write-Host "Already up to date ($current). Use -Force to reinstall." -ForegroundColor Green
        return
    }

    $asset = $release.assets | Where-Object { $_.name -like "*-$Rid.zip" } | Select-Object -First 1
    if (-not $asset) { throw "No asset for $Rid in release $latest (expected an asset ending in -$Rid.zip)." }

    # If the app is (still) running, its .exe is locked. Interactive flow only —
    # the in-app flow already waited above.
    $installedExe = Find-AppExe $Dir
    if ($installedExe) { $script:ExeName = $installedExe }
    $procName = [IO.Path]::GetFileNameWithoutExtension($script:ExeName)
    $running = Get-Process -Name $procName -ErrorAction SilentlyContinue
    if ($running -and $WaitPid -eq 0) {
        $answer = Read-Host "OptiScaler Manager is running and must be closed to update. Close it now? [y/N]"
        if ($answer -match '^(y|yes)$') {
            $running | Stop-Process -Force
            Start-Sleep -Seconds 1
        } else {
            throw 'Please close OptiScaler Manager and run the updater again.'
        }
    }

    $tmp = Join-Path ([IO.Path]::GetTempPath()) ("osm-" + [guid]::NewGuid())
    New-Item -ItemType Directory -Path $tmp | Out-Null
    try {
        $zip = Join-Path $tmp 'pkg.zip'
        Write-Host "Downloading $($asset.browser_download_url)"
        Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zip -Headers @{ 'User-Agent' = 'OptiscalerManager-Updater' }

        Write-Host 'Extracting...'
        $extract = Join-Path $tmp 'extract'
        Expand-Archive -Path $zip -DestinationPath $extract -Force

        Write-Host "Installing to $Dir ..."
        # Resolve both names before copying: if the application was renamed between
        # these releases the old .exe must go, or it lingers beside the new one and
        # anything pointing at the old name keeps starting the previous version.
        $oldExe = Find-AppExe $Dir
        $newExe = Find-AppExe $extract
        if ($newExe) { $script:ExeName = $newExe } elseif ($oldExe) { $script:ExeName = $oldExe }

        # Copy the new payload over the install dir. User data lives in the app-data
        # folder, so this only replaces the program itself, its bundled config.json
        # template and these scripts.
        Copy-Item -Path (Join-Path $extract '*') -Destination $Dir -Recurse -Force

        if ($oldExe -and $newExe -and ($oldExe -ne $newExe)) {
            Write-Host "The application was renamed: $oldExe -> $newExe"
            Remove-Item -Path (Join-Path $Dir $oldExe) -Force -ErrorAction SilentlyContinue
        }

        Write-Host "Done. Updated to $latest. Your settings, profiles, DLLs and backups were untouched." -ForegroundColor Green
    }
    finally {
        Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
    }
}

$exitCode = 0
try {
    Invoke-Update
}
catch {
    Write-Host "Error: $_" -ForegroundColor Red
    $exitCode = 1
}
finally {
    # In-app flow: bring the app back regardless of how the update went, so the
    # user is never left with nothing after "Update now".
    if ($Relaunch) {
        $launch = Find-AppExe $Dir
        if (-not $launch) { $launch = $script:ExeName }
        $exe = Join-Path $Dir $launch
        if (Test-Path $exe) {
            Write-Host "Relaunching $launch ..."
            Start-Process -FilePath $exe -WorkingDirectory $Dir
        }
    }
}
exit $exitCode
