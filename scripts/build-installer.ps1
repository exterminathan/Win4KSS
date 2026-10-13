<#
.SYNOPSIS
    Builds the Win4KSS installer end to end: publish, verify, compile.

.DESCRIPTION
    1. Makes sure native/mpv/libmpv-2.dll exists (fetching it via fetch-libmpv.ps1 if not) --
       this has to happen BEFORE `dotnet publish`, because libmpv is a build-time input.
       Win4K.Core.csproj only flows the DLL into the build at all if it already exists
       (Condition="Exists('$(LibMpvPath)')"), and with PublishSingleFile +
       IncludeNativeLibrariesForSelfExtract it ends up bundled *inside* Win4KSS.exe/.scr, not
       sitting next to them -- a real `dotnet publish` run against this repo confirms the
       output folder holds exactly Win4KSS.exe, Win4KSS.scr, and two .pdb files, nothing
       named libmpv or WebView2Loader. There is nothing for the installer to add separately;
       the thing to get right is making sure it was embedded at build time.
    2. Runs `dotnet publish` for the screen saver project.
    3. Sanity-checks the result (the two files exist, are the expected rough size -- a build
       with libmpv missing still "succeeds" at a much smaller file size, which is exactly the
       kind of silent failure this script exists to catch).
    4. Finds Inno Setup's ISCC.exe, installing Inno Setup via winget if it's missing.
    5. Compiles installer/Win4KSS.iss into installer/output/Win4KSS-Setup-<version>.exe.

.PARAMETER Version
    Installer/app version. Defaults to the <Version> already in Win4K.ScreenSaver.csproj.

.PARAMETER SkipPublish
    Compile the installer from whatever is already in publish\ instead of re-publishing.
    Useful while iterating on the .iss script itself.

.EXAMPLE
    .\scripts\build-installer.ps1
    .\scripts\build-installer.ps1 -SkipPublish
    .\scripts\build-installer.ps1 -Version 1.1.0
