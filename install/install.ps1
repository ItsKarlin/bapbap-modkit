<#
.SYNOPSIS
    Installs the BAPBAP mod manager and Third Person mod.

.DESCRIPTION
    Finds your BAPBAP install, checks MelonLoader is present, then downloads the mod DLLs
    and verifies each one against the sha256 in the repo manifest before writing it.

    Nothing is installed unless the hash matches.

.EXAMPLE
    irm https://raw.githubusercontent.com/ItsKarlin/bapbap-modkit/main/install/install.ps1 | iex

.EXAMPLE
    .\install.ps1 -GameDir "D:\Games\SteamLibrary\steamapps\common\BAPBAP"
#>
[CmdletBinding()]
param(
    # Path to the BAPBAP folder. Auto-detected from Steam when omitted.
    [string]$GameDir,

    # Branch or tag to install from.
    [string]$Ref = "main",

    # Overwrite existing DLLs without asking.
    [switch]$Force,

    # Install MelonLoader without asking, if it is missing.
    [switch]$InstallLoader
)

$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$Repo    = "ItsKarlin/bapbap-modkit"
$BaseUrl = "https://raw.githubusercontent.com/$Repo/$Ref"

# MelonLoader is pinned to the CI build the BAPHub launcher uses. The public 0.7.3 release is
# NOT interchangeable: BAPHub's mods are built against this one.
$MlVersion = "0.7.2-ci.2388"
$MlUrl     = "https://raw.githubusercontent.com/Sonic0810/BAPBAPLauncher/main/melonloader/$MlVersion/MelonLoader.Windows.x64.CI.Release.zip"
$MlSha256  = "2c3bf21c06dd6248f47514be4468abd56f7aaa3800ea3b6aedad0a64eb4366a8"

function Write-Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "    $msg" -ForegroundColor Green }
function Write-Warn($msg) { Write-Host "    $msg" -ForegroundColor Yellow }

# ---------------------------------------------------------------- find the game

