# ---------------------------------------------------------------------------
# NeoPrint dev-kiosk: builds and starts the kiosk app against the local stack
# (docs/DEV_ENVIRONMENT.md, section 5). Run from an elevated PowerShell:
#
#   powershell -ExecutionPolicy Bypass -File tools\dev-kiosk\start-kiosk.ps1
#   ... start-kiosk.ps1 -EnrollmentToken <token>      # no activation code screen
#   ... start-kiosk.ps1 -ApiUrl http://192.168.1.50:5080
#
# Administrator is required: the device key is a machine-scoped CNG key. A
# kiosk still running from before is stopped first, or its locked DLLs break
# the build. Development settings: local API, fake printer, no lockdown.
# ---------------------------------------------------------------------------
param(
    [string]$EnrollmentToken,
    [string]$ApiUrl,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$project = Join-Path $root 'desktop\src\NeoPrint.Kiosk'

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host 'PowerShell administrator kimi açılmayıb.' -ForegroundColor Red
    Write-Host 'Kioskun cihaz açarı maşın səviyyəsindədir (CNG) və adi sessiyada yaradıla bilmir.'
    Write-Host 'PowerShell-i "Run as administrator" ilə açıb əmri yenidən işlədin.'
    exit 1
}

& (Join-Path $PSScriptRoot 'stop-kiosk.ps1')

if (-not $NoBuild) {
    Write-Host "Build ($Configuration)..."
    dotnet build $project -c $Configuration --nologo -v quiet
    if ($LASTEXITCODE -ne 0) {
        Write-Host 'Build alınmadı — yuxarıdakı xətaya baxın.' -ForegroundColor Red
        exit $LASTEXITCODE
    }
}

# The newest exe of the configuration just built; an incremental build does not
# touch an unchanged exe, so a stale one from the other configuration could
# otherwise look newer. Any configuration only with -NoBuild and nothing there.
$newest = {
    param($folder)
    Get-ChildItem $folder -Recurse -Filter 'NeoPrint.Kiosk.exe' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
}
$exe = & $newest (Join-Path $project "bin\$Configuration")
if (-not $exe) { $exe = & $newest (Join-Path $project 'bin') }

if (-not $exe) {
    Write-Host 'NeoPrint.Kiosk.exe tapılmadı. -NoBuild olmadan işlədin.' -ForegroundColor Red
    exit 1
}

$env:NEOPRINT_KIOSK_ENVIRONMENT = 'Development'

$arguments = @()
if ($EnrollmentToken) { $arguments += @('--enrollment-token', $EnrollmentToken) }
if ($ApiUrl) { $arguments += @('--api-url', $ApiUrl) }

$started = if ($arguments.Count -gt 0) {
    Start-Process -FilePath $exe.FullName -ArgumentList $arguments -WorkingDirectory $exe.DirectoryName -PassThru
} else {
    Start-Process -FilePath $exe.FullName -WorkingDirectory $exe.DirectoryName -PassThru
}

# PDF, images and text open on any machine (PDFium ships with the build);
# Office and PS / PCL / XPS files need the converters the kiosk image has.
if (-not (Test-Path 'C:\Program Files\LibreOffice\program\soffice.exe')) {
    Write-Host 'Qeyd: LibreOffice quraşdırılmayıb — Word / Excel / PowerPoint faylları bu maşında açılmayacaq (kiosk "bu fayl növünü açan proqram işləmir" deyəcək). PDF, şəkil və TXT işləyir.' -ForegroundColor Yellow
}
if (-not (Get-Command 'gswin64c.exe' -ErrorAction SilentlyContinue)) {
    Write-Host 'Qeyd: Ghostscript PATH-da yoxdur — PS / PCL / XPS faylları bu maşında açılmayacaq.' -ForegroundColor Yellow
}

Write-Host "Kiosk işə salındı (PID $($started.Id)): $($exe.FullName)" -ForegroundColor Green
Write-Host "Qurulma vaxtı: $($exe.LastWriteTime)"
if (-not $ApiUrl) { Write-Host 'API: appsettings.Development.json (http://localhost:5080)' }
Write-Host 'Dayandırmaq: tools\dev-kiosk\stop-kiosk.ps1'
