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
    Start Screen Saver shortcuts on a fresh install (targeting the user-scope companion .exe),
    and starts the resident hotkey listener (Win4KSS.exe /hotkey), which registers itself
    under HKCU\...\Run to start at sign-in.

    The hotkey is NOT on the shortcuts (it was until v1.2.6): the listener holds it with
    RegisterHotKey, so it works whether or not any shortcut exists. Existing shortcuts are
    left as the user has them; the listener strips the old Hotkey field from them itself,
    after adopting its value, so this script must not clear it first.

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

.PARAMETER UserScope
    Skip the System32 copy; install only to %LOCALAPPDATA%\Programs\Win4KSS, with no
    elevation.

    Windows runs whatever .scr path HKCU points at, so this is a fully working install —
    the only thing it gives up is appearing in the Screen Saver Settings *dropdown*, which
    only ever lists System32. Everything else (idle trigger, resume lock, the hotkey,
    /c and /p from the Settings dialog) behaves identically.

.PARAMETER Uninstall
    Remove the System32 .scr (if present), the user-scope companion dir, the shortcuts, the
    hotkey listener and its HKCU Run value (and any leftover Win4KAerial install).

.PARAMETER RemoveMachineUninstallEntry
    Also delete the Apps & Features entry an older, machine-wide (admin) Inno install left in
    HKLM, now that the installer is per-user. Done in the same elevated step as the System32
    copy, so a migrating user sees one prompt.

.PARAMETER SkipShortcuts
    Leave the Desktop / Start Menu shortcuts alone. For sandboxed test runs only.

.PARAMETER SkipHotkey
    Do not start the hotkey listener after installing. For sandboxed test runs: a temporary
    LOCALAPPDATA does not move the app's own data root (.NET asks the shell for it), so a
    listener started from a sandbox acts on the real profile unless WIN4K_HOME is set too.

ELEVATION
    Nothing here runs elevated as a whole any more. Everything except System32 lives under
    %LOCALAPPDATA% and HKCU. The System32 relay is copied by a separate elevated child of this
    script (-System32Step), and only when the installed copy is missing or carries a different
    relay version (Win4K.ScreenSaver.csproj's Win4KRelayVersion, stamped into its product
    version) -- so an ordinary update never asks for admin. Declining the prompt is not fatal:
    the install falls back to a .scr in the companion dir, which Windows runs just the same;
    only the Screen Saver Settings dropdown entry is missing until the next install.

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
    [switch]$UserScope,
    [switch]$Uninstall,
    [switch]$RemoveMachineUninstallEntry,
    [switch]$SkipShortcuts,
    [switch]$SkipHotkey,
    # Internal: the elevated children this script starts for the System32 work.
    [switch]$System32Step,
    [switch]$RemoveSystem32Step,
    [string]$System32Dir
)

if ($System32Dir) { $env:WIN4K_SYSTEM32_DIR = $System32Dir }

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot   = Split-Path -Parent $PSScriptRoot
if (-not $PublishDir) { $PublishDir = Join-Path $repoRoot 'publish' }
$publishDir = $PublishDir
$appName    = 'Win4KSS'
$scrName    = "$appName.scr"
# WIN4K_SYSTEM32_DIR: a test seam, so a sandboxed run never writes the real System32.
$system32   = if ($env:WIN4K_SYSTEM32_DIR) { $env:WIN4K_SYSTEM32_DIR } else { Join-Path $env:SystemRoot 'System32' }
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

$machineUninstallKeys = @(
    'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{FDEAF37E-5817-43BA-A346-B9BB300B9B46}_is1',
    'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{FDEAF37E-5817-43BA-A346-B9BB300B9B46}_is1'
)

function Invoke-Elevated {
    # Runs this script again, elevated, for one System32 step. Returns whether it succeeded.
    # A declined UAC prompt throws from Start-Process; that is a normal answer, not an error.
    param([string[]]$StepArgs)

    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"",
                 '-PublishDir', "`"$publishDir`"") + $StepArgs
    # An elevated process gets a fresh environment, so a sandbox override must ride along.
    if ($env:WIN4K_SYSTEM32_DIR) { $argList += @('-System32Dir', "`"$env:WIN4K_SYSTEM32_DIR`"") }
    try {
        $p = Start-Process -FilePath 'powershell.exe' -ArgumentList $argList -Verb RunAs -Wait -PassThru -WindowStyle Hidden
        return ($p.ExitCode -eq 0)
    } catch {
        Write-Warning "Administrator permission was not given ($($_.Exception.Message))."
        return $false
    }
}

function Get-ProductVersion {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    return (Get-Item -LiteralPath $Path).VersionInfo.ProductVersion
}

