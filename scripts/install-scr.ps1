<#
.SYNOPSIS
    Builds, installs and registers Win4KSS as a Windows screen saver.

.DESCRIPTION
    Publishes the app (a loose multi-file build, plus a separate single-managed-file,
    no-native-DLLs build under publish\system32\ -- see Win4K.ScreenSaver.csproj and
    NativeDependencies.cs). Always installs the loose build into the user-scope companion dir
    at %LOCALAPPDATA%\Programs\Win4KSS; without -UserScope, ALSO copies the system32 build's
    .scr into System32 (needs elevation -- the only place Windows looks when populating the
    screen saver dropdown) and points SCRNSAVE.EXE at it instead. Either way, creates the
    walk-away shortcut with its hotkey, targeting the user-scope companion .exe.

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
    Skip the System32 copy; install only to %LOCALAPPDATA%\Programs\Win4KSS, with no
    elevation.

    Windows runs whatever .scr path HKCU points at, so this is a fully working install —
    the only thing it gives up is appearing in the Screen Saver Settings *dropdown*, which
    only ever lists System32. Everything else (idle trigger, resume lock, the hotkey,
    /c and /p from the Settings dialog) behaves identically.

.PARAMETER Uninstall
    Remove the System32 .scr (if present), the user-scope companion dir, and the shortcuts
    (and any leftover Win4KAerial install).

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

function Test-SingleFilePublish {
    # A single-file publish (PublishSingleFile + IncludeNativeLibrariesForSelfExtract) bundles
    # Win4K.Core.dll and libmpv-2.dll INSIDE the exe/.scr via self-extraction, so neither sits
    # beside it on disk. The now-normal loose multi-file publish leaves both sitting right next
    # to the exe. Checking for either is a reliable, build-output-stable signal: both file names
    # come from this project's own output (ProjectReference / native/mpv), not a third party that
    # could rename them. System32 mode needs this to be true -- copying only the .scr there
    # without its sibling DLLs produces a screen saver Windows cannot actually run.
    param([string]$Dir, [string]$ExeName)

    $exePath = Join-Path $Dir $ExeName
    if (-not (Test-Path -LiteralPath $exePath)) { return $false }

    $core   = Join-Path $Dir 'Win4K.Core.dll'
    $libmpv = Join-Path $Dir 'libmpv-2.dll'
    return -not ((Test-Path -LiteralPath $core) -or (Test-Path -LiteralPath $libmpv))
}

