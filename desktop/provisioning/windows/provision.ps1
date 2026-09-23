<#
.SYNOPSIS
    NeoPrint kiosk provisioning for Windows (plan section 7.12).

.DESCRIPTION
    Turns a Windows 11 IoT Enterprise LTSC (or Enterprise) install into a
    kiosk: creates the kiosk account, makes the NeoPrint UI its shell through
    Shell Launcher, blocks the escape keys with Keyboard Filter, silences every
    notification source, and installs the NeoPrint agent.

    IDEMPOTENT: running it again only corrects what has drifted, which is what
    makes the remote ReapplyProvisioning command safe.

    Nothing here identifies a kiosk. -EnrollmentToken is the one optional
    per-kiosk input, it is single use, and the kiosk's own parameters all come
    from the backend after activation (plan section 7.11).

    Every change this script makes is listed in docs/KIOSK_PROVISIONING.md.

.PARAMETER ApiUrl
    Backend address for this environment. Identifies the environment, not the
    kiosk, and is not a secret.

.PARAMETER EnrollmentToken
    Single-use token for zero-touch activation.

.PARAMETER Channel
    Release channel: pilot or stable.

.PARAMETER OfflinePackage
    Install from a local package instead of downloading.

.EXAMPLE
    .\provision.ps1 -ApiUrl https://api.neoprint.az -EnrollmentToken abc123
#>
[CmdletBinding()]
param(
    [string] $ApiUrl = "",
    [string] $EnrollmentToken = "",
    [ValidateSet("pilot", "stable")]
    [string] $Channel = "stable",
    [string] $OfflinePackage = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$ProvisioningVersion = "0.1.0"

$InstallRoot = "C:\Program Files\NeoPrint"
$DataRoot    = "C:\ProgramData\NeoPrint"
$LogFile     = Join-Path $DataRoot "provision.log"
$KioskUser   = "kiosk"

# ---------------------------------------------------------------------------
# Logging
# ---------------------------------------------------------------------------
function Write-Log {
    param([Parameter(Mandatory)][string] $Message)

    $line = "[provision {0}] {1}" -f (Get-Date -Format "yyyy-MM-ddTHH:mm:ssZ"), $Message
    Write-Host $line

    if (Test-Path (Split-Path $LogFile)) {
        Add-Content -Path $LogFile -Value $line -Encoding utf8
    }
}

function Assert-Administrator {
    $identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)

    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "This script must run as Administrator."
    }
}

# ---------------------------------------------------------------------------
# Edition check
#
# Shell Launcher and Keyboard Filter exist only on IoT Enterprise and
# Enterprise. Without them the screen cannot be locked down, so Pro is refused
# outright rather than producing a kiosk that a customer can escape from
# (plan section 2.1).
# ---------------------------------------------------------------------------
function Assert-SupportedEdition {
    $edition = (Get-ComputerInfo).WindowsProductName

    Write-Log "Windows edition: $edition"

    if ($edition -notmatch "Enterprise|IoT") {
        throw @"
This Windows edition ($edition) is not supported.

NeoPrint needs Shell Launcher and Keyboard Filter, which are only available on
Windows 11 IoT Enterprise LTSC and Windows 11 Enterprise. Windows Pro cannot
lock the screen down and is not supported (plan section 2.1).
"@
    }
}

# ---------------------------------------------------------------------------
# Registry helper: idempotent by construction
# ---------------------------------------------------------------------------
function Set-RegistryValue {
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][string] $Name,
        [Parameter(Mandatory)] $Value,
        [string] $Type = "DWord"
    )

    if (-not (Test-Path $Path)) {
        New-Item -Path $Path -Force | Out-Null
    }

    New-ItemProperty -Path $Path -Name $Name -Value $Value -PropertyType $Type -Force | Out-Null
}

# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------
Assert-Administrator

New-Item -ItemType Directory -Path $DataRoot -Force | Out-Null

Write-Log "NeoPrint kiosk provisioning $ProvisioningVersion starting (channel: $Channel)"

Assert-SupportedEdition

# --- Directories -----------------------------------------------------------
Write-Log "creating directories"

foreach ($directory in @("device", "spool", "state", "logs")) {
    New-Item -ItemType Directory -Path (Join-Path $DataRoot $directory) -Force | Out-Null
}

New-Item -ItemType Directory -Path $InstallRoot -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $InstallRoot "versions") -Force | Out-Null

# %ProgramData% is world-readable by default, and the spool holds customer
# documents (plan section 2.13).
Write-Log "restricting access to the data directory"
& icacls $DataRoot /inheritance:r /grant:r "SYSTEM:(OI)(CI)F" /grant:r "Administrators:(OI)(CI)F" /T /C /Q | Out-Null

# --- Kiosk account ---------------------------------------------------------
Write-Log "creating the kiosk account"

