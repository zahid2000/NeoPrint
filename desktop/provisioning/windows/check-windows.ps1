<#
.SYNOPSIS
    Is this computer ready to be a NeoPrint kiosk? (T-329, docs/KIOSK_SETUP.md, the first check on a new computer)

.DESCRIPTION
    Read-only: it changes nothing. Run it on a new kiosk computer before buying
    a licence or installing anything, and send the result to the NeoPrint team:

        powershell -ExecutionPolicy Bypass -File D:\provisioning\check-windows.ps1

    It reports the Windows edition and build, whether Shell Launcher and
    Keyboard Filter exist, the processor (and whether it can run Windows 11),
    memory, disk, screen, touch, network, TPM, Secure Boot and activation, and
    ends with one line: RESULT: SUITABLE or RESULT: NOT SUITABLE and why.
    Plain ASCII on purpose: it reads the same in any console.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot "edition.ps1")

$problems = New-Object System.Collections.Generic.List[string]
function Line([string] $Label, [string] $Value) { Write-Host ("{0,-22} {1}" -f $Label, $Value) }
function Safe([scriptblock] $Read, [string] $Otherwise = "unknown") {
    try { $value = & $Read; if ($null -eq $value -or "$value" -eq "") { $Otherwise } else { $value } } catch { $Otherwise }
}

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)

Write-Host "NeoPrint kiosk check - $(Get-Date -Format 'yyyy-MM-dd HH:mm')"
Write-Host ""

# --- Windows -----------------------------------------------------------------
$version = Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion"
$edition = if ($admin) { Get-NeoPrintEditionOfThisMachine } else { Get-NeoPrintEditionVerdict -EditionId ([string]$version.EditionID) -Build ([int]$version.CurrentBuild) }
$windows11 = [int]$version.CurrentBuild -ge 22000
Line "Windows" ("{0} {1} (build {2}.{3})" -f $(if ($windows11) { "Windows 11" } else { "Windows 10" }), $version.EditionID, $version.CurrentBuild, (Safe { $version.UBR } "0"))
Line "LTSC" $(if ($edition.Ltsc) { "yes - supported for 10 years from release" } else { "no" })
Line "Kiosk edition" $(if ($edition.Supported) { "yes" } else { "NO - " + $edition.Reason })
if (-not $admin) {
    Line "" "(Shell Launcher and Keyboard Filter not looked at: run as administrator)"
}
if (-not $edition.Supported) {
    $problems.Add("Windows: " + $edition.Reason + " " + $edition.Advice)
}

$activation = Safe {
    $licence = Get-CimInstance SoftwareLicensingProduct -Filter "PartialProductKey IS NOT NULL AND ApplicationID='55c92734-d682-4d71-983e-d6ec3f16059f'" |
        Select-Object -First 1
    if ($licence.LicenseStatus -eq 1) { "activated" } else { "NOT activated (status $($licence.LicenseStatus))" }
}
Line "Activation" $activation

# --- Hardware ----------------------------------------------------------------
$cpu = (Get-CimInstance Win32_Processor | Select-Object -First 1).Name.Trim()
Line "Processor" $cpu
$generation = if ($cpu -match 'Core\(TM\) i\d-(\d{4,5})') {
    $model = $Matches[1]
    if ($model.Length -eq 5) { [int]$model.Substring(0, 2) } else { [int]$model.Substring(0, 1) }
}
else { $null }
if ($null -ne $generation) {
    Line "Intel generation" ("{0} - Windows 11 {1}" -f $generation, $(if ($generation -ge 8) { "is supported" } else { "is NOT supported (8th generation or later); use Windows 10 IoT Enterprise LTSC 2021" }))
    if ($generation -lt 8 -and $windows11) {
        $problems.Add("Windows 11 runs on a processor it does not support: it may get no updates. Install Windows 10 IoT Enterprise LTSC 2021.")
    }
}

$memory = [math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB)
Line "Memory" ("{0} GB {1}" -f $memory, $(if ($memory -ge 8) { "" } else { "- LESS than the 8 GB a kiosk needs" }))
if ($memory -lt 8) { $problems.Add("Memory: $memory GB; a kiosk needs 8 GB.") }

$system = Get-CimInstance Win32_LogicalDisk -Filter "DeviceID='$($env:SystemDrive)'"
$sizeGb = [math]::Round($system.Size / 1GB)
$freeGb = [math]::Round($system.FreeSpace / 1GB)
Line "System disk" ("{0} GB, {1} GB free{2}" -f $sizeGb, $freeGb, $(if ($freeGb -lt 40) { " - LESS than the 40 GB free a kiosk needs" } else { "" }))
if ($freeGb -lt 40) { $problems.Add("Disk: $freeGb GB free; a kiosk needs 40 GB free (updates keep a previous version).") }

$screens = Safe {
    (Get-CimInstance Win32_VideoController | Where-Object { $_.CurrentHorizontalResolution } |
        ForEach-Object { "{0}x{1}" -f $_.CurrentHorizontalResolution, $_.CurrentVerticalResolution }) -join ", "
}
Line "Screen" $screens

$touch = Safe {
    $found = Get-PnpDevice -Class HIDClass -PresentOnly -ErrorAction Stop | Where-Object FriendlyName -match "touch screen"
    if ($found) { "yes (" + (($found | Select-Object -First 1).FriendlyName) + ")" } else { "NO touch screen found" }
}
Line "Touch" $touch
if ($touch -like "NO*") { $problems.Add("Touch: Windows does not see a touch screen; check its USB cable and driver.") }

$network = Safe {
    (Get-NetAdapter -Physical -ErrorAction Stop | ForEach-Object { "{0} ({1}, {2})" -f $_.Name, $_.MediaType, $_.Status }) -join "; "
}
Line "Network" $network

$tpm = if ($admin) {
    Safe {
        $chip = Get-CimInstance -Namespace "root\cimv2\security\microsofttpm" -ClassName Win32_Tpm -ErrorAction Stop
        if ($chip) { "yes, version " + ($chip.SpecVersion -split ",")[0] } else { "none" }
    } "none"
}
else { "not looked at (run as administrator)" }
Line "TPM" $tpm

$secureBoot = if ($admin) { Safe { if (Confirm-SecureBootUEFI -ErrorAction Stop) { "on" } else { "off" } } "not available (legacy BIOS)" } else { "not looked at (run as administrator)" }
Line "Secure Boot" $secureBoot

Write-Host ""
if ($problems.Count -eq 0) {
    Write-Host "RESULT: SUITABLE - this computer can be set up as a NeoPrint kiosk (docs/KIOSK_SETUP.md)."
    exit 0
}

Write-Host "RESULT: NOT SUITABLE"
foreach ($problem in $problems) {
    Write-Host "  - $problem"
}
exit 1
