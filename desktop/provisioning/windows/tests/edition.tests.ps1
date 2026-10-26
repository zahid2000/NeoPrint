<#
.SYNOPSIS
    Checks the edition decision of edition.ps1 (T-329). Plain PowerShell, so it
    runs the same under Windows PowerShell 5.1 and PowerShell 7 with no module:

        pwsh -NoProfile -File desktop\provisioning\windows\tests\edition.tests.ps1

    Exit code 0 when every check passes.
#>
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "..\edition.ps1")

$failures = 0
function Check([bool] $Condition, [string] $Text) {
    if ($Condition) { Write-Host "ok     $Text" } else { Write-Host "WRONG  $Text"; $script:failures++ }
}

$iot = Get-NeoPrintEditionVerdict -EditionId "IoTEnterpriseS" -Build 19044
Check ($iot.Supported -and $iot.Ltsc) "Windows 10 IoT Enterprise LTSC 2021 is a kiosk, and an LTSC one"

$enterprise = Get-NeoPrintEditionVerdict -EditionId "Enterprise" -Build 22631
Check ($enterprise.Supported -and -not $enterprise.Ltsc) "Windows 11 Enterprise is a kiosk"

Check (Get-NeoPrintEditionVerdict -EditionId "Education" -Build 22631).Supported "Education has the features too"

$pro = Get-NeoPrintEditionVerdict -EditionId "Professional" -Build 22631
Check (-not $pro.Supported) "Pro is refused"
Check ($pro.Reason -match "neither Shell Launcher nor Keyboard Filter") "and says why"
Check ($pro.Advice -match "IoT Enterprise LTSC 2021" -and $pro.Advice -match "Assigned Access") "and what to do, with the fallback for the owner to decide"

Check (-not (Get-NeoPrintEditionVerdict -EditionId "Core" -Build 22631).Supported) "Home is refused"
Check (-not (Get-NeoPrintEditionVerdict -EditionId "" -Build 22631).Supported) "an edition that cannot be read is refused"

$old = Get-NeoPrintEditionVerdict -EditionId "EnterpriseS" -Build 14393
Check (-not $old.Supported -and $old.Reason -match "1809") "LTSB 2016 is too old for Shell Launcher v2"

$broken = Get-NeoPrintEditionVerdict -EditionId "IoTEnterpriseS" -Build 19044 -KeyboardFilterAvailable $false
Check (-not $broken.Supported -and $broken.Reason -match "Keyboard Filter") "the right edition without Keyboard Filter is refused, naming what is missing"

if ($failures -gt 0) {
    Write-Host "$failures check(s) failed."
    exit 1
}

Write-Host "Every check passed."
