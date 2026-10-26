<#
.SYNOPSIS
    Whether a Windows installation can be a NeoPrint kiosk (T-329).

.DESCRIPTION
    Shell Launcher v2 (the kiosk as the only shell) and Keyboard Filter (no
    Ctrl+Alt+Del, no Windows key) exist only on the Enterprise, Education and
    IoT Enterprise editions, from Windows 10 version 1809 (build 17763) on.
    Without them a customer can leave the kiosk application, so every other
    edition - Pro first of all - is refused, and says why and what to do.

    The edition is read from EditionID, not ProductName: Windows 11 still
    calls itself "Windows 10 ..." in ProductName.

    Dot-sourced by provision.ps1 and check-windows.ps1; the decision itself is
    a plain function with no side effects, checked by tests\edition.tests.ps1.
#>

$script:KioskEditions = @(
    "Enterprise", "EnterpriseN", "EnterpriseS", "EnterpriseSN",
    "Education", "EducationN",
    "IoTEnterprise", "IoTEnterpriseK", "IoTEnterpriseS", "IoTEnterpriseSK"
)

# Shell Launcher v2 first shipped in Windows 10 version 1809.
$script:MinimumKioskBuild = 17763

function Get-NeoPrintEditionVerdict {
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string] $EditionId,
        [Parameter(Mandatory)][int] $Build,
        [bool] $ShellLauncherAvailable = $true,
        [bool] $KeyboardFilterAvailable = $true
    )

    $ltsc = $EditionId -match 'S(N|K)?$' -and $EditionId -ne "Education"
    $verdict = [ordered]@{
        Supported = $false
        Edition   = $EditionId
        Build     = $Build
        Ltsc      = $ltsc
        Reason    = ""
        Advice    = ""
    }

    if ($script:KioskEditions -notcontains $EditionId) {
        if ($EditionId -like "Professional*") {
            $verdict.Reason = "Windows Pro has neither Shell Launcher nor Keyboard Filter: a customer could leave the kiosk application."
            $verdict.Advice = "Install Windows 10 IoT Enterprise LTSC 2021 (recommended), or upgrade this licence to Enterprise. Assigned Access, which Pro has, is a possible fallback that the project owner decides on (docs/KIOSK_SETUP.md)."
        }
        else {
            $verdict.Reason = "The edition '$EditionId' cannot lock the screen down."
            $verdict.Advice = "Install Windows 10 IoT Enterprise LTSC 2021 (docs/KIOSK_SETUP.md)."
        }

        return [pscustomobject]$verdict
    }

    if ($Build -lt $script:MinimumKioskBuild) {
        $verdict.Reason = "Build $Build is older than Windows 10 version 1809 (build $script:MinimumKioskBuild), which brought Shell Launcher v2."
        $verdict.Advice = "Install Windows 10 IoT Enterprise LTSC 2021 (build 19044)."
        return [pscustomobject]$verdict
    }

    if (-not $ShellLauncherAvailable -or -not $KeyboardFilterAvailable) {
        $missing = @(
            if (-not $ShellLauncherAvailable) { "Shell Launcher (Client-EmbeddedShellLauncher)" }
            if (-not $KeyboardFilterAvailable) { "Keyboard Filter (Client-KeyboardFilter)" }
        ) -join " and "
        $verdict.Reason = "The edition is right, but $missing cannot be found on this installation."
        $verdict.Advice = "Repair or reinstall Windows from the IoT Enterprise / Enterprise image; the features are part of it."
        return [pscustomobject]$verdict
    }

    $verdict.Supported = $true
    return [pscustomobject]$verdict
}

# This machine's edition, build and features, as the verdict wants them.
function Get-NeoPrintEditionOfThisMachine {
    $version = Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion"
    $features = @{}

    foreach ($name in @("Client-EmbeddedShellLauncher", "Client-KeyboardFilter")) {
        try {
            $features[$name] = $null -ne (Get-WindowsOptionalFeature -Online -FeatureName $name -ErrorAction Stop)
        }
        catch {
            $features[$name] = $false
        }
    }

    return Get-NeoPrintEditionVerdict `
        -EditionId ([string]$version.EditionID) `
        -Build ([int]$version.CurrentBuild) `
        -ShellLauncherAvailable $features["Client-EmbeddedShellLauncher"] `
        -KeyboardFilterAvailable $features["Client-KeyboardFilter"]
}
