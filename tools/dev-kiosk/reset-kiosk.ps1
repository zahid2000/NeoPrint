# ---------------------------------------------------------------------------
# NeoPrint dev-kiosk: takes this machine's kiosk back to the activation
# screen (docs/DEV_ENVIRONMENT.md, section 5). Run from an elevated PowerShell:
#
#   powershell -ExecutionPolicy Bypass -File tools\dev-kiosk\reset-kiosk.ps1
#   ... reset-kiosk.ps1 -SafeModeOnly      # only clear the crash counter
#   ... reset-kiosk.ps1 -DeleteDeviceKey   # a brand new device identity too
#
# Development machine only: it deletes %ProgramData%\NeoPrint - the device
# identity, the configuration cache, the encrypted spool and the job journal.
# Jobs in that spool are lost. In the admin panel "Devices -> Unpair" is the
# gentler way: the kiosk returns to activation on its own.
# ---------------------------------------------------------------------------
param(
    [switch]$SafeModeOnly,
    [switch]$DeleteDeviceKey
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host 'PowerShell administrator kimi açılmayıb.' -ForegroundColor Red
    Write-Host 'Kiosk qovluğu yalnız SYSTEM və administratorlara açıqdır. "Run as administrator" ilə açıb yenidən işlədin.'
    exit 1
}

& (Join-Path $PSScriptRoot 'stop-kiosk.ps1')

$data = Join-Path $env:ProgramData 'NeoPrint'

if ($SafeModeOnly) {
    Remove-Item (Join-Path $data 'state\runs.json') -ErrorAction SilentlyContinue
    Write-Host 'Çöküş sayğacı silindi: növbəti açılış təhlükəsiz rejimsiz olacaq.' -ForegroundColor Green
    exit 0
}

if (Test-Path $data) {
    Remove-Item -Recurse -Force $data
    Write-Host "$data silindi: cihaz kimliyi, keş, spool, jurnal." -ForegroundColor Green
} else {
    Write-Host "$data artıq yoxdur."
}

if ($DeleteDeviceKey) {
    $options = [Security.Cryptography.CngKeyOpenOptions]::MachineKey
    $removed = $false
    foreach ($name in 'Microsoft Platform Crypto Provider', 'Microsoft Software Key Storage Provider') {
        $provider = New-Object Security.Cryptography.CngProvider $name
        try {
            if ([Security.Cryptography.CngKey]::Exists('NeoPrint.Device', $provider, $options)) {
                [Security.Cryptography.CngKey]::Open('NeoPrint.Device', $provider, $options).Delete()
                Write-Host "Cihaz açarı silindi ($name)." -ForegroundColor Green
                $removed = $true
            }
        } catch {
            Write-Host "$name yoxlanıla bilmədi: $($_.Exception.Message)" -ForegroundColor Yellow
        }
    }
    if (-not $removed) { Write-Host 'Cihaz açarı tapılmadı.' }
}

Write-Host 'Növbəti açılışda kiosk aktivləşdirmə ekranı göstərəcək (və ya -EnrollmentToken ilə dərhal aktivləşəcək).'
Write-Host 'Admin paneldə köhnə cihaz qeydi qalır: Cihazlar → Cihazı ayır.'