function Sync-CompanionFiles {
    # -UserScope needs every sibling file a loose multi-file publish produces -- Win4K.Core.dll,
    # libmpv-2.dll, WebView2Loader.dll, the two WebView2 managed DLLs, runtimes\win-x64\native\,
    # deps.json, runtimeconfig.json -- actually sitting beside the companion .exe, or it cannot
    # run. Copying only the .scr and the .exe (which is all the code below this function does on
    # its own) worked by coincidence under the Inno Setup installer, where -PublishDir IS
    # $companionDir already (Inno's own [Files] section put everything there directly) -- but
    # the documented dev flow runs this script against <repo>\publish, a DIFFERENT folder, and
    # left $companionDir missing every one of those files.
    #
    # Skips the .scr/.exe themselves: the code below has its own hard-link/self-reference
    # handling for those two and must not be fought over them here. Skips *.pdb: those are
    # symbols, not something the running app needs beside it. Skips publish\system32\ entirely:
    # that is the separate one-file, no-native-DLLs build for the System32 copy (see below) --
    # it belongs beside nothing, and copying it into the companion dir would just confuse the
    # "which Win4KSS.scr is this" question with no benefit. Removes stale files that are in
    # $To but not in $From, so a DLL a past build needed and this one does not does not linger
    # across upgrades -- but ONLY among .dll/.json files and anything under runtimes\, i.e. the
    # file shapes a publish actually produces. $companionDir is NOT always publish-output-only:
    # the Inno Setup installer also puts its own unins000.exe/.dat, this very script, and
    # THIRD-PARTY-NOTICES.md there. Deleting those would break Programs & Features' uninstall
    # and strand a script this same [Run] step still needs -- restricting removal by file shape
    # keeps this function from ever touching something it didn't put there itself.
    param([string]$From, [string]$To)

    $fromFull = (Resolve-Path -LiteralPath $From).Path
    if ((Test-Path -LiteralPath $To) -and ((Resolve-Path -LiteralPath $To).Path -ieq $fromFull)) { return }

    New-Item -ItemType Directory -Force -Path $To | Out-Null
    $skipNames = @($scrName, "$appName.exe")

    $wanted = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    Get-ChildItem -LiteralPath $From -Recurse -File |
        Where-Object {
            $_.Extension -ine '.pdb' -and ($skipNames -notcontains $_.Name) -and
            -not $_.FullName.Substring($fromFull.Length + 1).StartsWith('system32\', [StringComparison]::OrdinalIgnoreCase)
        } |
        ForEach-Object {
            $rel  = $_.FullName.Substring($fromFull.Length + 1)
            $dest = Join-Path $To $rel
            $destDir = Split-Path $dest
            if ($destDir -and -not (Test-Path -LiteralPath $destDir)) {
                New-Item -ItemType Directory -Force -Path $destDir | Out-Null
            }
            Copy-Item -LiteralPath $_.FullName -Destination $dest -Force
            [void]$wanted.Add($rel)
        }

    $toFull = (Resolve-Path -LiteralPath $To).Path
    Get-ChildItem -LiteralPath $To -Recurse -File |
        Where-Object {
            ($skipNames -notcontains $_.Name) -and
            ($_.Extension -ieq '.dll' -or $_.Extension -ieq '.json' -or
             $_.FullName.Substring($toFull.Length + 1).StartsWith('runtimes\', [StringComparison]::OrdinalIgnoreCase))
        } |
        ForEach-Object {
            $rel = $_.FullName.Substring($toFull.Length + 1)
            if (-not $wanted.Contains($rel)) {
                Remove-Item -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue
                Write-Host "  removed stale: $($_.FullName)" -ForegroundColor DarkGray
            }
        }
}

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

# Two different .scr builds, picked by install scope. -UserScope installs the ordinary loose
# multi-file build (needs Sync-CompanionFiles below for its sibling DLLs). System32 mode
# installs publish\system32\Win4KSS.scr instead: a separate single-managed-file build with no
# native DLLs bundled (app side; see NativeDependencies.cs), which resolves libmpv-2.dll and
# WebView2Loader.dll from the companion dir at its own startup -- deliberately, so System32
# never gains a copy of either. WebView2Loader.dll especially is a name shared across every
# WebView2 app on the machine; putting our copy on System32's DLL search path, where any
# process can find it, was rejected for exactly that reason. Either way $companionDir gets the
# full loose build (below, unconditional) -- the System32 .scr is dead without it.
$source = if ($UserScope) { Join-Path $publishDir $scrName } else { Join-Path (Join-Path $publishDir 'system32') $scrName }
if (-not (Test-Path $source)) {
    throw "$source not found. Run without -SkipBuild."
}

# ---- install ----------------------------------------------------------------------------

Assert-Admin

# Sums the whole publish folder, not just $source -- for a loose multi-file publish, the .scr
# itself is a ~150 KB apphost; the real size is libmpv-2.dll and friends sitting beside it.
$sizeMb = [math]::Round(((Get-ChildItem -LiteralPath $publishDir -Recurse -File | Measure-Object -Property Length -Sum).Sum) / 1MB, 1)
$where = if ($UserScope) { 'the user profile' } else { 'System32' }
Write-Host "Installing $appName ($sizeMb MB) to $where..." -ForegroundColor Cyan

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

# A stray $companionDir\Win4KSS.scr left over from an earlier -UserScope install (where it WAS
# $installed) has nothing pointing at it once System32 mode takes over -- SCRNSAVE.EXE now
# points at the System32 copy instead -- but it would otherwise just sit there, still
# hard-linked to the companion exe until that gets overwritten below, going stale across
# upgrades and inviting a "which .scr is this" confusion. Harmless, but cheap to clean up.
if (-not $UserScope) {
    Remove-FileTolerant (Join-Path $companionDir $scrName)
}

# Unconditional, regardless of install scope: the companion .exe is what every shortcut
# targets (the .scr itself can't be, see the header comment), in BOTH System32 and -UserScope
# mode, and it needs its sibling DLLs (Win4K.Core.dll, libmpv-2.dll, WebView2Loader.dll,
# runtimes\, both .json files) to actually run /now when clicked -- a loose multi-file publish
# does not bundle them into the exe itself. Previously scoped to -UserScope only, which missed
# that System32 mode populates the SAME companion dir for its shortcut target. No-op when
# $publishDir already IS $companionDir (Inno's own case).
Sync-CompanionFiles -From $publishDir -To $companionDir

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

    # Hard-linking against $installed only makes sense in -UserScope: there $installed IS the
    # loose build's own .scr, byte-identical to $sourceExe (same apphost, renamed) -- a free
    # link, not a copy. In System32 mode $installed is the unrelated one-file build (see the
    # $source comment above): linking against it would make the companion .exe silently BE
    # that build instead of the loose one Sync-CompanionFiles just put its DLLs beside, and a
    # version mismatch waiting to happen the day the two builds move independently. Always copy
    # from $sourceExe there instead -- it is small, so losing the free-link optimisation for
    # that one case costs nothing.
    $linked = $false
    if ($UserScope -and [IO.Path]::GetPathRoot($companionExe) -eq [IO.Path]::GetPathRoot($installed)) {
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
