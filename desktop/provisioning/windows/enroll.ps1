<#
.SYNOPSIS
    Gives a Windows kiosk its enrollment token after it was copied from a
    golden image (T-327, docs/KIOSK_SETUP.md).

.DESCRIPTION
    The image carries no token; each clone is given one - the same one for
    every clone when it was made for several kiosks - and activates itself
    at its next start, with nobody reading its screen. The token is asked for
    and not shown: an argument would stay in the console's history.

        powershell -ExecutionPolicy Bypass -File D:\provisioning\enroll.ps1
        powershell -ExecutionPolicy Bypass -File D:\provisioning\enroll.ps1 -FromFile D:\token.txt

    The kiosk deletes the token once it is activated. Restart the kiosk
    afterwards: it registers on every start.
#>
[CmdletBinding()]
param(
    [string] $FromFile = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$DataRoot  = "C:\ProgramData\NeoPrint"
$KioskUser = "kiosk"

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run it as Administrator."
}

if ($FromFile) {
    $token = (Get-Content -Path $FromFile -Raw).Trim()
}
else {
    $secure = Read-Host -Prompt "Enrollment token" -AsSecureString
    $token = [System.Net.NetworkCredential]::new("", $secure).Password.Trim()
}

# What the panel shows: 64 hexadecimal characters.
if ($token -notmatch '^[0-9a-f]{64}$') {
    throw "That is not an enrollment token - it is 64 characters, 0-9 and a-f. Copy it again from the panel."
}

$device = Join-Path $DataRoot "device"
$tokenPath = Join-Path $device "enrollment-token"

# The device folder is SYSTEM's and the kiosk account's only (provision.ps1):
# Administrators take a moment's access to write the token, and give it up.
& takeown /f $device /a | Out-Null
& icacls $device /grant "Administrators:(OI)(CI)F" /C /Q | Out-Null
try {
    Set-Content -Path $tokenPath -Value $token -NoNewline -Encoding ascii
    & icacls $tokenPath /inheritance:r /grant:r "SYSTEM:F" /grant:r "${KioskUser}:M" /C /Q | Out-Null
}
finally {
    & icacls $device /remove:g "Administrators" /C /Q | Out-Null
}

Write-Host "The token is in place. Restart the kiosk: it activates itself at its next start and the panel shows it."
