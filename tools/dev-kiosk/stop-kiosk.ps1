# ---------------------------------------------------------------------------
# NeoPrint dev-kiosk: stops every running kiosk app on this machine
# (docs/DEV_ENVIRONMENT.md, section 5).
#
#   powershell -ExecutionPolicy Bypass -File tools\dev-kiosk\stop-kiosk.ps1
#
# An old kiosk left running locks its DLLs, and the next build fails with
# MSB3027 "The file is locked by: NeoPrint.Kiosk". The app is asked to close
# first; only one that does not close in 5 seconds is killed, because a kill
# counts as a crash and three in ten minutes open the safe mode screen.
# ---------------------------------------------------------------------------
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$running = @(Get-Process -Name 'NeoPrint.Kiosk' -ErrorAction SilentlyContinue)

if ($running.Count -eq 0) {
    Write-Host 'İşləyən kiosk yoxdur.'
    exit 0
}

foreach ($process in $running) {
    Write-Host "Kiosk bağlanır (PID $($process.Id))..."
    [void]$process.CloseMainWindow()
}

$deadline = (Get-Date).AddSeconds(5)
while ((Get-Date) -lt $deadline -and @(Get-Process -Name 'NeoPrint.Kiosk' -ErrorAction SilentlyContinue).Count -gt 0) {
    Start-Sleep -Milliseconds 250
}

$left = @(Get-Process -Name 'NeoPrint.Kiosk' -ErrorAction SilentlyContinue)
foreach ($process in $left) {
    Write-Host "PID $($process.Id) 5 saniyədə bağlanmadı, dayandırılır (çöküş kimi sayılır)." -ForegroundColor Yellow
    Stop-Process -Id $process.Id -Force
}

Write-Host 'Kiosk dayandırıldı.' -ForegroundColor Green