function Test-System32RelayCurrent {
    # The relay is only replaced when its relay version changes, never per release.
    param([string]$Stub, [string]$Target)
    $want = Get-ProductVersion $Stub
    $have = Get-ProductVersion $Target
    return ($null -ne $want) -and ($want -eq $have)
}

function Test-AccessDenied {
    param($ErrorRecord)
    $e = $ErrorRecord.Exception
    while ($e) {
        if ($e -is [System.UnauthorizedAccessException]) { return $true }
        $e = $e.InnerException
    }
    return $false
}

function Copy-System32Relay {
    # Tries without elevation first (a sandboxed SystemRoot, or an already-elevated shell), and
    # only asks for admin when System32 actually refuses the write.
    param([string]$Stub, [string]$Target, [bool]$AlsoRemoveMachineEntry)

    if (-not $AlsoRemoveMachineEntry -or (Test-Admin)) {
        try {
            Copy-Item -LiteralPath $Stub -Destination $Target -Force -ErrorAction Stop
            if ($AlsoRemoveMachineEntry) { Remove-MachineUninstallEntry }
            return $true
        } catch {
            if (-not (Test-AccessDenied $_)) { throw }
        }
    }

    Write-Host '  asking for permission to add Win4KSS to the Screen Saver Settings list...' -ForegroundColor Cyan
    $step = @('-System32Step')
    if ($AlsoRemoveMachineEntry) { $step += '-RemoveMachineUninstallEntry' }
    return (Invoke-Elevated $step) -and (Test-System32RelayCurrent -Stub $Stub -Target $Target)
}

function Remove-MachineUninstallEntry {
    foreach ($key in $machineUninstallKeys) {
        if (Test-Path -LiteralPath $key) {
            Remove-Item -LiteralPath $key -Recurse -Force
            Write-Host "  removed the old machine-wide uninstall entry: $key" -ForegroundColor Green
        }
    }
}

