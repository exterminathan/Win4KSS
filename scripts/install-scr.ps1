<#
.SYNOPSIS
    Builds, installs and registers Win4KSS as a Windows screen saver.

.DESCRIPTION
    Publishes a single-file build, copies the .scr into System32 (which requires
    elevation, and is the only place Windows looks when populating the screen saver
    dropdown), and creates the walk-away shortcut with its hotkey.

    The shortcut points at /now rather than /s on purpose. /now asks Windows to start the
    configured screen saver, which is the only route that honours "On resume, display logon
    screen"; running the .scr directly with /s shows the visuals but never locks.

    Migration: the app used to be called Win4KAerial. Installing removes the old
    Win4KAerial.scr / Win4KAerial.exe (the user-scope dir, and System32 when elevated) and,
    if HKCU SCRNSAVE.EXE still points at the old .scr, repoints it at the new one, keeping
    the existing active flag and timeout. A locked old file (e.g. held by a running preview
    process) is warned about, never fatal. Settings and the video cache live in
    %LOCALAPPDATA%\Win4KScreensavers and are untouched by either name.

.PARAMETER SkipBuild
    Install whatever is already in publish\ instead of rebuilding.

.PARAMETER PublishDir
    Where to find the published Win4KSS.scr/.exe when -SkipBuild is used. Defaults to
    <repo>\publish. The Inno Setup installer (installer/Win4KSS.iss) passes the folder it
    just extracted its own files into, so this script works from an installed copy that
    is not inside a clone of the repo at all.

.PARAMETER SetAsDefault
    Also make Win4KSS the active screen saver and set the idle timeout.

.PARAMETER TimeoutMinutes
    Idle timeout to set when -SetAsDefault is used. Default 10.

.PARAMETER Hotkey
    Hotkey for the shortcut, in .lnk form. Default CTRL+ALT+S. Pass "" for none.

.PARAMETER UserScope
    Install to %LOCALAPPDATA%\Programs\Win4KSS instead of System32, with no elevation.

    Windows runs whatever .scr path HKCU points at, so this is a fully working install —
    the only thing it gives up is appearing in the Screen Saver Settings *dropdown*, which
    only ever lists System32. Everything else (idle trigger, resume lock, the hotkey,
    /c and /p from the Settings dialog) behaves identically.

.PARAMETER Uninstall
    Remove the .scr and the shortcuts (and any leftover Win4KAerial install).

.EXAMPLE
    .\scripts\install-scr.ps1 -UserScope          # no elevation needed
    .\scripts\install-scr.ps1 -SetAsDefault -TimeoutMinutes 5
    .\scripts\install-scr.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    [switch]$SkipBuild,
    [string]$PublishDir,
    [switch]$SetAsDefault,
    [int]$TimeoutMinutes = 10,
    [string]$Hotkey = 'CTRL+ALT+S',
    [switch]$UserScope,
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot   = Split-Path -Parent $PSScriptRoot
if (-not $PublishDir) { $PublishDir = Join-Path $repoRoot 'publish' }
$publishDir = $PublishDir
$appName    = 'Win4KSS'
$scrName    = "$appName.scr"
$system32   = Join-Path $env:SystemRoot 'System32'
$installed  = Join-Path $system32 $scrName
$shortcut   = 'Start Screen Saver.lnk'

# The shortcut cannot point at the .scr. Windows registers scrfile's "open" verb as
# `"%1" /S`, so the shell discards a shortcut's arguments and substitutes /S — the saver
# would run directly and never honour "On resume, display logon screen". A companion .exe
# is installed for the shortcut to target instead.
$companionDir = Join-Path $env:LOCALAPPDATA "Programs\$appName"
$companionExe = Join-Path $companionDir "$appName.exe"

# In user scope the .scr lives beside the companion .exe rather than in System32.
if ($UserScope) {
    $installed = Join-Path $companionDir $scrName
}

# The previous name. Only ever removed, never installed.
$legacyName      = 'Win4KAerial'
$legacyDir       = Join-Path $env:LOCALAPPDATA "Programs\$legacyName"
$legacyUserScr   = Join-Path $legacyDir "$legacyName.scr"
$legacyUserExe   = Join-Path $legacyDir "$legacyName.exe"
$legacySystemScr = Join-Path $system32 "$legacyName.scr"