function Find-BapbapDir {
    $appId = "2226280"

    $steamRoot = $null
    foreach ($key in @("HKCU:\Software\Valve\Steam", "HKLM:\SOFTWARE\WOW6432Node\Valve\Steam")) {
        try {
            $p = (Get-ItemProperty -Path $key -ErrorAction Stop).SteamPath
            if ($p) { $steamRoot = $p.Replace("/", "\"); break }
        } catch { }
    }
    if (-not $steamRoot) { return $null }

    # Every library folder Steam knows about, not just the default one.
    $libraries = @($steamRoot)
    $vdf = Join-Path $steamRoot "steamapps\libraryfolders.vdf"
    if (Test-Path $vdf) {
        foreach ($line in Get-Content $vdf) {
            if ($line -match '"path"\s+"(.+?)"') {
                $libraries += $Matches[1].Replace("\\", "\")
            }
        }
    }

    foreach ($lib in $libraries) {
        # Trust the manifest over a guessed folder name: the install dir can be renamed.
        $manifest = Join-Path $lib "steamapps\appmanifest_$appId.acf"
        if (Test-Path $manifest) {
            foreach ($line in Get-Content $manifest) {
                if ($line -match '"installdir"\s+"(.+?)"') {
                    $candidate = Join-Path $lib "steamapps\common\$($Matches[1])"
                    if (Test-Path $candidate) { return $candidate }
                }
            }
        }
        $candidate = Join-Path $lib "steamapps\common\BAPBAP"
        if (Test-Path $candidate) { return $candidate }
    }
    return $null
}

Write-Step "Locating BAPBAP"
if (-not $GameDir) { $GameDir = Find-BapbapDir }

if (-not $GameDir -or -not (Test-Path $GameDir)) {
    Write-Host ""
    Write-Host "Could not find your BAPBAP install." -ForegroundColor Red
    Write-Host "Right-click BAPBAP in Steam -> Manage -> Browse local files, then re-run with:"
    Write-Host '    .\install.ps1 -GameDir "C:\path\to\BAPBAP"'
    exit 1
}
Write-Ok $GameDir

# ---------------------------------------------------------------- MelonLoader

function Install-MelonLoader($gameDir) {
    $zip = Join-Path ([IO.Path]::GetTempPath()) "MelonLoader-$MlVersion.zip"
    try {
        Write-Host "    downloading $MlVersion (18 MB)..."
        Invoke-WebRequest -Uri $MlUrl -OutFile $zip -UseBasicParsing

        $actual = (Get-FileHash -Path $zip -Algorithm SHA256).Hash.ToLower()
        if ($actual -ne $MlSha256) {
            Write-Host "    sha256 MISMATCH on the MelonLoader download - refusing to extract" -ForegroundColor Red
            Write-Host "      expected $MlSha256"
            Write-Host "      got      $actual"
            exit 1
        }
        Write-Ok "sha256 verified"

        # The archive is laid out to drop straight into the game folder: MelonLoader\ + version.dll
        Expand-Archive -Path $zip -DestinationPath $gameDir -Force
        Write-Ok "MelonLoader $MlVersion installed"
    } finally {
        Remove-Item -Force $zip -ErrorAction SilentlyContinue
    }
}

Write-Step "Checking MelonLoader"
$hasLoader = (Test-Path (Join-Path $GameDir "MelonLoader")) -and (Test-Path (Join-Path $GameDir "version.dll"))

if ($hasLoader) {
    # The loader writes its version into the log on every run, which is the cheapest way to see
    # what is actually installed.
    $log = Join-Path $GameDir "MelonLoader\Latest.log"
    $installed = $null
    if (Test-Path $log) {
        $line = Select-String -Path $log -Pattern 'MelonLoader v(\S+)' -List -ErrorAction SilentlyContinue
        if ($line) { $installed = $line.Matches[0].Groups[1].Value }
    }

    if ($installed -and $installed -ne $MlVersion) {
        Write-Warn "found v$installed, but BAPHub's mods are built against $MlVersion"
        Write-Warn "leaving it alone - replace it yourself if BAPHub mods misbehave"
    } elseif ($installed) {
        Write-Ok "v$installed"
    } else {
        Write-Ok "found (version unknown - the game has not been launched with it yet)"
    }
} else {
    Write-Warn "not installed"
    Write-Host "    MelonLoader $MlVersion is required. This is the build the BAPHub launcher"
    Write-Host "    pins, not the public 0.7.3 release, and it will be verified by sha256."

    if ($InstallLoader) {
        Install-MelonLoader $GameDir
    } else {
        $answer = Read-Host "    Download and install it now? [Y/n]"
        if ($answer -match '^[Nn]') {
            Write-Host ""
            Write-Host "Nothing installed. Get MelonLoader yourself and re-run this script."
            exit 1
        }
        Install-MelonLoader $GameDir
    }
}

$modsDir = Join-Path $GameDir "Mods"
New-Item -ItemType Directory -Force -Path $modsDir | Out-Null

# ---------------------------------------------------------------- fetch + verify

Write-Step "Fetching manifest"
try {
    $manifest = Invoke-RestMethod -Uri "$BaseUrl/dist/manifest.json" -UseBasicParsing
} catch {
    Write-Host "Could not download the manifest: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
Write-Ok "$($manifest.mods.Count) mods listed"

$staging = Join-Path ([IO.Path]::GetTempPath()) ("bapbap-modkit-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $staging | Out-Null

$verified = @()
try {
    foreach ($mod in $manifest.mods) {
        Write-Step "$($mod.id) v$($mod.version)"

        $tmp = Join-Path $staging $mod.file
        Invoke-WebRequest -Uri "$BaseUrl/dist/$($mod.file)" -OutFile $tmp -UseBasicParsing

        $actual = (Get-FileHash -Path $tmp -Algorithm SHA256).Hash.ToLower()
        if ($actual -ne $mod.sha256.ToLower()) {
            Write-Host "    sha256 MISMATCH - refusing to install" -ForegroundColor Red
            Write-Host "      expected $($mod.sha256)"
            Write-Host "      got      $actual"
            exit 1
        }
        Write-Ok "sha256 verified"
        $verified += [pscustomobject]@{ Mod = $mod; Path = $tmp }
    }

    # Only start touching Mods/ once every download has passed verification.
    foreach ($item in $verified) {
        $dest = Join-Path $modsDir $item.Mod.file

        if ((Test-Path $dest) -and -not $Force) {
            $existing = (Get-FileHash -Path $dest -Algorithm SHA256).Hash.ToLower()
            if ($existing -eq $item.Mod.sha256.ToLower()) {
                Write-Warn "$($item.Mod.file) already up to date"
                continue
            }
            $answer = Read-Host "    $($item.Mod.file) exists and differs. Replace it? [y/N]"
            if ($answer -notmatch '^[Yy]') { Write-Warn "skipped"; continue }
        }

        Copy-Item -Path $item.Path -Destination $dest -Force
        Write-Ok "installed $($item.Mod.file)"
    }
} finally {
    Remove-Item -Recurse -Force $staging -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "Done. Launch BAPBAP and press F5, or click MODS in the top nav bar." -ForegroundColor Green
Write-Host "Mods are only loaded at startup, so restart the game if it is already running."
