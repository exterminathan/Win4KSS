<#
.SYNOPSIS
    Downloads libmpv-2.dll into native/mpv/ — the video engine for Win4KScreensavers.

.DESCRIPTION
    Pulls the latest mpv-dev-x86_64 build from shinchiro/mpv-winbuild-cmake and extracts
    the shared library plus headers.

    The DLL is ~45 MB and is deliberately NOT committed (see .gitignore). Run this once
    after cloning, and again whenever you want to move to a newer mpv.

    The release archives are .7z and this machine has no 7-Zip, so the script bootstraps
    the official standalone 7zr.exe (602 KB, from 7-zip.org) into native/tools/ if it
    cannot find a system 7z first.

    Deliberately uses the plain x86_64 build rather than x86_64-v3: the v3 variant requires
    AVX2 and buys nothing for hardware-decoded playback, where the GPU does the work.

.PARAMETER Force
    Re-download even if native/mpv/libmpv-2.dll already exists.

.EXAMPLE
    .\scripts\fetch-libmpv.ps1
    .\scripts\fetch-libmpv.ps1 -Force
#>
[CmdletBinding()]
param(
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot  = Split-Path -Parent $PSScriptRoot
$mpvDir    = Join-Path $repoRoot 'native\mpv'
$toolsDir  = Join-Path $repoRoot 'native\tools'
$targetDll = Join-Path $mpvDir 'libmpv-2.dll'

if ((Test-Path $targetDll) -and (-not $Force)) {
    $size = [math]::Round((Get-Item $targetDll).Length / 1MB, 1)
    Write-Host "libmpv-2.dll already present ($size MB). Use -Force to re-download." -ForegroundColor Green
    exit 0
}

# TLS 1.2 for PowerShell 5.1, whose default can still be SSL3/TLS1 on older boxes.
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

function Resolve-SevenZip {
    <# Returns a path to a 7z-capable exe, bootstrapping 7zr.exe if nothing is installed. #>
    $candidates = @(
        'C:\Program Files\7-Zip\7z.exe',
        'C:\Program Files (x86)\7-Zip\7z.exe',
        "$env:LOCALAPPDATA\Programs\7-Zip\7z.exe"
    )
    foreach ($c in $candidates) {
        if (Test-Path $c) { return $c }
    }
    $onPath = Get-Command 7z, 7za, 7zr -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($onPath) { return $onPath.Source }

    $sevenZr = Join-Path $toolsDir '7zr.exe'
    if (Test-Path $sevenZr) { return $sevenZr }

    Write-Host 'No 7-Zip found. Fetching standalone 7zr.exe from 7-zip.org...' -ForegroundColor Yellow
    New-Item -ItemType Directory -Force -Path $toolsDir | Out-Null
    Invoke-WebRequest -Uri 'https://www.7-zip.org/a/7zr.exe' -OutFile $sevenZr -UseBasicParsing
    return $sevenZr
}

Write-Host 'Querying latest mpv-dev release...' -ForegroundColor Cyan

# Unauthenticated api.github.com calls are capped at 60/hour *per IP*, and GitHub Actions
# runners share outbound IP ranges across unrelated workflows -- that pool is frequently
# already exhausted before this script ever runs, confirmed by an actual CI failure, not
# assumed. GITHUB_TOKEN (automatically available as a job-scoped secret in Actions, see
# .github/workflows/build-installer.yml) raises this to 5,000/hour and draws from a separate,
# per-token pool. Local runs have no such variable set and fall back to unauthenticated exactly
# as before -- this never requires a token to work, it just avoids CI's shared-IP problem
# when one happens to be available.
$headers = @{ 'User-Agent' = 'Win4KScreensavers-fetch-libmpv' }
$ghToken = if ($env:GITHUB_TOKEN) { $env:GITHUB_TOKEN } elseif ($env:GH_TOKEN) { $env:GH_TOKEN } else { $null }
if ($ghToken) { $headers['Authorization'] = "Bearer $ghToken" }
$release = Invoke-RestMethod -Uri 'https://api.github.com/repos/shinchiro/mpv-winbuild-cmake/releases/latest' -Headers $headers

# Match mpv-dev-x86_64-<date>-git-<sha>.7z but NOT mpv-dev-x86_64-v3-... (AVX2-only build).
$asset = $release.assets |
    Where-Object { $_.name -match '^mpv-dev-x86_64-(?!v3-)' } |
    Select-Object -First 1

if (-not $asset) {
    throw "No mpv-dev-x86_64 asset in release '$($release.tag_name)'. Assets: $(($release.assets | ForEach-Object { $_.name }) -join ', ')"
}

$sizeMb = [math]::Round($asset.size / 1MB, 1)
Write-Host "  release: $($release.tag_name)"
Write-Host "  asset:   $($asset.name) ($sizeMb MB)"

$tempDir = Join-Path ([IO.Path]::GetTempPath()) "win4k-mpv-$([Guid]::NewGuid().ToString('N').Substring(0,8))"
New-Item -ItemType Directory -Force -Path $tempDir | Out-Null

try {
    $archive = Join-Path $tempDir $asset.name

    Write-Host 'Downloading...' -ForegroundColor Cyan
    # Invoke-WebRequest's progress bar makes large downloads dramatically slower in PS 5.1.
    $priorProgress = $ProgressPreference
    $ProgressPreference = 'SilentlyContinue'
    try {
        Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $archive -UseBasicParsing
    } finally {
        $ProgressPreference = $priorProgress
    }

    $sevenZip = Resolve-SevenZip
    Write-Host "Extracting with $sevenZip ..." -ForegroundColor Cyan

    $extractDir = Join-Path $tempDir 'x'
    & $sevenZip x $archive "-o$extractDir" -y | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "7-Zip extraction failed with exit code $LASTEXITCODE" }

    $dll = Get-ChildItem -Path $extractDir -Recurse -Filter 'libmpv-2.dll' | Select-Object -First 1
    if (-not $dll) { throw "libmpv-2.dll not found inside $($asset.name)" }

    New-Item -ItemType Directory -Force -Path $mpvDir | Out-Null
    Copy-Item $dll.FullName $targetDll -Force

    # Headers are not needed to build (we P/Invoke by hand) but are worth keeping next to
    # the DLL as the authoritative reference for the client API surface and version.
    $include = Get-ChildItem -Path $extractDir -Recurse -Directory -Filter 'include' | Select-Object -First 1
    if ($include) {
        Copy-Item $include.FullName (Join-Path $mpvDir 'include') -Recurse -Force
    }

    # Record what we pulled, so the version is answerable without re-querying GitHub.
    [PSCustomObject]@{
        release   = $release.tag_name
        asset     = $asset.name
        published = $release.published_at
        fetched   = (Get-Date).ToString('o')
    } | ConvertTo-Json | Set-Content -Path (Join-Path $mpvDir 'VERSION.json') -Encoding utf8

    $dllMb = [math]::Round((Get-Item $targetDll).Length / 1MB, 1)
    Write-Host ''
    Write-Host "libmpv-2.dll -> $targetDll ($dllMb MB)" -ForegroundColor Green
    Write-Host "release $($release.tag_name)" -ForegroundColor Green
}
finally {
    Remove-Item $tempDir -Recurse -Force -ErrorAction SilentlyContinue
}