function Test-Admin {
    $identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Assert-Admin {
    # User-scope installs write only under %LOCALAPPDATA% and HKCU, so they never elevate.
    if ($UserScope) { return }
    if (Test-Admin) { return }

    # Writing to System32 needs elevation, and there is no way to ask for it mid-script,
    # so relaunch with the same arguments rather than failing halfway through.
    Write-Host 'Elevation is required to write to System32. Relaunching...' -ForegroundColor Yellow

    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    if ($SkipBuild)      { $argList += '-SkipBuild' }
    if ($PSBoundParameters.ContainsKey('PublishDir')) { $argList += '-PublishDir'; $argList += "`"$PublishDir`"" }
    if ($SetAsDefault)   { $argList += '-SetAsDefault'; $argList += "-TimeoutMinutes"; $argList += $TimeoutMinutes }
    if ($Uninstall)      { $argList += '-Uninstall' }
    if ($Hotkey -ne 'CTRL+ALT+S') { $argList += '-Hotkey'; $argList += "`"$Hotkey`"" }

    Start-Process -FilePath 'powershell.exe' -ArgumentList $argList -Verb RunAs -Wait
    exit 0
}

function New-TriggerShortcut {
    param([string]$Path, [string]$Target, [string]$HotkeyValue)

    $shell = New-Object -ComObject WScript.Shell
    $lnk = $shell.CreateShortcut($Path)
    $lnk.TargetPath       = $Target
    $lnk.Arguments        = '/now'
    $lnk.Description      = 'Start the screen saver now (locks on resume if configured)'
    $lnk.WorkingDirectory = Split-Path $Target
    $lnk.IconLocation     = "$Target,0"
    if (-not [string]::IsNullOrWhiteSpace($HotkeyValue)) { $lnk.Hotkey = $HotkeyValue }
    $lnk.Save()

    Write-Host "  shortcut: $Path" -ForegroundColor Green
}

function Remove-FileTolerant {
    # A running process holds its image file open and Windows refuses to delete it (leaked
    # /p preview processes did exactly this). That must never fail an install: warn, go on.
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return }
    try {
        Remove-Item -LiteralPath $Path -Force -ErrorAction Stop
        Write-Host "  removed: $Path" -ForegroundColor Green
    } catch {
        Write-Warning "Could not remove $Path ($($_.Exception.Message)). It is probably in use by a running $legacyName process; delete it once that exits, or after signing out."
    }
}

function Test-LegacyScreenSaverPath {
    # SCRNSAVE.EXE can hold either the long path or an 8.3 short path (desk.cpl writes short
    # paths), so match on the file name rather than the full path.
    param([string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value)) { return $false }
    $leaf = Split-Path -Leaf ($Value.Trim().Trim('"'))
    return ($leaf -ieq "$legacyName.scr") -or ($leaf -imatch '^WIN4KA~\d+\.SCR$')
}

function Sync-ScreenSaverSession {
    # Writing the registry is NOT enough. The running session caches these, so until
    # SystemParametersInfo is called SPI_GETSCREENSAVEACTIVE keeps reporting false and
    # Windows ignores SC_SCREENSAVE entirely — the hotkey appears to do nothing.
    param([bool]$Active, [int]$TimeoutSeconds)

    $spi = @'
using System; using System.Runtime.InteropServices;
public static class Win4KSpi {
  [DllImport("user32.dll", EntryPoint="SystemParametersInfoW", SetLastError=true)]
  public static extern bool Set(uint action, uint param, IntPtr pv, uint winIni);
  [DllImport("user32.dll", EntryPoint="SystemParametersInfoW", SetLastError=true)]
  public static extern bool GetBool(uint action, uint param, ref bool pv, uint winIni);
}
'@
    if (-not ('Win4KSpi' -as [type])) { Add-Type -TypeDefinition $spi }

    $SPI_SETSCREENSAVEACTIVE  = 0x0011
    $SPI_GETSCREENSAVEACTIVE  = 0x0010
    $SPI_SETSCREENSAVETIMEOUT = 0x000F
    $SPIF_UPDATEINIFILE_SENDCHANGE = 0x0003

    $okActive  = [Win4KSpi]::Set($SPI_SETSCREENSAVEACTIVE, [uint32]$Active, [IntPtr]::Zero, $SPIF_UPDATEINIFILE_SENDCHANGE)
    $errActive = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
    if ($TimeoutSeconds -gt 0) {
        [Win4KSpi]::Set($SPI_SETSCREENSAVETIMEOUT, $TimeoutSeconds, [IntPtr]::Zero, $SPIF_UPDATEINIFILE_SENDCHANGE) | Out-Null
    }

    $live = $false
    [Win4KSpi]::GetBool($SPI_GETSCREENSAVEACTIVE, 0, [ref]$live, 0) | Out-Null

    if ($live -eq $Active) {
        Write-Host "  session updated (SPI_GETSCREENSAVEACTIVE = $live)" -ForegroundColor Green
    } else {
        # Error 329 is "an operation is currently in progress with the device", which is what
        # you get when Windows still believes a screen saver is running — usually because one
        # was force-killed rather than allowed to exit.
        Write-Host "  WARNING: the session did not accept the change (SPI returned $okActive, error $errActive)." -ForegroundColor Yellow
        Write-Host '  The screen saver will not start until this clears. Open Screen Saver Settings' -ForegroundColor Yellow
        Write-Host '  and press Apply, or sign out and back in.' -ForegroundColor Yellow
    }
}

function Remove-LegacyInstall {
    # Leftovers from when the app was called Win4KAerial. The Desktop / Start Menu shortcuts
    # kept the same file name, so the new install overwrites them with the new target.
    $any = (Test-Path -LiteralPath $legacyDir) -or (Test-Path -LiteralPath $legacySystemScr)
    if (-not $any) { return }

    Write-Host "Removing the old $legacyName install..." -ForegroundColor Cyan

    Remove-FileTolerant $legacyUserExe
    Remove-FileTolerant $legacyUserScr
    if (Test-Path -LiteralPath $legacyDir) {
        if (-not (Get-ChildItem -LiteralPath $legacyDir -Force | Select-Object -First 1)) {
            Remove-Item -LiteralPath $legacyDir -Force -ErrorAction SilentlyContinue
            Write-Host "  removed: $legacyDir" -ForegroundColor Green
        } else {
            Write-Warning "$legacyDir still has files in it; left in place."
        }
    }

    if (Test-Path -LiteralPath $legacySystemScr) {
        if (Test-Admin) {
            Remove-FileTolerant $legacySystemScr
        } else {
            Write-Warning "$legacySystemScr remains; removing it needs an elevated prompt: Remove-Item '$legacySystemScr'"
        }
    }
}

function Stop-NonSaverInstances {
    # Windows keeps the .scr locked while any instance runs. Preview (/p), settings (/c) and
    # /now instances are safe to end. A full-screen /s instance is NOT: force-killing the
    # running screen saver leaves SPI_GETSCREENSAVERRUNNING stuck true until sign-out.
    # (Get-Process -Name never matched .scr processes anyway — it only strips '.exe'.)
    $procs = @(Get-CimInstance Win32_Process -Filter "Name='$appName.scr' OR Name='$appName.exe'" -ErrorAction SilentlyContinue)
    foreach ($p in $procs) {
        if ("$($p.CommandLine)" -imatch '\s[/-]s(\s|$)') {
            throw "$appName is running as the screen saver (pid $($p.ProcessId)). Dismiss it with the mouse or keyboard, then re-run."
        }
    }
    foreach ($p in $procs) {
        Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue
    }
    if ($procs.Count -gt 0) { Start-Sleep -Milliseconds 300 }
}

# ---- uninstall --------------------------------------------------------------------------

function Restore-ScreenSaverRegistration {
    # Leaving SCRNSAVE.EXE pointing at a .scr we are about to delete means Windows tries to
    # launch a file that no longer exists the next time the idle timer (or /now) fires.
    # Repoint at the stock blank saver that ships with Windows when it is there; otherwise
    # just turn the feature off rather than leave it dangling.
    $desktopKey = 'HKCU:\Control Panel\Desktop'
    $current = (Get-ItemProperty -Path $desktopKey -Name 'SCRNSAVE.EXE' -ErrorAction SilentlyContinue).'SCRNSAVE.EXE'
    if ([string]::IsNullOrWhiteSpace($current)) { return }

    $leaf = Split-Path -Leaf ($current.Trim().Trim('"'))
    $isOurs = ($leaf -ieq $scrName) -or ($leaf -imatch '^WIN4KS~\d+\.SCR$') -or (Test-LegacyScreenSaverPath $current)
    if (-not $isOurs) { return }

    $stock = Join-Path $system32 'scrnsave.scr'
    if (Test-Path $stock) {
        Set-ItemProperty -Path $desktopKey -Name 'SCRNSAVE.EXE' -Value $stock
        Write-Host "  SCRNSAVE.EXE -> $stock (was $current)" -ForegroundColor Green
    } else {
        Remove-ItemProperty -Path $desktopKey -Name 'SCRNSAVE.EXE' -ErrorAction SilentlyContinue
        Set-ItemProperty -Path $desktopKey -Name 'ScreenSaveActive' -Value '0'
        Write-Host "  cleared SCRNSAVE.EXE (was $current); no stock saver found, so the screen saver is now off" -ForegroundColor Green
    }
}

if ($Uninstall) {
    Assert-Admin

    # Guard against the same wedge the install path guards against: deleting the .scr out
    # from under an actively-running /s instance leaves Windows believing one is still
    # running (SPI_GETSCREENSAVERRUNNING stuck true) until sign-out. /p, /c and /now
    # instances are closed first and are safe to end.
    Stop-NonSaverInstances

    Restore-ScreenSaverRegistration

    if (Test-Path $installed) {
        Remove-Item $installed -Force
        Write-Host "Removed $installed" -ForegroundColor Green
    } else {
        Write-Host "Not installed at $installed" -ForegroundColor Yellow
    }

    foreach ($dir in @([Environment]::GetFolderPath('DesktopDirectory'), [Environment]::GetFolderPath('Programs'))) {
        $path = Join-Path $dir $shortcut
        if (Test-Path $path) {
            Remove-Item $path -Force
            Write-Host "Removed $path" -ForegroundColor Green
        }
    }

    if (Test-Path $companionDir) {
        Remove-Item $companionDir -Recurse -Force
        Write-Host "Removed $companionDir" -ForegroundColor Green
    }

    Remove-LegacyInstall

    Write-Host ''
    Write-Host 'Cached video and settings were left alone. To remove them too:' -ForegroundColor Gray
    Write-Host "  Remove-Item -Recurse '$env:LOCALAPPDATA\Win4KScreensavers'" -ForegroundColor Gray
    exit 0
}

# ---- build ------------------------------------------------------------------------------

if (-not $SkipBuild) {
    Write-Host 'Publishing...' -ForegroundColor Cyan

    $libmpv = Join-Path $repoRoot 'native\mpv\libmpv-2.dll'
    if (-not (Test-Path $libmpv)) {
        throw "libmpv-2.dll is missing. Run scripts/fetch-libmpv.ps1 first."
    }

    Push-Location $repoRoot
    try {
        & dotnet publish 'src\Win4K.ScreenSaver\Win4K.ScreenSaver.csproj' -c Release -o $publishDir
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
    } finally {
        Pop-Location
    }
}

$source = Join-Path $publishDir $scrName
if (-not (Test-Path $source)) {
    throw "$source not found. Run without -SkipBuild."
}

# ---- install ----------------------------------------------------------------------------

Assert-Admin

$sizeMb = [math]::Round((Get-Item $source).Length / 1MB, 1)
$where = if ($UserScope) { 'the user profile' } else { 'System32' }
Write-Host "Installing $scrName ($sizeMb MB) to $where..." -ForegroundColor Cyan

Stop-NonSaverInstances

New-Item -ItemType Directory -Force -Path (Split-Path $installed) | Out-Null

# -PublishDir can legitimately point at $installed's own folder already (the Inno Setup
# installer extracts straight into the user-scope companion dir and passes that same path
# back in as -PublishDir), in which case $source and $installed are the same file.
# Copy-Item throws "Cannot overwrite the item ... with itself" if asked to copy a file onto
# its own path, so treat that as already-installed rather than as a copy to perform.
$sourceFull = (Resolve-Path -LiteralPath $source).Path
$installedFull = if (Test-Path -LiteralPath $installed) { (Resolve-Path -LiteralPath $installed).Path } else { $null }
if ($installedFull -and $sourceFull -ieq $installedFull) {
    Write-Host "  already in place: $installed" -ForegroundColor Green
} else {
    Copy-Item $source $installed -Force
    Write-Host "  installed: $installed" -ForegroundColor Green
}

# Companion .exe for the shortcut to target. A hard link costs no extra disk when both
# paths are on the same volume, which they normally are; fall back to a copy otherwise.
# Same self-reference possibility as above: in a -UserScope install sourced from the
# companion dir itself, $sourceExe and $companionExe are the same file already.
New-Item -ItemType Directory -Force -Path $companionDir | Out-Null

$sourceExe = Join-Path $publishDir "$appName.exe"
$companionIsSourceExe = (Test-Path -LiteralPath $sourceExe) -and (Test-Path -LiteralPath $companionExe) -and
    ((Resolve-Path -LiteralPath $sourceExe).Path -ieq (Resolve-Path -LiteralPath $companionExe).Path)

if ($companionIsSourceExe) {
    Write-Host "  companion: $companionExe (already in place)" -ForegroundColor Green
} else {
    if (Test-Path $companionExe) { Remove-Item $companionExe -Force }

    $linked = $false
    if ([IO.Path]::GetPathRoot($companionExe) -eq [IO.Path]::GetPathRoot($installed)) {
        try {
            New-Item -ItemType HardLink -Path $companionExe -Target $installed -ErrorAction Stop | Out-Null
            $linked = $true
        } catch {
            $linked = $false
        }
    }
    if (-not $linked) { Copy-Item $sourceExe $companionExe -Force }

    Write-Host "  companion: $companionExe$(if ($linked) { ' (hard link)' })" -ForegroundColor Green
}

# ---- shortcuts --------------------------------------------------------------------------

Write-Host 'Creating shortcuts...' -ForegroundColor Cyan

# Explorer only binds a .lnk hotkey for shortcuts under the Desktop or the Start Menu,
# so both are created rather than picking one. Both target the companion .exe.
New-TriggerShortcut -Path (Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) $shortcut) `
                    -Target $companionExe -HotkeyValue $Hotkey
New-TriggerShortcut -Path (Join-Path ([Environment]::GetFolderPath('Programs')) $shortcut) `
                    -Target $companionExe -HotkeyValue $Hotkey

