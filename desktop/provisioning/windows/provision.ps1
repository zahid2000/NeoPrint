<#
.SYNOPSIS
    NeoPrint kiosk provisioning for Windows (plan section 7.12).

.DESCRIPTION
    Turns a Windows 10 IoT Enterprise LTSC 2021 (the pilot's target, T-329),
    Windows 11 IoT Enterprise LTSC or an Enterprise / Education install into a
    kiosk: creates the kiosk account with automatic sign-in, makes the NeoPrint
    UI its shell through Shell Launcher v2, blocks the escape keys with
    Keyboard Filter, silences every notification source, installs the
    converters and the NeoPrint agent, and closes the network down to what a
    kiosk needs - with no network at all for the document converters.

    IDEMPOTENT: running it again only corrects what has drifted, which is what
    makes the remote ReapplyProvisioning command safe.

    Enabling Shell Launcher and Keyboard Filter the first time needs a restart
    before they can be configured: run the script, restart, run it again. The
    script says so when it happens.

    A step that fails says which of the thirteen it was and what to do
    (docs/KIOSK_SETUP.md has the same steps in Azerbaijani); the registry
    values it had changed in this run are put back, and the firewall's
    defaults too. Every step can run again: remove the cause and start the
    script once more (T-327). Run check-windows.ps1 first on a new computer.

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
    The NeoPrint package: a folder or a .zip with app\ and agent\ (the UI and
    the agent, published) and VERSION, on the setup stick (docs/KIOSK_SETUP.md).

.PARAMETER Download
    Instead of -OfflinePackage: the newest published UI and agent from the
    backend (GET api/releases/latest, needs -ApiUrl), each checked by the agent
    on the setup stick (-Verifier, default ..\verifier\NeoPrint.Kiosk.Agent.exe)
    with the release keys built into it before anything is installed (T-327).
    The backend is a place to fetch from, not a source of trust.

.PARAMETER Verifier
    The checker for -Download: an agent build carrying the production release keys.

.PARAMETER DependenciesPath
    A folder with the converter installers: LibreOffice_*_x64.msi and
    gs*w64.exe (docs/KIOSK_SETUP.md). What is already installed is skipped.

.EXAMPLE
    .\provision.ps1 -ApiUrl https://api.neoprint.az -OfflinePackage D:\neoprint -DependenciesPath D:\deps
#>
[CmdletBinding()]
param(
    [string] $ApiUrl = "",
    [string] $EnrollmentToken = "",
    [ValidateSet("pilot", "stable")]
    [string] $Channel = "stable",
    [string] $OfflinePackage = "",
    [switch] $Download,
    [string] $Verifier = "",
    [string] $DependenciesPath = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$ProvisioningVersion = "0.5.0"

$InstallRoot = "C:\Program Files\NeoPrint"
$DataRoot    = "C:\ProgramData\NeoPrint"
$LogFile     = Join-Path $DataRoot "provision.log"
$KioskUser   = "kiosk"
$ShellPath   = Join-Path $InstallRoot "app\current\NeoPrint.Kiosk.exe"
$AgentPath   = Join-Path $InstallRoot "agent\current\NeoPrint.Kiosk.Agent.exe"
$AgentName   = "NeoPrintAgent"

# Where network printers live: the kiosk prints and reads page counters on
# the local network only.
$LocalNetworks = @("LocalSubnet", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16")

$script:RestartNeeded = $false

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
# Edition check (edition.ps1, T-329)
#
# Shell Launcher and Keyboard Filter exist only on the Enterprise, Education
# and IoT Enterprise editions. Without them the screen cannot be locked down,
# so Pro is refused outright rather than producing a kiosk that a customer
# can escape from (plan section 2.1) - with what to do instead.
# ---------------------------------------------------------------------------
. (Join-Path $PSScriptRoot "edition.ps1")

function Assert-SupportedEdition {
    $verdict = Get-NeoPrintEditionOfThisMachine

    Write-Log ("Windows edition: {0}, build {1}{2}" -f $verdict.Edition, $verdict.Build, $(if ($verdict.Ltsc) { " (LTSC)" } else { "" }))

    if (-not $verdict.Supported) {
        throw "This Windows cannot be a NeoPrint kiosk. $($verdict.Reason) $($verdict.Advice)"
    }
}

# ---------------------------------------------------------------------------
# Steps, and what to do when one fails
# ---------------------------------------------------------------------------
$script:Steps = 13
$script:Step = 0
$script:StepName = "starting"
$script:RegistryUndo = New-Object System.Collections.Generic.List[object]
$script:FirewallBefore = $null

function Set-Step([int] $Number, [string] $Name) {
    $script:Step = $Number
    $script:StepName = $Name
    Write-Log "step $Number/$($script:Steps): $Name"
}

# Whatever to do, said for each step (the same as the setup errors table in docs/KIOSK_SETUP.md).
function Get-StepHint([int] $Number) {
    switch ($Number) {
        1  { "this Windows cannot be a kiosk: see the reason above; check-windows.ps1 says the same." }
        2  { "a Windows feature could not be enabled: check the edition with check-windows.ps1 and that Windows Update is not installing something right now, then try again." }
        3  { "a folder could not be made: the disk may be full - see 'Get-PSDrive C'." }
        4  { "the kiosk account or its automatic sign-in could not be set up: the line above says which." }
        5  { "the data folder's access could not be set: another program may hold a file in it; restart and try again." }
        6  { "the enrollment token could not be written: the disk may be full." }
        7  { "a lockdown setting could not be written: the line above names it." }
        8  { "an update, printing or time setting could not be written: the line above names it." }
        9  { "a converter could not be installed: check the installers in the dependencies folder on the stick (docs/KIOSK_SETUP.md)." }
        10 { "NeoPrint could not be installed: the package is incomplete, could not be downloaded, or did not pass its signature check - check the network, or prepare the stick again (docs/KIOSK_SETUP.md)." }
        11 { "the agent service could not be set up: see 'Get-Service NeoPrintAgent'." }
        12 { "the firewall could not be set up: its defaults from before were put back; see 'Get-NetFirewallProfile'." }
        13 { "Shell Launcher or Keyboard Filter could not be configured: restart, then run the script again." }
        default { "see the lines above." }
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

    # What was there, to be put back if a step of this run fails.
    $before = if (Test-Path $Path) { Get-ItemProperty -Path $Path -Name $Name -ErrorAction SilentlyContinue } else { $null }
    $script:RegistryUndo.Add([pscustomobject]@{
        Path    = $Path
        Name    = $Name
        Existed = $null -ne $before
        Value   = if ($null -ne $before) { $before.$Name } else { $null }
        Kind    = if ($null -ne $before) { (Get-Item -Path $Path).GetValueKind($Name).ToString() } else { $null }
    })

    if (-not (Test-Path $Path)) {
        New-Item -Path $Path -Force | Out-Null
    }

    New-ItemProperty -Path $Path -Name $Name -Value $Value -PropertyType $Type -Force | Out-Null
}

# ---------------------------------------------------------------------------
# The autologon password as an LSA secret
#
# Windows reads "DefaultPassword" from the LSA secret store when it is not in
# the registry, so the password never sits in clear text on disk (plan
# section 7.12). This is the same call Sysinternals Autologon makes.
# ---------------------------------------------------------------------------
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

public static class NeoPrintLsa
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LsaUnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LsaObjectAttributes
    {
        public int Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [DllImport("advapi32.dll")]
    private static extern uint LsaOpenPolicy(IntPtr system, ref LsaObjectAttributes attributes, uint access, out IntPtr policy);

    [DllImport("advapi32.dll")]
    private static extern uint LsaStorePrivateData(IntPtr policy, ref LsaUnicodeString key, ref LsaUnicodeString data);

    [DllImport("advapi32.dll")]
    private static extern uint LsaClose(IntPtr policy);

    [DllImport("advapi32.dll")]
    private static extern uint LsaNtStatusToWinError(uint status);

    private static LsaUnicodeString Text(string value)
    {
        return new LsaUnicodeString
        {
            Buffer = Marshal.StringToHGlobalUni(value),
            Length = (ushort)(value.Length * 2),
            MaximumLength = (ushort)((value.Length + 1) * 2),
        };
    }

    public static void Store(string name, string value)
    {
        var attributes = new LsaObjectAttributes();
        IntPtr policy;

        // POLICY_CREATE_SECRET
        var status = LsaOpenPolicy(IntPtr.Zero, ref attributes, 0x00000020, out policy);
        if (status != 0)
        {
            throw new System.ComponentModel.Win32Exception((int)LsaNtStatusToWinError(status));
        }

        var key = Text(name);
        var data = Text(value);

        try
        {
            status = LsaStorePrivateData(policy, ref key, ref data);
            if (status != 0)
            {
                throw new System.ComponentModel.Win32Exception((int)LsaNtStatusToWinError(status));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(key.Buffer);
            Marshal.FreeHGlobal(data.Buffer);
            LsaClose(policy);
        }
    }
}
"@

function Enable-Feature {
    param([Parameter(Mandatory)][string] $Name)

    $feature = Get-WindowsOptionalFeature -Online -FeatureName $Name

    if ($feature.State -ne "Enabled") {
        Write-Log "enabling the Windows feature $Name"
        $result = Enable-WindowsOptionalFeature -Online -FeatureName $Name -All -NoRestart

        if ($result.RestartNeeded) {
            $script:RestartNeeded = $true
        }
    }
}

# Runs a script block as SYSTEM: the MDM bridge that configures Shell Launcher
# v2 only accepts that account.
function Invoke-AsSystem {
    param(
        [Parameter(Mandatory)][string] $ScriptPath,
        [Parameter(Mandatory)][string] $ResultPath
    )

    $taskName = "NeoPrintProvisionSystemStep"
    Remove-Item -Path $ResultPath -Force -ErrorAction SilentlyContinue

    $action    = New-ScheduledTaskAction -Execute "powershell.exe" -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$ScriptPath`""
    $principal = New-ScheduledTaskPrincipal -UserId "SYSTEM" -LogonType ServiceAccount -RunLevel Highest

    Register-ScheduledTask -TaskName $taskName -Action $action -Principal $principal -Force | Out-Null

    try {
        Start-ScheduledTask -TaskName $taskName

        $deadline = (Get-Date).AddMinutes(2)
        while (-not (Test-Path $ResultPath) -and (Get-Date) -lt $deadline) {
            Start-Sleep -Seconds 1
        }
    }
    finally {
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    }

    if (-not (Test-Path $ResultPath)) {
        throw "The SYSTEM step ($ScriptPath) did not finish."
    }

    $result = Get-Content $ResultPath -Raw
    Remove-Item -Path $ResultPath -Force -ErrorAction SilentlyContinue

    if ($result.Trim() -ne "OK") {
        throw "The SYSTEM step ($ScriptPath) failed: $result"
    }
}

# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------
Assert-Administrator

New-Item -ItemType Directory -Path $DataRoot -Force | Out-Null

Write-Log "NeoPrint kiosk provisioning $ProvisioningVersion starting (channel: $Channel)"

try {

Set-Step 1 "Windows edition"
Assert-SupportedEdition

# --- Features --------------------------------------------------------------
Set-Step 2 "Windows features"
Enable-Feature "Client-EmbeddedShellLauncher"
Enable-Feature "Client-KeyboardFilter"

# --- Directories -----------------------------------------------------------
Set-Step 3 "directories"

foreach ($directory in @("device", "spool", "state", "logs", "updates", "agent")) {
    New-Item -ItemType Directory -Path (Join-Path $DataRoot $directory) -Force | Out-Null
}

New-Item -ItemType Directory -Path $InstallRoot -Force | Out-Null
# Each component has its versions and a "current" junction (docs/KIOSK_UPDATES.md
# section 2, T-321); the agent moves them. Not checked on a real Windows kiosk yet (T-093).
foreach ($component in @("app", "agent", "provisioning")) {
    New-Item -ItemType Directory -Path (Join-Path $InstallRoot "$component\versions") -Force | Out-Null
}

# --- Kiosk account and automatic sign-in -----------------------------------
Set-Step 4 "kiosk account and automatic sign-in"

# A random password nobody needs to know, renewed on every run: the account
# signs in automatically, and the password lives only as an LSA secret.
$bytes = New-Object byte[] 32
[Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
$password = [Convert]::ToBase64String($bytes) + "aA1!"
$secure = ConvertTo-SecureString $password -AsPlainText -Force

if (-not (Get-LocalUser -Name $KioskUser -ErrorAction SilentlyContinue)) {
    New-LocalUser -Name $KioskUser `
        -Password $secure `
        -PasswordNeverExpires `
        -UserMayNotChangePassword `
        -AccountNeverExpires `
        -Description "NeoPrint kiosk UI account" | Out-Null
}
else {
    Set-LocalUser -Name $KioskUser -Password $secure -PasswordNeverExpires $true
}

# Standard rights only: never an administrator.
Add-LocalGroupMember -Group "Users" -Member $KioskUser -ErrorAction SilentlyContinue
Remove-LocalGroupMember -Group "Administrators" -Member $KioskUser -ErrorAction SilentlyContinue

[NeoPrintLsa]::Store("DefaultPassword", $password)
Remove-Variable password, secure, bytes

$winlogon = "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon"
Set-RegistryValue $winlogon "AutoAdminLogon" "1" "String"
Set-RegistryValue $winlogon "DefaultUserName" $KioskUser "String"
Set-RegistryValue $winlogon "DefaultDomainName" $env:COMPUTERNAME "String"
Remove-ItemProperty -Path $winlogon -Name "DefaultPassword" -ErrorAction SilentlyContinue

# --- Data directory access -------------------------------------------------
Set-Step 5 "data folder access"
#
# The spool holds customer documents and the device folder the kiosk's
# identity: SYSTEM and the kiosk account only (plan section 7.12).
Write-Log "restricting access to the data directory to SYSTEM and the kiosk account"
& icacls $DataRoot /inheritance:r /grant:r "SYSTEM:(OI)(CI)F" /grant:r "${KioskUser}:(OI)(CI)M" /T /C /Q | Out-Null
& icacls $DataRoot /remove:g "Administrators" "Users" "Authenticated Users" /T /C /Q | Out-Null

# --- Enrollment token ------------------------------------------------------
Set-Step 6 "enrollment token"
if ($EnrollmentToken) {
    Write-Log "writing the enrollment token for zero-touch activation"

    $tokenPath = Join-Path $DataRoot "device\enrollment-token"
    Set-Content -Path $tokenPath -Value $EnrollmentToken -NoNewline -Encoding utf8
    & icacls $tokenPath /inheritance:r /grant:r "SYSTEM:F" /grant:r "${KioskUser}:M" /C /Q | Out-Null
}

# --- Notifications ---------------------------------------------------------
Set-Step 7 "screen lockdown"
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
Set-RegistryValue "$policies\CloudContent" "DisableCloudOptimizedContent" 1
Set-RegistryValue "$policies\Windows Search" "AllowCortana" 0
Set-RegistryValue "$policies\WindowsCopilot" "TurnOffWindowsCopilot" 1
Set-RegistryValue "HKLM:\SOFTWARE\Policies\Microsoft\Dsh" "AllowNewsAndInterests" 0
Set-RegistryValue "$policies\OneDrive" "DisableFileSyncNGSC" 1
Set-RegistryValue "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Communications" "ConfigureChatAutoInstall" 0
Set-RegistryValue "$policies\Windows Chat" "ChatIcon" 3

# Error reporting dialogs.
Set-RegistryValue "$policies\Windows Error Reporting" "DontShowUI" 1
Set-RegistryValue "$policies\Windows Error Reporting" "Disabled" 1

# "Low disk space" and "new device installed" balloons.
Set-RegistryValue "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer" "NoLowDiskSpaceChecks" 1
Set-RegistryValue "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer" "NoBalloonFeatureAdvertisements" 1
Set-RegistryValue "$policies\DeviceInstall\Settings" "DisableBalloonTips" 1

# Defender notifications hidden; the protection itself is untouched.
Set-RegistryValue "$policies\Windows Defender Security Center\Notifications" "DisableNotifications" 1

# OneDrive is not part of a kiosk.
$oneDrive = Join-Path $env:SystemRoot "System32\OneDriveSetup.exe"
if (Test-Path $oneDrive) {
    Write-Log "removing OneDrive"
    Start-Process -FilePath $oneDrive -ArgumentList "/uninstall" -Wait -NoNewWindow -ErrorAction SilentlyContinue
}

# --- AutoPlay --------------------------------------------------------------
#
# The kiosk handles USB sticks itself; an AutoPlay dialog would sit on top of it.
Write-Log "disabling AutoPlay"
Set-RegistryValue "$policies\Explorer" "NoDriveTypeAutoRun" 255
Set-RegistryValue "$policies\Explorer" "NoAutorun" 1
Set-RegistryValue "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer" "NoDriveTypeAutoRun" 255

# --- Touch and display -----------------------------------------------------
Write-Log "disabling edge gestures, the lock screen, the screen saver and power saving"

Set-RegistryValue "HKLM:\SOFTWARE\Policies\Microsoft\Windows\EdgeUI" "AllowEdgeSwipe" 0
Set-RegistryValue "HKLM:\SOFTWARE\Policies\Microsoft\Windows\Personalization" "NoLockScreen" 1
Set-RegistryValue "HKLM:\SOFTWARE\Microsoft\TabletTip\1.7" "EnableDesktopModeAutoInvoke" 0
Set-RegistryValue "HKLM:\SOFTWARE\Policies\Microsoft\Windows\Control Panel\Desktop" "ScreenSaveActive" "0" "String"

# A kiosk that goes to sleep is a kiosk that looks broken.
& powercfg /setactive SCHEME_MIN | Out-Null
& powercfg /change monitor-timeout-ac 0 | Out-Null
& powercfg /change standby-timeout-ac 0 | Out-Null
& powercfg /change hibernate-timeout-ac 0 | Out-Null
& powercfg /hibernate off | Out-Null

# --- Windows Update --------------------------------------------------------
Set-Step 8 "updates, printing and time"
#
# Updates install, but the reboot is the agent's decision: it waits for the
# update window and an empty queue (plan section 7.12). No update prompts.
Write-Log "stopping Windows Update from rebooting or prompting on its own"

Set-RegistryValue "$policies\WindowsUpdate\AU" "NoAutoRebootWithLoggedOnUsers" 1
Set-RegistryValue "$policies\WindowsUpdate\AU" "AUOptions" 3
Set-RegistryValue "$policies\WindowsUpdate" "SetUpdateNotificationLevel" 1
Set-RegistryValue "$policies\WindowsUpdate" "UpdateNotificationLevel" 2

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

# --- Converters (LibreOffice, Ghostscript) ---------------------------------
Set-Step 9 "converters"
if ($DependenciesPath) {
    Write-Log "installing the converters from $DependenciesPath"

    $soffice = Join-Path ${env:ProgramFiles} "LibreOffice\program\soffice.exe"
    if (-not (Test-Path $soffice)) {
        $msi = Get-ChildItem -Path $DependenciesPath -Filter "LibreOffice_*_x64*.msi" | Select-Object -First 1
        if ($msi) {
            Write-Log "  LibreOffice: $($msi.Name)"
            $arguments = "/i `"$($msi.FullName)`" /qn /norestart ALLUSERS=1 CREATEDESKTOPLINK=0 REGISTER_NO_MSO_TYPES=1 ISCHECKFORPRODUCTUPDATES=0 QUICKSTART=0 UI_LANGS=en_US"
            $process = Start-Process -FilePath "msiexec.exe" -ArgumentList $arguments -Wait -PassThru
            if ($process.ExitCode -notin @(0, 3010)) {
                throw "The LibreOffice installer failed with exit code $($process.ExitCode)."
            }
        }
        else {
            Write-Log "  WARNING: no LibreOffice_*_x64.msi in $DependenciesPath; office documents will not convert"
        }
    }

    if (-not (Get-ChildItem -Path (Join-Path ${env:ProgramFiles} "gs") -Filter "gswin64c.exe" -Recurse -ErrorAction SilentlyContinue)) {
        $gs = Get-ChildItem -Path $DependenciesPath -Filter "gs*w64.exe" | Select-Object -First 1
        if ($gs) {
            Write-Log "  Ghostscript: $($gs.Name)"
            $process = Start-Process -FilePath $gs.FullName -ArgumentList "/S" -Wait -PassThru
            if ($process.ExitCode -ne 0) {
                throw "The Ghostscript installer failed with exit code $($process.ExitCode)."
            }
        }
        else {
            Write-Log "  WARNING: no gs*w64.exe in $DependenciesPath; PostScript files will not convert"
        }
    }
}

# --- NeoPrint --------------------------------------------------------------
Set-Step 10 "NeoPrint"
#
# The UI (app) and the agent, each in <component>\versions\<version> with a
# "current" junction; the data directory is outside them, so an update never
# touches identity or spool (plan section 7.12). -OfflinePackage is a folder
# or .zip with app\, agent\ and VERSION; later versions come through the agent.
function Install-Version([string]$Component, [string]$Version, [string]$Source) {
    $target = Join-Path $InstallRoot "$Component\versions\$Version"
    if (Test-Path $target) {
        Remove-Item -Path $target -Recurse -Force
    }

    Copy-Item -Path $Source -Destination $target -Recurse -Force
    Set-Content -Path (Join-Path $target ".neoprint-complete") -Value $Version -NoNewline

    $current = Join-Path $InstallRoot "$Component\current"
    if (Test-Path $current) {
        (Get-Item $current).Delete()
    }

    & cmd /c mklink /J "`"$current`"" "`"$target`"" | Out-Null
    Write-Log "NeoPrint $Component $Version is current"
}

# The newest published release of a component, checked on the stick (T-327).
function Get-LatestRelease([string] $Component) {
    $work = Join-Path $env:TEMP ("neoprint-download-$Component-" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $work -Force | Out-Null

    Write-Log "asking $ApiUrl for the newest $Component for win-x64 ($Channel)"
    $meta = Invoke-RestMethod -Uri "$ApiUrl/api/releases/latest/$Component/win-x64?channel=$Channel" -TimeoutSec 30
    $base = $meta.packagePath.Substring(0, $meta.packagePath.Length - "/package".Length)

    Invoke-WebRequest -Uri "$ApiUrl/$base/manifest" -OutFile (Join-Path $work "manifest.json") -UseBasicParsing -TimeoutSec 30
    Invoke-WebRequest -Uri "$ApiUrl/$base/signature" -OutFile (Join-Path $work "manifest.json.sig") -UseBasicParsing -TimeoutSec 30
    Invoke-WebRequest -Uri "$ApiUrl/$base/package" -OutFile (Join-Path $work "package.zip") -UseBasicParsing -TimeoutSec 1800

    $checker = if ($Verifier) { $Verifier } else { Join-Path $PSScriptRoot "..\verifier\NeoPrint.Kiosk.Agent.exe" }
    if (-not (Test-Path $checker)) {
        throw "The checker is not on the stick: $checker"
    }

    $checked = & $checker verify-package (Join-Path $work "package.zip") (Join-Path $work "manifest.json") (Join-Path $work "manifest.json.sig") $Component
    if ($LASTEXITCODE -ne 0) {
        throw "$Component $($meta.version) did NOT pass the signature check; nothing of it is installed"
    }
    if ("$checked".Trim() -ne $meta.version) {
        throw "The signed version ($checked) is not the one the backend named ($($meta.version)); nothing of it is installed"
    }
    Write-Log "$Component $($meta.version): signature checked with the keys on the stick"

    Expand-Archive -Path (Join-Path $work "package.zip") -DestinationPath (Join-Path $work "files") -Force
    return [pscustomobject]@{ Version = $meta.version; Folder = (Join-Path $work "files"); Work = $work }
}

if ($Download) {
    if ($OfflinePackage) { throw "-Download and -OfflinePackage are two ways to get NeoPrint: choose one" }
    if (-not $ApiUrl) { throw "-Download needs -ApiUrl: the backend it downloads from" }

    $app = Get-LatestRelease "app"
    $agent = Get-LatestRelease "agent"
    try {
        if (Get-Service -Name $AgentName -ErrorAction SilentlyContinue) {
            Stop-Service -Name $AgentName -Force -ErrorAction SilentlyContinue
        }

        Install-Version "app" $app.Version $app.Folder
        Install-Version "agent" $agent.Version $agent.Folder
    }
    finally {
        Remove-Item -Path $app.Work, $agent.Work -Recurse -Force -ErrorAction SilentlyContinue
    }
}
elseif ($OfflinePackage) {
    Write-Log "installing NeoPrint from $OfflinePackage"

    $staging = Join-Path $env:TEMP ("neoprint-" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $staging -Force | Out-Null

    try {
        if (Test-Path $OfflinePackage -PathType Container) {
            Copy-Item -Path (Join-Path $OfflinePackage "*") -Destination $staging -Recurse -Force
        }
        else {
            Expand-Archive -Path $OfflinePackage -DestinationPath $staging -Force
        }

        foreach ($required in @("app\NeoPrint.Kiosk.exe", "agent\NeoPrint.Kiosk.Agent.exe")) {
            if (-not (Test-Path (Join-Path $staging $required))) {
                throw "The package has no $required; see docs/KIOSK_SETUP.md."
            }
        }

        $versionFile = Join-Path $staging "VERSION"
        $version = if (Test-Path $versionFile) { (Get-Content $versionFile -Raw).Trim() } else { Get-Date -Format "yyyyMMddHHmmss" }

        # The agent runs from its "current"; it stops while its files are swapped.
        if (Get-Service -Name $AgentName -ErrorAction SilentlyContinue) {
            Stop-Service -Name $AgentName -Force -ErrorAction SilentlyContinue
        }

        Install-Version "app" $version (Join-Path $staging "app")
        Install-Version "agent" $version (Join-Path $staging "agent")
    }
    finally {
        Remove-Item -Path $staging -Recurse -Force -ErrorAction SilentlyContinue
    }
}
elseif (-not (Test-Path $ShellPath)) {
    Write-Log "NOTE: no -OfflinePackage and nothing installed yet; install a first version with -OfflinePackage; later ones come through the agent (T-321)"
}

if ($ApiUrl) {
    Write-Log "recording the API address for this environment"
    [Environment]::SetEnvironmentVariable("NEOPRINT_KIOSK_Kiosk__ApiBaseUrl", $ApiUrl, "Machine")
}

# --- The agent service -----------------------------------------------------
Set-Step 11 "agent service"
if (Test-Path $AgentPath) {
    Write-Log "installing the agent service"

    if (Get-Service -Name $AgentName -ErrorAction SilentlyContinue) {
        & sc.exe config $AgentName binPath= "`"$AgentPath`"" start= auto | Out-Null
    }
    else {
        New-Service -Name $AgentName -BinaryPathName "`"$AgentPath`"" -DisplayName "NeoPrint agent" `
            -Description "Keeps the NeoPrint kiosk running (plan section 7.12)." -StartupType Automatic | Out-Null
    }

    # Restarted by Windows if it ever stops.
    & sc.exe failure $AgentName reset= 86400 actions= restart/5000/restart/5000/restart/5000 | Out-Null
    Start-Service -Name $AgentName -ErrorAction SilentlyContinue
}

# --- Firewall --------------------------------------------------------------
#
# Nothing reaches a kiosk from outside. Out: DNS, time, the backend and
# updates (HTTP/HTTPS), and the printers on the local network (IPP, raw, LPD,
# SNMP, mDNS). The document converters: nothing at all (docs/TASKS.md T-075).
Set-Step 12 "firewall"
$script:FirewallBefore = Get-NetFirewallProfile -Profile Domain, Private, Public |
    Select-Object Name, Enabled, DefaultInboundAction, DefaultOutboundAction

Set-NetFirewallProfile -Profile Domain, Private, Public -Enabled True -DefaultInboundAction Block -DefaultOutboundAction Block

Get-NetFirewallRule -Group "NeoPrint" -ErrorAction SilentlyContinue | Remove-NetFirewallRule
Get-NetFirewallRule -Group "NeoPrint converters" -ErrorAction SilentlyContinue | Remove-NetFirewallRule

$allow = @(
    @{ Name = "DNS (UDP)";   Protocol = "UDP"; Port = "53" },
    @{ Name = "DNS (TCP)";   Protocol = "TCP"; Port = "53" },
    @{ Name = "Time";        Protocol = "UDP"; Port = "123" },
    @{ Name = "HTTP";        Protocol = "TCP"; Port = "80" },
    @{ Name = "HTTPS";       Protocol = "TCP"; Port = "443" }
)

foreach ($rule in $allow) {
    New-NetFirewallRule -DisplayName "NeoPrint: $($rule.Name)" -Group "NeoPrint" -Direction Outbound -Action Allow `
        -Protocol $rule.Protocol -RemotePort $rule.Port | Out-Null
}

$printers = @(
    @{ Name = "IPP";  Protocol = "TCP"; Port = "631" },
    @{ Name = "Raw";  Protocol = "TCP"; Port = "9100" },
    @{ Name = "LPD";  Protocol = "TCP"; Port = "515" },
    @{ Name = "SNMP"; Protocol = "UDP"; Port = "161" },
    @{ Name = "mDNS"; Protocol = "UDP"; Port = "5353" }
)

foreach ($rule in $printers) {
    New-NetFirewallRule -DisplayName "NeoPrint: printers ($($rule.Name))" -Group "NeoPrint" -Direction Outbound -Action Allow `
        -Protocol $rule.Protocol -RemotePort $rule.Port -RemoteAddress $LocalNetworks | Out-Null
}

# Block rules win over allow rules: the converters stay offline whatever else is open.
$converters = @()
$converters += Get-ChildItem -Path (Join-Path ${env:ProgramFiles} "LibreOffice\program") -Include "soffice.exe", "soffice.bin" -Recurse -ErrorAction SilentlyContinue
$converters += Get-ChildItem -Path (Join-Path ${env:ProgramFiles} "gs") -Include "gswin64c.exe", "gswin64.exe", "gpcl6win64.exe", "gxpswin64.exe" -Recurse -ErrorAction SilentlyContinue

foreach ($converter in $converters) {
    New-NetFirewallRule -DisplayName "NeoPrint: no network for $($converter.Name)" -Group "NeoPrint converters" `
        -Direction Outbound -Action Block -Program $converter.FullName | Out-Null
}

Write-Log ("  {0} converter program(s) cut off from the network" -f $converters.Count)

# --- Shell Launcher v2 and Keyboard Filter ---------------------------------
Set-Step 13 "Shell Launcher and Keyboard Filter"
if ($script:RestartNeeded) {
    Write-Log "NOTE: Shell Launcher and Keyboard Filter were just enabled; RESTART and run this script again to configure them"
}
else {
    Write-Log "making NeoPrint the kiosk account's shell (Shell Launcher v2)"

    # The kiosk account gets NeoPrint and nothing else; every other account
    # keeps Explorer - which is where a technician signs in after the admin
    # menu's service access (plan section 7.12). If NeoPrint exits, it is
    # started again.
    $shellXml = @"
<?xml version="1.0" encoding="utf-8"?>
<ShellLauncherConfiguration xmlns="http://schemas.microsoft.com/ShellLauncher/2018/Configuration"
                            xmlns:V2="http://schemas.microsoft.com/ShellLauncher/2019/Configuration">
  <Profiles>
    <DefaultProfile>
      <Shell Shell="%SystemRoot%\explorer.exe" />
    </DefaultProfile>
    <Profile Id="{6A5D6A30-8F9B-4C8E-9C1E-4E3A2D1B0C7F}">
      <Shell Shell="$ShellPath" V2:AppType="Desktop" V2:AllAppsFullScreen="true">
        <DefaultAction Action="RestartShell" />
      </Shell>
    </Profile>
  </Profiles>
  <Configs>
    <Config>
      <Account Name="$KioskUser" />
      <Profile Id="{6A5D6A30-8F9B-4C8E-9C1E-4E3A2D1B0C7F}" />
    </Config>
  </Configs>
</ShellLauncherConfiguration>
"@

    $xmlPath    = Join-Path $DataRoot "state\shell-launcher.xml"
    $stepPath   = Join-Path $DataRoot "state\shell-launcher.ps1"
    $resultPath = Join-Path $DataRoot "state\shell-launcher.result"

    Set-Content -Path $xmlPath -Value $shellXml -Encoding utf8

    Set-Content -Path $stepPath -Encoding utf8 -Value @"
try {
    `$xml = Get-Content -Path '$xmlPath' -Raw
    `$assigned = Get-CimInstance -Namespace 'root\cimv2\mdm\dmmap' -ClassName 'MDM_AssignedAccess'
    `$assigned.ShellLauncher = [System.Net.WebUtility]::HtmlEncode(`$xml)
    Set-CimInstance -CimInstance `$assigned
    Set-Content -Path '$resultPath' -Value 'OK'
}
catch {
    Set-Content -Path '$resultPath' -Value `$_.Exception.Message
}
"@

    Invoke-AsSystem -ScriptPath $stepPath -ResultPath $resultPath
    Remove-Item -Path $stepPath -Force -ErrorAction SilentlyContinue

    Write-Log "blocking the escape keys (Keyboard Filter)"

    $embedded = "root\standardcimv2\embedded"
    $blocked = @(
        "Windows", "Application",
        "Alt+Tab", "Alt+Esc", "Alt+F4", "Alt+Space",
        "Ctrl+Esc", "Ctrl+Alt+Del", "Shift+Ctrl+Esc",
        "Win+L", "Win+D", "Win+E", "Win+R", "Win+Tab"
    )

    foreach ($id in $blocked) {
        $key = Get-CimInstance -Namespace $embedded -ClassName "WEKF_PredefinedKey" -Filter "Id='$id'" -ErrorAction SilentlyContinue
        if ($key) {
            Set-CimInstance -CimInstance $key -Property @{ Enabled = $true } | Out-Null
        }
        else {
            Write-Log "  NOTE: Keyboard Filter has no predefined key '$id' on this build"
        }
    }

    # Administrators keep their keys: the technician's session after service access.
    $setting = Get-CimInstance -Namespace $embedded -ClassName "WEKF_Settings" -Filter "Name='DisableKeyboardFilterForAdministrators'" -ErrorAction SilentlyContinue
    if ($setting) {
        Set-CimInstance -CimInstance $setting -Property @{ Value = "true" } | Out-Null
    }

    Set-Service -Name "MsKeyboardFilter" -StartupType Automatic -ErrorAction SilentlyContinue
}

Set-Content -Path (Join-Path $DataRoot "state\provisioning-version") `
    -Value $ProvisioningVersion -NoNewline -Encoding utf8

if ($script:RestartNeeded) {
    Write-Log "provisioning is half done: restart, then run it again"
}
else {
    Write-Log "provisioning finished; a restart is required to enter kiosk mode"
}

Write-Log "log written to $LogFile"

}
catch {
    Write-Log "ERROR: step $($script:Step)/$($script:Steps) ($($script:StepName)) failed: $($_.Exception.Message)"
    Write-Log "what to do: $(Get-StepHint $script:Step)"

    # Back as they were: the registry values this run had changed, newest first.
    for ($i = $script:RegistryUndo.Count - 1; $i -ge 0; $i--) {
        $undo = $script:RegistryUndo[$i]
        try {
            if ($undo.Existed) {
                New-ItemProperty -Path $undo.Path -Name $undo.Name -Value $undo.Value -PropertyType $undo.Kind -Force | Out-Null
            }
            else {
                Remove-ItemProperty -Path $undo.Path -Name $undo.Name -ErrorAction SilentlyContinue
            }
        }
        catch {
            Write-Log "  could not put back $($undo.Path)\$($undo.Name): $($_.Exception.Message)"
        }
    }
    if ($script:RegistryUndo.Count -gt 0) {
        Write-Log "put back as they were: $($script:RegistryUndo.Count) registry value(s)"
    }

    if ($script:Step -eq 12 -and $script:FirewallBefore) {
        foreach ($saved in $script:FirewallBefore) {
            Set-NetFirewallProfile -Profile $saved.Name -Enabled $saved.Enabled `
                -DefaultInboundAction $saved.DefaultInboundAction -DefaultOutboundAction $saved.DefaultOutboundAction -ErrorAction SilentlyContinue
        }
        Write-Log "the firewall's defaults are as they were"
    }

    Write-Log "nothing else needs undoing: every step can run again - remove the cause and run this script once more"
    Write-Log "log written to $LogFile"
    exit 1
}

