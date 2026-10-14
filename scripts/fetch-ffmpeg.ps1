<#
.SYNOPSIS
    Downloads ffmpeg (for clip-browser previews) into native/ffmpeg/.

.DESCRIPTION
    Pulls the GPL "shared" Windows build of the FFmpeg 9.0 release line from BtbN/FFmpeg-Builds:
    ffmpeg.exe and ffprobe.exe plus the av*/sw* DLLs they share. ffplay is not kept.

    Shared rather than static: the two static exes would each carry their own ~100 MB copy of
    the libraries. The DLLs are installed in their own ffmpeg\ subfolder, never beside
    Win4KSS.exe.

    This build's TLS is Schannel, which cannot verify Apple's CDN (Apple Root CA is not in the
    Windows store). That does not matter: ffmpeg never goes to the network itself; it reads
    through Win4KSS's loopback relay (Win4K.Core/Media/LoopbackRelay.cs).

    GPL, like the libmpv build Win4KSS already ships; see docs/THIRD-PARTY-NOTICES.md.

.PARAMETER Force
    Re-download even if native/ffmpeg/ffmpeg.exe already exists.
#>
[CmdletBinding()]
param(
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot  = Split-Path -Parent $PSScriptRoot
$targetDir = Join-Path $repoRoot 'native\ffmpeg'
$targetExe = Join-Path $targetDir 'ffmpeg.exe'
$assetName = 'ffmpeg-n9.0-latest-win64-gpl-shared-9.0.zip'

if ((Test-Path $targetExe) -and (-not $Force)) {
    Write-Host "ffmpeg already present in $targetDir. Use -Force to re-download." -ForegroundColor Green
    exit 0
}

[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

# Same token handling as fetch-libmpv.ps1: CI runners share an IP pool whose unauthenticated
# api.github.com quota is often already spent.
$headers = @{ 'User-Agent' = 'Win4KScreensavers-fetch-ffmpeg' }
$ghToken = if ($env:GITHUB_TOKEN) { $env:GITHUB_TOKEN } elseif ($env:GH_TOKEN) { $env:GH_TOKEN } else { $null }
if ($ghToken) { $headers['Authorization'] = "Bearer $ghToken" }

Write-Host 'Querying BtbN/FFmpeg-Builds...' -ForegroundColor Cyan
$release = Invoke-RestMethod -Uri 'https://api.github.com/repos/BtbN/FFmpeg-Builds/releases/tags/latest' -Headers $headers
$asset = $release.assets | Where-Object { $_.name -eq $assetName } | Select-Object -First 1
if (-not $asset) {
    throw "No $assetName in BtbN/FFmpeg-Builds 'latest'. Assets: $(($release.assets | ForEach-Object { $_.name }) -join ', ')"
}
Write-Host "  asset: $($asset.name) ($([math]::Round($asset.size / 1MB, 1)) MB)"

$tempDir = Join-Path ([IO.Path]::GetTempPath()) "win4k-ffmpeg-$([Guid]::NewGuid().ToString('N').Substring(0,8))"
New-Item -ItemType Directory -Force -Path $tempDir | Out-Null
try {
    $archive = Join-Path $tempDir $asset.name
    $priorProgress = $ProgressPreference
    $ProgressPreference = 'SilentlyContinue'
    try {
        Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $archive -UseBasicParsing
    } finally {
        $ProgressPreference = $priorProgress
    }

    $extractDir = Join-Path $tempDir 'x'
    Expand-Archive -LiteralPath $archive -DestinationPath $extractDir -Force

    $bin = Get-ChildItem -LiteralPath $extractDir -Recurse -Directory -Filter bin | Select-Object -First 1
    if (-not $bin -or -not (Test-Path (Join-Path $bin.FullName 'ffmpeg.exe'))) {
        throw "ffmpeg.exe not found in $($asset.name)"
    }

    if (Test-Path $targetDir) { Remove-Item -LiteralPath $targetDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $targetDir | Out-Null
    Get-ChildItem -LiteralPath $bin.FullName -File |
        Where-Object { $_.Name -ne 'ffplay.exe' -and ($_.Extension -eq '.exe' -or $_.Extension -eq '.dll') } |
        Copy-Item -Destination $targetDir

    $licence = Get-ChildItem -LiteralPath $extractDir -Recurse -File -Filter LICENSE.txt | Select-Object -First 1
    if ($licence) { Copy-Item -LiteralPath $licence.FullName -Destination (Join-Path $targetDir 'LICENSE.txt') }

    $total = (Get-ChildItem -LiteralPath $targetDir -File | Measure-Object Length -Sum).Sum
    Write-Host "ffmpeg -> $targetDir ($([math]::Round($total / 1MB, 1)) MB)" -ForegroundColor Green
}
finally {
    Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue
}
