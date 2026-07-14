<#
    OptiScaler Manager - in-place updater for Windows.
    GPL-3.0-or-later. See repository LICENSE.

    Updates the installed OptiScaler Manager to the latest GitHub release, in
    place, WITHOUT touching your data: all settings, imported DLLs, .ini profiles,
    backups and the download cache live under %APPDATA%\OptiscalerManager, not in
    the install folder — so replacing the program here leaves them intact.

    Usage:
        powershell -ExecutionPolicy Bypass -File update.ps1 [-Force] [-Dir <install-dir>]

      -Force          reinstall even if already on the latest version
      -Dir <path>     install dir to update (default: the folder this script is in)

    The app must be closed while updating (a running .exe is locked by Windows);
    the script will offer to close it for you.
#>
[CmdletBinding()]
param(
    [switch]$Force,
    [string]$Dir
)

$ErrorActionPreference = 'Stop'
$Repo = 'filobus97/optiscaler-manager'
$AssetPrefix = 'OptiscalerManager'
$ExeName = 'OptiscalerManager.exe'

function Fail($msg) { Write-Host "Error: $msg" -ForegroundColor Red; exit 1 }

# Install dir = -Dir, else this script's folder.
if (-not $Dir) { $Dir = Split-Path -Parent $PSCommandPath }
if (-not (Test-Path $Dir)) { Fail "Install dir not found: $Dir" }

# Only win-x64 is published; on Windows-on-ARM it runs under emulation.
$Rid = 'win-x64'
Write-Host "Platform: $Rid"
Write-Host "Install dir: $Dir"

Write-Host 'Checking the latest release...'
$headers = @{ 'User-Agent' = 'OptiscalerManager-Updater'; 'Accept' = 'application/vnd.github+json' }
try {
    $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases/latest" -Headers $headers
} catch {
    Fail "Could not reach GitHub. Check your connection (or GitHub rate limits) and retry. $_"
}

$latest = $release.tag_name
if (-not $latest) { Fail 'Could not determine the latest version from GitHub.' }
$latestNum = $latest -replace '^v', ''
Write-Host "Latest release: $latest"

$verFile = Join-Path $Dir 'VERSION'
$current = if (Test-Path $verFile) { (Get-Content $verFile -Raw).Trim() } else { '' }
if ($current) { Write-Host "Installed version: $current" }

if (-not $Force -and $current -and ($current -eq $latestNum)) {
    Write-Host "Already up to date ($current). Use -Force to reinstall." -ForegroundColor Green
    exit 0
}

$asset = $release.assets | Where-Object { $_.name -like "*-$Rid.zip" } | Select-Object -First 1
if (-not $asset) { Fail "No asset for $Rid in release $latest (expected $AssetPrefix-$latestNum-$Rid.zip)." }

# If the app is running, its .exe is locked. Offer to stop it.
$procName = [IO.Path]::GetFileNameWithoutExtension($ExeName)
$running = Get-Process -Name $procName -ErrorAction SilentlyContinue
if ($running) {
    $answer = Read-Host "OptiScaler Manager is running and must be closed to update. Close it now? [y/N]"
    if ($answer -match '^(y|yes)$') {
        $running | Stop-Process -Force
        Start-Sleep -Seconds 1
    } else {
        Fail 'Please close OptiScaler Manager and run the updater again.'
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
    # Copy the new payload over the install dir. User data lives in
    # %APPDATA%\OptiscalerManager, so this only replaces the program itself,
    # its bundled config.json template and these scripts.
    Copy-Item -Path (Join-Path $extract '*') -Destination $Dir -Recurse -Force

    Write-Host "Done. Updated to $latest. Your settings, profiles, DLLs and backups were untouched." -ForegroundColor Green
}
finally {
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}