#>
[CmdletBinding()]
param(
    [string]$Version,
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot      = Split-Path -Parent $PSScriptRoot
$publishDir    = Join-Path $repoRoot 'publish'
$installerDir  = Join-Path $repoRoot 'installer'
$outputDir     = Join-Path $installerDir 'output'
$issPath       = Join-Path $installerDir 'Win4KSS.iss'
$csprojPath    = Join-Path $repoRoot 'src\Win4K.ScreenSaver\Win4K.ScreenSaver.csproj'
$libmpvPath    = Join-Path $repoRoot 'native\mpv\libmpv-2.dll'
$fetchLibmpv   = Join-Path $PSScriptRoot 'fetch-libmpv.ps1'

# ---- 0. version -----------------------------------------------------------------------------

if (-not $Version) {
    $csprojXml = [xml](Get-Content -LiteralPath $csprojPath -Raw)
    $Version = ($csprojXml.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
    if (-not $Version) {
        throw "Could not read <Version> from $csprojPath. Pass -Version explicitly."
    }
}
Write-Host "Version: $Version" -ForegroundColor Cyan

# ---- 1. libmpv --------------------------------------------------------------------------

if (-not (Test-Path -LiteralPath $libmpvPath)) {
    Write-Host 'libmpv-2.dll is missing; fetching it before publishing (it is a build-time input, not a packaging one)...' -ForegroundColor Yellow
    & $fetchLibmpv
    if (-not (Test-Path -LiteralPath $libmpvPath)) {
        throw "fetch-libmpv.ps1 ran but $libmpvPath still doesn't exist. Fix that before continuing."
    }
}
else {
    $mpvSizeMb = [math]::Round((Get-Item -LiteralPath $libmpvPath).Length / 1MB, 1)
    Write-Host "libmpv-2.dll present ($mpvSizeMb MB)." -ForegroundColor Green
}

# ---- 2. publish -------------------------------------------------------------------------

if (-not $SkipPublish) {
    Write-Host 'Publishing Win4KSS (Release, single-file)...' -ForegroundColor Cyan

    if (Test-Path -LiteralPath $publishDir) {
        Remove-Item -LiteralPath $publishDir -Recurse -Force
    }

    Push-Location $repoRoot
    try {
        & dotnet publish 'src\Win4K.ScreenSaver\Win4K.ScreenSaver.csproj' -c Release -o $publishDir
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
    } finally {
        Pop-Location
    }
}
else {
    Write-Host 'Skipping publish (-SkipPublish); using whatever is already in publish\.' -ForegroundColor Yellow
}

# ---- 3. verify ----------------------------------------------------------------------------

$exePath = Join-Path $publishDir 'Win4KSS.exe'
$scrPath = Join-Path $publishDir 'Win4KSS.scr'

foreach ($p in @($exePath, $scrPath)) {
    if (-not (Test-Path -LiteralPath $p)) {
        throw "$p is missing after publish. Something is wrong with the build, not this script."
    }
}

# libmpv alone is ~114 MB; a publish that somehow didn't embed it would be a fraction of this.
# This is the "don't assume, verify" check -- see scripts/build-installer.ps1's own history in
# CLAUDE.md for why: it was confirmed empirically that libmpv/WebView2Loader are bundled INSIDE
# Win4KSS.exe/.scr (self-extracted at runtime), not placed beside them as separate files, so
# their absence as loose files in publish\ is expected and not itself a problem -- file *size*
# is the real signal.
$exeSizeMb = [math]::Round((Get-Item -LiteralPath $exePath).Length / 1MB, 1)
Write-Host "Win4KSS.exe: $exeSizeMb MB" -ForegroundColor Green
if ($exeSizeMb -lt 80) {
    throw "Win4KSS.exe is only $exeSizeMb MB -- too small to contain libmpv (~114 MB) and WebView2Loader. " +
          "Confirm native\mpv\libmpv-2.dll exists and re-publish (delete publish\ first to rule out a stale build)."
}

$looseLibmpv = Join-Path $publishDir 'libmpv-2.dll'
if (Test-Path -LiteralPath $looseLibmpv) {
    # Would mean a future SDK/csproj change stopped embedding it and started copying it
    # loose instead -- the installer's [Files] section would then need updating to include
    # it explicitly, per the original task. Flag it rather than silently doing the wrong thing.
    Write-Warning "libmpv-2.dll showed up as a loose file in publish\ -- that didn't happen when this script was written. installer\Win4KSS.iss's [Files] section does not currently copy it; add it there if this persists."
}

# ---- 4. find or install Inno Setup -------------------------------------------------------

function Find-Iscc {
    $candidates = @(
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    )
    foreach ($c in $candidates) {
        if ($c -and (Test-Path -LiteralPath $c)) { return $c }
    }
    $onPath = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    return $null
}

$iscc = Find-Iscc
if (-not $iscc) {
    Write-Host 'Inno Setup (ISCC.exe) not found. Trying winget...' -ForegroundColor Yellow

    $winget = Get-Command winget -ErrorAction SilentlyContinue
    if ($winget) {
        & winget install --id JRSoftware.InnoSetup -e --silent --accept-package-agreements --accept-source-agreements
        $iscc = Find-Iscc
    }

    if (-not $iscc) {
        Write-Host ''
        Write-Host 'Could not find or install Inno Setup automatically.' -ForegroundColor Red
        Write-Host 'Install it yourself, then re-run this script:' -ForegroundColor Yellow
        Write-Host '  winget install JRSoftware.InnoSetup' -ForegroundColor Yellow
        Write-Host '  (or download it from https://jrsoftware.org/isdl.php)' -ForegroundColor Yellow
        throw 'Inno Setup is required to compile the installer.'
    }
}
Write-Host "Using Inno Setup compiler: $iscc" -ForegroundColor Green

# ---- 5. compile -----------------------------------------------------------------------------

New-Item -ItemType Directory -Force -Path $outputDir | Out-Null

Write-Host "Compiling $issPath ..." -ForegroundColor Cyan
& $iscc "/DMyAppVersion=$Version" $issPath
if ($LASTEXITCODE -ne 0) { throw "ISCC.exe failed with exit code $LASTEXITCODE" }

$outputExe = Join-Path $outputDir "Win4KSS-Setup-$Version.exe"
if (-not (Test-Path -LiteralPath $outputExe)) {
    throw "Expected $outputExe but it was not produced. Check the ISCC output above."
}

$outSizeMb = [math]::Round((Get-Item -LiteralPath $outputExe).Length / 1MB, 1)
Write-Host ''
Write-Host "Built $outputExe ($outSizeMb MB)" -ForegroundColor Green