function New-TriggerShortcut {
    # Opens an existing shortcut rather than replacing it, and never sets its Hotkey: the
    # listener holds the hotkey, and adopts and clears the field old versions left here.
    param([string]$Path, [string]$Target)

    $shell = New-Object -ComObject WScript.Shell
    $lnk = $shell.CreateShortcut($Path)
    $lnk.TargetPath       = $Target
    $lnk.Arguments        = '/now'
    $lnk.Description      = 'Start the screen saver now (locks on resume if configured)'
    $lnk.WorkingDirectory = Split-Path $Target
    $ico = Join-Path (Split-Path $Target) 'win4kss.ico'
    $lnk.IconLocation     = if (Test-Path -LiteralPath $ico) { "$ico,0" } else { "$Target,0" }
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
    # Windows keeps the .scr locked while any instance runs. Preview (/p), settings (/c),
    # /now and the hotkey listener (/hotkey) are safe to end. A full-screen /s instance is NOT:
    # force-killing the running screen saver leaves SPI_GETSCREENSAVERRUNNING stuck true until
    # sign-out. (Get-Process -Name never matched .scr processes anyway — it only strips '.exe'.)
    # Only this install's own processes (its companion dir, its System32 copy), so a sandboxed
    # run never ends the real install's browser or listener.
    $ourDir = [IO.Path]::GetFullPath($companionDir).TrimEnd('\') + '\'
    $ourScr = Join-Path $system32 $scrName
    $procs = @(Get-CimInstance Win32_Process -Filter "Name='$appName.scr' OR Name='$appName.exe'" -ErrorAction SilentlyContinue |
        Where-Object {
            $exe = "$($_.ExecutablePath)"
            ($exe -ieq $ourScr) -or $exe.StartsWith($ourDir, [StringComparison]::OrdinalIgnoreCase)
        })
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

# ---- elevated children ------------------------------------------------------------------

$system32Target = Join-Path $system32 $scrName
$system32Stub   = Join-Path (Join-Path $publishDir 'system32') $scrName

if ($System32Step) {
    Copy-Item -LiteralPath $system32Stub -Destination $system32Target -Force
    if (Test-Path -LiteralPath $legacySystemScr) { Remove-FileTolerant $legacySystemScr }
    if ($RemoveMachineUninstallEntry) { Remove-MachineUninstallEntry }
    exit 0
}

if ($RemoveSystem32Step) {
    foreach ($path in @($system32Target, $legacySystemScr)) {
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
    }
    exit 0
}

# ---- hotkey listener --------------------------------------------------------------------

$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

function Remove-HotkeyRunValue {
    # The listener's sign-in entry ("Win4KSS", or "Win4KSS-test-..." from a test data root),
    # and only when it starts something in this install's folder: a sandboxed uninstall must
    # never remove the real one.
    $props = Get-ItemProperty -Path $runKey -ErrorAction SilentlyContinue
    if (-not $props) { return }
    $prefix = [IO.Path]::GetFullPath($companionDir).TrimEnd('\') + '\'
    foreach ($p in @($props.PSObject.Properties)) {
        if ($p.Name -notlike "$appName*") { continue }
        $exe = "$($p.Value)".Trim()
        if ($exe.StartsWith('"')) { $exe = $exe.Substring(1, [Math]::Max(0, $exe.IndexOf('"', 1) - 1)) } else { $exe = ($exe -split ' ')[0] }
        if ($exe.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
            Remove-ItemProperty -Path $runKey -Name $p.Name -ErrorAction SilentlyContinue
            Write-Host "Removed the sign-in entry $($p.Name)" -ForegroundColor Green
        }
    }
}

function Start-HotkeyListener {
    # The listener moves an old shortcut hotkey into settings, registers its own Run value and
    # holds the hotkey; it exits at once when the hotkey is off. Waits until it holds its
    # single-instance mutex (or has exited), so a browser opened next sees the migrated hotkey.
    if (-not (Test-Path -LiteralPath $companionExe)) { return }
    try {
        $p = Start-Process -FilePath $companionExe -ArgumentList '/hotkey' -WorkingDirectory $companionDir -PassThru
    } catch {
        Write-Warning "Could not start the hotkey listener ($($_.Exception.Message)). It starts at the next sign-in, or when the clip browser opens."
        return
    }
    $deadline = (Get-Date).AddSeconds(5)
    while ((Get-Date) -lt $deadline) {
        $m = $null
        if ($p.HasExited -or [Threading.Mutex]::TryOpenExisting('Local\Win4KSS.Hotkey', [ref]$m)) {
            if ($m) { $m.Dispose() }
            break
        }
        Start-Sleep -Milliseconds 100
    }
    if ($p.HasExited) {
        Write-Host '  hotkey listener: not running (the hotkey is turned off)' -ForegroundColor Green
    } else {
        Write-Host "  hotkey listener: running (pid $($p.Id))" -ForegroundColor Green
    }
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

    $value = $current.Trim().Trim('"')
    $leaf = Split-Path -Leaf $value
    $isOurs = ($leaf -ieq $scrName) -or ($leaf -imatch '^WIN4KS~\d+\.SCR$') -or (Test-LegacyScreenSaverPath $current)
    if (-not $isOurs) { return }

    # And in a folder this install owns: a sandboxed run (WIN4K_SYSTEM32_DIR, a temp
    # LOCALAPPDATA) must never reset the real machine's screen saver.
    $dir = Split-Path -Parent $value
    $ourDirs = @($system32, $companionDir, $legacyDir) | ForEach-Object { [IO.Path]::GetFullPath($_).TrimEnd('\') }
    if ($ourDirs -inotcontains [IO.Path]::GetFullPath($dir).TrimEnd('\')) { return }

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
    # Guard against the same wedge the install path guards against: deleting the .scr out
    # from under an actively-running /s instance leaves Windows believing one is still
    # running (SPI_GETSCREENSAVERRUNNING stuck true) until sign-out. /p, /c and /now
    # instances are closed first and are safe to end.
    Stop-NonSaverInstances

    Remove-HotkeyRunValue

    Restore-ScreenSaverRegistration

    # The only part of an uninstall that needs admin, and only if a System32 copy exists.
    if ((Test-Path -LiteralPath $system32Target) -or (Test-Path -LiteralPath $legacySystemScr)) {
        $removed = $true
        foreach ($path in @($system32Target, $legacySystemScr)) {
            if (-not (Test-Path -LiteralPath $path)) { continue }
            try {
                Remove-Item -LiteralPath $path -Force -ErrorAction Stop
            } catch {
                if (-not (Test-AccessDenied $_)) { throw }
                $removed = $false
            }
        }
        if (-not $removed) { $removed = Invoke-Elevated @('-RemoveSystem32Step') }
        if (-not $removed -or (Test-Path -LiteralPath $system32Target)) {
            Write-Warning "$system32Target remains; remove it from an administrator prompt."
        } else {
            Write-Host "Removed $system32Target" -ForegroundColor Green
        }
    }

    $shortcutDirs = if ($SkipShortcuts) { @() } else { @([Environment]::GetFolderPath('DesktopDirectory'), [Environment]::GetFolderPath('Programs')) }
    foreach ($dir in $shortcutDirs) {
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
$looseScr = Join-Path $publishDir $scrName
if (-not (Test-Path -LiteralPath $looseScr)) {
    throw "$looseScr not found. Run without -SkipBuild."
}
if (-not $UserScope -and -not (Test-Path -LiteralPath $system32Stub)) {
    throw "$system32Stub not found. Run without -SkipBuild."
}

# ---- install ----------------------------------------------------------------------------

# Sums the whole publish folder: the .scr itself is a ~150 KB apphost; the real size is
# libmpv-2.dll and friends sitting beside it.
$sizeMb = [math]::Round(((Get-ChildItem -LiteralPath $publishDir -Recurse -File | Measure-Object -Property Length -Sum).Sum) / 1MB, 1)
Write-Host "Installing $appName ($sizeMb MB)..." -ForegroundColor Cyan

Stop-NonSaverInstances

# The .scr Windows runs. System32 when the relay there is (or can be made) current; otherwise
# a copy of the loose build's .scr beside the companion exe, which Windows runs just the same.
$companionScr = Join-Path $companionDir $scrName
$useSystem32 = $false
if (-not $UserScope) {
    if ((Test-System32RelayCurrent -Stub $system32Stub -Target $system32Target) -and -not $RemoveMachineUninstallEntry) {
        Write-Host "  System32 relay already current: $system32Target" -ForegroundColor Green
        $useSystem32 = $true
    } elseif (Copy-System32Relay -Stub $system32Stub -Target $system32Target -AlsoRemoveMachineEntry $RemoveMachineUninstallEntry.IsPresent) {
        Write-Host "  installed: $system32Target" -ForegroundColor Green
        $useSystem32 = $true
    } elseif (Test-Path -LiteralPath $system32Target) {
        # An older copy is still there and still forwards to the companion exe; keep using it.
        Write-Warning "Could not update $system32Target; the existing copy keeps working."
        $useSystem32 = $true
    } else {
        Write-Warning 'Win4KSS will not appear in the Screen Saver Settings list until a later install is allowed to add it. Everything else works.'
    }
}
$installed = if ($useSystem32) { $system32Target } else { $companionScr }

New-Item -ItemType Directory -Force -Path $companionDir | Out-Null
if ($useSystem32) {
    # A companion-dir .scr from a user-scope install has nothing pointing at it any more.
    Remove-FileTolerant $companionScr
} else {
    $looseFull = (Resolve-Path -LiteralPath $looseScr).Path
    $companionScrFull = if (Test-Path -LiteralPath $companionScr) { (Resolve-Path -LiteralPath $companionScr).Path } else { $null }
    if ($companionScrFull -ine $looseFull) { Copy-Item -LiteralPath $looseScr -Destination $companionScr -Force }
    Write-Host "  installed: $companionScr" -ForegroundColor Green
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
    if (-not $useSystem32 -and [IO.Path]::GetPathRoot($companionExe) -eq [IO.Path]::GetPathRoot($installed)) {
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

if (-not $SkipShortcuts) {
    # Optional launchers, nothing more: the hotkey works without them. A fresh install gets
    # both; an existing one keeps the ones the user kept, re-pointed at the companion .exe
    # (the Win4KAerial rename used the same file name). Removed ones stay removed.
    $fresh = -not (Test-Path -LiteralPath (Join-Path $env:LOCALAPPDATA 'Win4KScreensavers\settings.json'))
    $shortcutPaths = @((Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) $shortcut),
                       (Join-Path ([Environment]::GetFolderPath('Programs')) $shortcut))
    if ($fresh -or @($shortcutPaths | Where-Object { Test-Path -LiteralPath $_ }).Count -gt 0) {
        Write-Host 'Shortcuts...' -ForegroundColor Cyan
    }
    foreach ($path in $shortcutPaths) {
        if ($fresh -or (Test-Path -LiteralPath $path)) {
            New-TriggerShortcut -Path $path -Target $companionExe
        }
    }
}

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

# ---- hotkey -------------------------------------------------------------------------------

# Stop-NonSaverInstances ended any listener above; this one is the new version.
if (-not $SkipHotkey) {
    Write-Host 'Starting the hotkey listener...' -ForegroundColor Cyan
    Start-HotkeyListener
}

Write-Host ''
Write-Host 'Done.' -ForegroundColor Green
Write-Host "  Configure : rundll32.exe desk.cpl,InstallScreenSaver $installed" -ForegroundColor Gray
Write-Host "  Settings  : $installed /c" -ForegroundColor Gray
Write-Host "  Try it    : $installed /s" -ForegroundColor Gray
Write-Host '  Hotkey    : Settings > Start now in the clip browser (default Ctrl+Alt+S)' -ForegroundColor Gray