# ---- optionally activate ------------------------------------------------------------------

# HKCU\Control Panel\Desktop is per-user, so this deliberately affects only the invoking
# user even when the script is running elevated.
$desktopKey = 'HKCU:\Control Panel\Desktop'
$current = (Get-ItemProperty -Path $desktopKey -Name 'SCRNSAVE.EXE' -ErrorAction SilentlyContinue).'SCRNSAVE.EXE'

if ($SetAsDefault) {
    Write-Host 'Setting as the active screen saver...' -ForegroundColor Cyan

    Set-ItemProperty -Path $desktopKey -Name 'SCRNSAVE.EXE' -Value $installed
    Set-ItemProperty -Path $desktopKey -Name 'ScreenSaveActive' -Value '1'
    Set-ItemProperty -Path $desktopKey -Name 'ScreenSaveTimeOut' -Value ([string]($TimeoutMinutes * 60))

    Write-Host "  active, idle timeout $TimeoutMinutes minutes" -ForegroundColor Green
    Sync-ScreenSaverSession -Active $true -TimeoutSeconds ($TimeoutMinutes * 60)
}
elseif (Test-LegacyScreenSaverPath $current) {
    # Migration: the old name was the configured saver. Keep it configured under the new
    # name, with the user's existing active flag and timeout rather than this script's
    # defaults — and without this, deleting the old .scr below would leave none at all.
    Write-Host "Repointing the screen saver from $current..." -ForegroundColor Cyan
    Set-ItemProperty -Path $desktopKey -Name 'SCRNSAVE.EXE' -Value $installed

    $props   = Get-ItemProperty -Path $desktopKey -ErrorAction SilentlyContinue
    $active  = [bool]($props.PSObject.Properties['ScreenSaveActive'] -and "$($props.ScreenSaveActive)" -eq '1')
    $seconds = 0
    if ($props.PSObject.Properties['ScreenSaveTimeOut']) {
        [int]::TryParse("$($props.ScreenSaveTimeOut)", [ref]$seconds) | Out-Null
    }

    Write-Host "  SCRNSAVE.EXE -> $installed (active=$active, timeout ${seconds}s kept)" -ForegroundColor Green
    Sync-ScreenSaverSession -Active $active -TimeoutSeconds $seconds
}

# ---- migrate ----------------------------------------------------------------------------

# Last, so the new install is complete and registered before anything old goes away.
Remove-LegacyInstall

Write-Host ''
Write-Host 'Done.' -ForegroundColor Green
Write-Host "  Configure : rundll32.exe desk.cpl,InstallScreenSaver $installed" -ForegroundColor Gray
Write-Host "  Settings  : $installed /c" -ForegroundColor Gray
Write-Host "  Try it    : $installed /s" -ForegroundColor Gray
if (-not [string]::IsNullOrWhiteSpace($Hotkey)) {
    Write-Host "  Hotkey    : $Hotkey (from the Desktop or Start Menu shortcut)" -ForegroundColor Gray
}
