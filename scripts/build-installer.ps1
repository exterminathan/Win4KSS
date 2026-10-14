<#
.SYNOPSIS
    Builds the Win4KSS installer end to end: publish, verify, compile.

.DESCRIPTION
    1. Makes sure native/mpv/libmpv-2.dll exists (fetching it via fetch-libmpv.ps1 if not) --
       this has to happen BEFORE `dotnet publish`, because libmpv is a build-time input.
       Win4K.Core.csproj only flows the DLL into the build at all if it already exists
       (Condition="Exists('$(LibMpvPath)')").
    2. Runs `dotnet publish` for the screen saver project. Publish is an ordinary loose
       multi-file build now (no PublishSingleFile / IncludeNativeLibrariesForSelfExtract --
       that self-extracting-native-DLL shape is what tripped an AV heuristic false positive on
       an unsigned download), so libmpv-2.dll, WebView2Loader.dll, Win4K.Core.dll etc. land
       beside Win4KSS.exe/.scr as their own files instead of being bundled inside them.
    3. Sanity-checks the result: every file installer/Win4KSS.iss's [Files] section expects to
       ship is actually present in publish\, and libmpv-2.dll in particular is the expected
       rough size -- a build with libmpv missing still "succeeds" at a much smaller file size,
       which is exactly the kind of silent failure this script exists to catch.
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
    Write-Host 'Publishing Win4KSS (Release)...' -ForegroundColor Cyan

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

# Every file installer\Win4KSS.iss's [Files] section expects to ship, kept in sync with it by
# hand (Inno's preprocessor can't read this script, and this script can't easily parse .iss).
# If `dotnet publish`'s output ever stops matching this list, that is exactly the kind of
# silent mismatch -- an installer shipped without one of its own DLLs -- this check exists to
# catch before ISCC.exe silently compiles a broken installer around whatever is missing.
$expectedFiles = @(
    'Win4KSS.exe', 'Win4KSS.scr', 'Win4KSS.dll', 'Win4KSS.deps.json', 'Win4KSS.runtimeconfig.json',
    'Win4K.Core.dll', 'libmpv-2.dll', 'WebView2Loader.dll',
    'Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.WinForms.dll'
)
foreach ($name in $expectedFiles) {
    $p = Join-Path $publishDir $name
    if (-not (Test-Path -LiteralPath $p)) {
        throw "$p is missing after publish. Something is wrong with the build, not this script."
    }
}

# The separate single-managed-file, no-native-DLLs build for the System32 "systemscr" Task
# (Win4K.ScreenSaver.csproj's nested system32 publish, NativeDependencies.cs) -- produced by
# the same `dotnet publish` call above, so a missing file here means that build broke, not
# that this script forgot to ask for it.
$system32ScrPath = Join-Path $publishDir 'system32\Win4KSS.scr'
if (-not (Test-Path -LiteralPath $system32ScrPath)) {
    throw "$system32ScrPath is missing after publish. The System32 build target isn't producing it."
}
$system32ScrSizeMb = [math]::Round((Get-Item -LiteralPath $system32ScrPath).Length / 1MB, 1)
Write-Host "publish\system32\Win4KSS.scr: $system32ScrSizeMb MB" -ForegroundColor Green
if ($system32ScrSizeMb -gt 10) {
    throw "publish\system32\Win4KSS.scr is $system32ScrSizeMb MB -- too large for a managed-only, " +
          "no-native-DLLs build. It may have picked up libmpv-2.dll/WebView2Loader.dll by mistake."
}

$exePath              = Join-Path $publishDir 'Win4KSS.exe'
$publishedLibmpvPath  = Join-Path $publishDir 'libmpv-2.dll'

# Publish is loose multi-file now (no PublishSingleFile / IncludeNativeLibrariesForSelfExtract),
# so the two checks that used to live here are now the other way around from when this script
# was written: Win4KSS.exe should be SMALL (an ordinary apphost, no embedded native payload) and
# libmpv-2.dll -- ~114 MB alone -- should be sitting loose beside it, not missing. Checking both
# directions catches a regression back to single-file publishing just as reliably as it catches
# libmpv failing to flow into the build at all.
$exeSizeMb = [math]::Round((Get-Item -LiteralPath $exePath).Length / 1MB, 1)
Write-Host "Win4KSS.exe: $exeSizeMb MB" -ForegroundColor Green
if ($exeSizeMb -gt 10) {
    throw "Win4KSS.exe is $exeSizeMb MB -- too large for an ordinary apphost. This looks like " +
          "PublishSingleFile/IncludeNativeLibrariesForSelfExtract came back in Win4K.ScreenSaver.csproj " +
          "(deliberately removed -- see its own comment and CLAUDE.md -- to stop shipping a " +
          "self-extracting-native-DLL exe, the shape that triggered an AV heuristic false positive)."
}

$libmpvSizeMb = [math]::Round((Get-Item -LiteralPath $publishedLibmpvPath).Length / 1MB, 1)
Write-Host "libmpv-2.dll: $libmpvSizeMb MB" -ForegroundColor Green
if ($libmpvSizeMb -lt 80) {
    throw "libmpv-2.dll is only $libmpvSizeMb MB -- too small to be the real library. " +
          "Confirm native\mpv\libmpv-2.dll exists and re-publish (delete publish\ first to rule out a stale build)."
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
