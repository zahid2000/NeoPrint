# ---------------------------------------------------------------------------
# NeoPrint dev-kiosk: "scans" a user's personal QR at the running dev kiosk,
# as a USB HID scanner would (plan section 9.4; docs/DEV_ENVIRONMENT.md 7d).
#
#   powershell -ExecutionPolicy Bypass -File tools\dev-kiosk\scan-user-qr.ps1
#   ... scan-user-qr.ps1 -Email dev-user@neoprint.local -Password DevTest!2026
#   ... scan-user-qr.ps1 -ApiUrl http://192.168.1.50:5080
#
# Signs in as the user, fetches a fresh code (GET users/me/qr), brings the
# kiosk window to the front and types the code with Enter in one go - fast
# enough for the kiosk to take it for a scanner (under 50 ms per character).
# Development only: the password is the seeded test user's.
# ---------------------------------------------------------------------------
param(
    [string]$Email = 'dev-user@neoprint.local',
    [string]$Password = 'DevTest!2026',
    [string]$ApiUrl = 'http://localhost:5080'
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$login = Invoke-RestMethod -Method Post -Uri "$ApiUrl/api/auth/login" -ContentType 'application/json' `
    -Body (@{ email = $Email; password = $Password } | ConvertTo-Json)
$qr = Invoke-RestMethod -Uri "$ApiUrl/api/users/me/qr" -Headers @{ Authorization = "Bearer $($login.accessToken)" }

# The kiosk process itself, never a window that merely has "NeoPrint" in its title
# (an editor open on this repository has): the keys must reach the kiosk only.
$kiosk = Get-Process -Name 'NeoPrint.Kiosk' -ErrorAction SilentlyContinue |
    Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1

$shell = New-Object -ComObject WScript.Shell
if ($null -eq $kiosk -or -not $shell.AppActivate($kiosk.Id)) {
    Write-Host 'Kiosk pəncərəsi tapılmadı. Əvvəl tools\dev-kiosk\start-kiosk.ps1 ilə kiosku işə salın.' -ForegroundColor Red
    exit 1
}

Start-Sleep -Milliseconds 300

# The code is NPU:<hex>:<digits>:<base64url> - none of SendKeys' special characters.
$shell.SendKeys("$($qr.code){ENTER}")

Write-Host "QR oxudu: $($qr.code.Substring(0, 4))... (etibarlıdır: $($qr.rotationSeconds) san)"