if (-not (Get-LocalUser -Name $KioskUser -ErrorAction SilentlyContinue)) {
    # A random password nobody needs to know: the account signs in
    # automatically, and the password is stored as an LSA secret rather than in
    # the registry in clear text.
    $bytes = New-Object byte[] 32
    [Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    $password = [Convert]::ToBase64String($bytes)

    New-LocalUser -Name $KioskUser `
        -Password (ConvertTo-SecureString $password -AsPlainText -Force) `
        -PasswordNeverExpires `
        -UserMayNotChangePassword `
        -AccountNeverExpires `
        -Description "NeoPrint kiosk UI account" | Out-Null

    Add-LocalGroupMember -Group "Users" -Member $KioskUser -ErrorAction SilentlyContinue

    Write-Log "NOTE: storing the autologon password as an LSA secret is completed with the full lockdown in step 3"
}

& icacls $DataRoot /grant "${KioskUser}:(OI)(CI)M" /T /C /Q | Out-Null

# --- Enrollment token ------------------------------------------------------
if ($EnrollmentToken) {
    Write-Log "writing the enrollment token for zero-touch activation"

    $tokenPath = Join-Path $DataRoot "device\enrollment-token"
    Set-Content -Path $tokenPath -Value $EnrollmentToken -NoNewline -Encoding utf8
    & icacls $tokenPath /inheritance:r /grant:r "SYSTEM:F" /grant:r "Administrators:F" /grant:r "${KioskUser}:R" /C /Q | Out-Null
}

# --- Notifications ---------------------------------------------------------
#
# Everything that could put something on the screen in front of a customer.
# Defender protection stays on; only its notifications are hidden.
Write-Log "silencing every notification source"

$policies = "HKLM:\SOFTWARE\Policies\Microsoft\Windows"

Set-RegistryValue "$policies\Explorer" "DisableNotificationCenter" 1
Set-RegistryValue "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\PushNotifications" "ToastEnabled" 0
Set-RegistryValue "$policies\CloudContent" "DisableWindowsConsumerFeatures" 1
Set-RegistryValue "$policies\CloudContent" "DisableSoftLanding" 1
Set-RegistryValue "$policies\CloudContent" "DisableWindowsSpotlightFeatures" 1
Set-RegistryValue "$policies\Windows Search" "AllowCortana" 0
Set-RegistryValue "$policies\WindowsCopilot" "TurnOffWindowsCopilot" 1
Set-RegistryValue "$policies\Dsh" "AllowNewsAndInterests" 0

# Error reporting dialogs.
Set-RegistryValue "$policies\Windows Error Reporting" "DontShowUI" 1
Set-RegistryValue "$policies\Windows Error Reporting" "Disabled" 1

# "Low disk space" and "new device installed" balloons.
Set-RegistryValue "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer" "NoLowDiskSpaceChecks" 1
Set-RegistryValue "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer" "NoBalloonFeatureAdvertisements" 1

# Defender notifications hidden; the protection itself is untouched.
Set-RegistryValue "$policies\Windows Defender Security Center\Notifications" "DisableNotifications" 1

# --- AutoPlay --------------------------------------------------------------
#
# The kiosk handles USB sticks itself; an AutoPlay dialog would sit on top of it.
Write-Log "disabling AutoPlay"
Set-RegistryValue "$policies\Explorer" "NoDriveTypeAutoRun" 255
Set-RegistryValue "$policies\Explorer" "NoAutorun" 1

# --- Touch and display -----------------------------------------------------
Write-Log "disabling edge gestures, the lock screen and power saving"

Set-RegistryValue "HKLM:\SOFTWARE\Policies\Microsoft\Windows\EdgeUI" "AllowEdgeSwipe" 0
Set-RegistryValue "HKLM:\SOFTWARE\Policies\Microsoft\Windows\Personalization" "NoLockScreen" 1
Set-RegistryValue "HKLM:\SOFTWARE\Microsoft\TabletTip\1.7" "EnableDesktopModeAutoInvoke" 0

# A kiosk that goes to sleep is a kiosk that looks broken.
& powercfg /setactive SCHEME_MIN | Out-Null
& powercfg /change monitor-timeout-ac 0 | Out-Null
& powercfg /change standby-timeout-ac 0 | Out-Null
& powercfg /change hibernate-timeout-ac 0 | Out-Null
& powercfg /hibernate off | Out-Null

# --- Windows Update --------------------------------------------------------
#
# Updates install, but the reboot is the agent's decision: it waits for the
# update window and an empty queue (plan section 7.12).
Write-Log "stopping Windows Update from rebooting on its own"

Set-RegistryValue "$policies\WindowsUpdate\AU" "NoAutoRebootWithLoggedOnUsers" 1
Set-RegistryValue "$policies\WindowsUpdate\AU" "AUOptions" 3

# --- Printing --------------------------------------------------------------
#
# The active printer is chosen from the admin panel, so Windows must not move
# it (plan section 7.7).
Write-Log "stopping Windows from managing the default printer"
Set-RegistryValue "HKLM:\SOFTWARE\Policies\Microsoft\Windows NT\Printers" "LegacyDefaultPrinterMode" 1

# --- Time ------------------------------------------------------------------
Write-Log "setting the time zone to Azerbaijan Standard Time"
& tzutil /s "Azerbaijan Standard Time"
& w32tm /resync | Out-Null

# --- Firewall --------------------------------------------------------------
Write-Log "blocking all inbound connections"
& netsh advfirewall set allprofiles firewallpolicy blockinbound,allowoutbound | Out-Null

# --- Still to come ---------------------------------------------------------
Write-Log "NOTE: Shell Launcher, Keyboard Filter, autologon and the silent dependency"
Write-Log "      installs (LibreOffice, Ghostscript, fonts) are completed with the full"
Write-Log "      lockdown in step 3 (plan section 12, step 3)"

if ($ApiUrl) {
    Write-Log "recording the API address for this environment"
    [Environment]::SetEnvironmentVariable("NEOPRINT_KIOSK_Kiosk__ApiBaseUrl", $ApiUrl, "Machine")
}

Set-Content -Path (Join-Path $DataRoot "state\provisioning-version") `
    -Value $ProvisioningVersion -NoNewline -Encoding utf8

Write-Log "provisioning finished; a restart is required to enter kiosk mode"
Write-Log "log written to $LogFile"
