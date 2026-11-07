<#
.SYNOPSIS
    Sets this Windows kiosk's service PIN (T-334, docs/KIOSK_SETUP.md).

.DESCRIPTION
    The way into the kiosk's hidden admin menu - five taps on the logo - that
    works before the kiosk is activated and while it cannot reach the server.
    Asked for twice and not shown; never an argument. The agent keeps only a
    PBKDF2 hash in C:\ProgramData\NeoPrint\service-pin, which Administrators
    and SYSTEM may change and the kiosk account may only read. Each machine
    has its own; a golden image leaves it out (prepare-image.ps1).

        powershell -ExecutionPolicy Bypass -File D:\provisioning\service-pin.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$AgentPath = "C:\Program Files\NeoPrint\agent\current\NeoPrint.Kiosk.Agent.exe"
$PinFile   = "C:\ProgramData\NeoPrint\service-pin"
$KioskUser = "kiosk"

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run it as Administrator."
}
if (-not (Test-Path $AgentPath)) {
    throw "NeoPrint is not installed here yet - run provision.ps1 first."
}

function Read-Secret([string] $Prompt) {
    $secure = Read-Host -Prompt $Prompt -AsSecureString
    return [System.Net.NetworkCredential]::new("", $secure).Password
}

foreach ($attempt in 1..3) {
    $pin = Read-Secret "Service PIN for this kiosk (6-12 digits)"
    $again = Read-Secret "The same again"
    if ($pin -cne $again) { Write-Warning "The two were not the same; again."; continue }
    if ($pin -notmatch '^[0-9]{6,12}$') { Write-Warning "6 to 12 digits, nothing else; again."; continue }

    $pin | & $AgentPath set-service-pin $PinFile | Out-Null
    if ($LASTEXITCODE) { throw "The agent could not set the service PIN (exit $LASTEXITCODE)." }
    $pin = $null; $again = $null

    # Administrators and SYSTEM change it, the kiosk account reads it, nobody else.
    $acl = New-Object System.Security.AccessControl.FileSecurity
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($sid in @("S-1-5-18", "S-1-5-32-544")) {
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule((New-Object System.Security.Principal.SecurityIdentifier($sid)), "FullControl", "Allow")))
    }
    $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($KioskUser, "Read", "Allow")))
    Set-Acl -Path $PinFile -AclObject $acl

    Write-Host "Service PIN set. Keep it somewhere safe: five taps on the NeoPrint logo, then this PIN, opens the service menu."
    exit 0
}

throw "Service PIN not set. Run service-pin.ps1 again."
