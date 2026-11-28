<#
.SYNOPSIS
    Puts FFmpeg where the kiosk's camera looks for it (T-482, T-495).

.DESCRIPTION
    For a Windows kiosk provisioned without ffmpeg-*-win64-lgpl*.zip in its
    dependencies folder: the panel then says "FFmpeg yoxdur". Downloads
    BtbN's LGPL build (the one docs/KIOSK_CAMERA.md names), or takes a zip
    already on the machine, and unpacks it to
    C:\Program Files\NeoPrint\ffmpeg\ - what provision.ps1 does.
    The kiosk application finds it within 10 minutes, or at once after a restart.

    Run in an administrator PowerShell on the kiosk.

.PARAMETER Zip
    A zip already downloaded (offline kiosk). Without it, the build is downloaded.
#>
param(
    [string]$Zip
)

$ErrorActionPreference = 'Stop'

$Url = 'https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n8.1-latest-win64-lgpl-8.1.zip'
$Target = Join-Path $env:ProgramFiles 'NeoPrint\ffmpeg'
$Exe = Join-Path $Target 'bin\ffmpeg.exe'

if (Test-Path $Exe) {
    Write-Host "FFmpeg artıq var: $Exe" -ForegroundColor Green
    & $Exe -hide_banner -version | Select-Object -First 1
    return
}

if (-not $Zip) {
    $Zip = Join-Path $env:TEMP 'neoprint-ffmpeg.zip'
    Write-Host "FFmpeg endirilir (~170 MB): $Url"
    $ProgressPreference = 'SilentlyContinue'
    Invoke-WebRequest -Uri $Url -OutFile $Zip -UseBasicParsing
}

if (-not (Test-Path $Zip)) {
    throw "Zip tapılmadı: $Zip"
}

$Unpacked = Join-Path $env:TEMP 'neoprint-ffmpeg'
if (Test-Path $Unpacked) {
    Remove-Item $Unpacked -Recurse -Force
}

Expand-Archive -Path $Zip -DestinationPath $Unpacked -Force
$Root = Get-ChildItem -Path $Unpacked -Directory | Select-Object -First 1
if (-not $Root -or -not (Test-Path (Join-Path $Root.FullName 'bin\ffmpeg.exe'))) {
    throw 'Zip-də bin\ffmpeg.exe yoxdur — ffmpeg-*-win64-lgpl*.zip olduğunu yoxlayın.'
}

New-Item -ItemType Directory -Force -Path $Target | Out-Null
Copy-Item -Path (Join-Path $Root.FullName '*') -Destination $Target -Recurse -Force
Remove-Item $Unpacked -Recurse -Force

& $Exe -hide_banner -version | Select-Object -First 1
Write-Host "Hazırdır: $Exe" -ForegroundColor Green
Write-Host 'Kiosk tətbiqi onu 10 dəqiqə ərzində özü tapır; dərhal üçün tətbiqi yenidən başladın (panel → Əmrlər → "Tətbiqi yenidən başlat").'
