<#
.SYNOPSIS
    Prepare a provisioned Windows kiosk to be cloned (plan section 7.12).

.DESCRIPTION
    Removes everything that identifies this particular machine, so the image
    can be copied onto many kiosks and each one registers as its own device on
    first boot. Getting this wrong is how two kiosks end up sharing an
    identity, so the script verifies afterwards that nothing was left behind.

    Run this LAST, immediately before capturing the image. With -RunSysprep it
    also generalizes the installation, which is what gives each clone its own
    machine GUID.

.PARAMETER RunSysprep
    Runs sysprep /generalize /oobe /shutdown at the end. The machine shuts down
    and must not be booted again before capture.

.EXAMPLE
    .\prepare-image.ps1 -RunSysprep
#>
[CmdletBinding()]
param(
    [switch] $RunSysprep
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$DataRoot = "C:\ProgramData\NeoPrint"

function Write-Log {
    param([Parameter(Mandatory)][string] $Message)
    Write-Host ("[prepare-image] {0}" -f $Message)
}

function Assert-Administrator {
    $identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)

    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "This script must run as Administrator."
    }
}

Assert-Administrator

# ---------------------------------------------------------------------------
# The device key
#
# On Windows the key is a non-exportable CNG key, not a file, so deleting the
# folder is not enough - the key container itself has to go, or every clone
# would carry the same identity (plan section 7.11).
# ---------------------------------------------------------------------------
Write-Log "removing the device key container"

foreach ($provider in @("Microsoft Platform Crypto Provider", "Microsoft Software Key Storage Provider")) {
    try {
        $providerObject = New-Object System.Security.Cryptography.CngProvider($provider)

        if ([System.Security.Cryptography.CngKey]::Exists(
                "NeoPrint.Device", $providerObject,
                [System.Security.Cryptography.CngKeyOpenOptions]::MachineKey)) {

            $key = [System.Security.Cryptography.CngKey]::Open(
                "NeoPrint.Device", $providerObject,
                [System.Security.Cryptography.CngKeyOpenOptions]::MachineKey)

            $key.Delete()
            $key.Dispose()

            Write-Log "  removed from $provider"
        }
    }
    catch [System.Security.Cryptography.CryptographicException] {
        # Provider not present on this machine.
    }
}

Write-Log "removing the device folder, the cached configuration and the spool"

foreach ($directory in @("device", "spool", "state")) {
    $path = Join-Path $DataRoot $directory
    if (Test-Path $path) {
        Remove-Item -Path (Join-Path $path "*") -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Log "removing the logs"

Remove-Item -Path (Join-Path $DataRoot "logs\*") -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -Path (Join-Path $DataRoot "provision.log") -Force -ErrorAction SilentlyContinue

foreach ($log in @("Application", "System")) {
    & wevtutil cl $log 2>$null
}

# ---------------------------------------------------------------------------
# Verification
# ---------------------------------------------------------------------------
Write-Log "verifying"

$failed = $false

foreach ($provider in @("Microsoft Platform Crypto Provider", "Microsoft Software Key Storage Provider")) {
    try {
        $providerObject = New-Object System.Security.Cryptography.CngProvider($provider)

        if ([System.Security.Cryptography.CngKey]::Exists(
                "NeoPrint.Device", $providerObject,
                [System.Security.Cryptography.CngKeyOpenOptions]::MachineKey)) {

            Write-Error "  STILL PRESENT: the device key in $provider"
            $failed = $true
        }
    }
    catch [System.Security.Cryptography.CryptographicException] {
    }
}

foreach ($leftover in @("device\key.json", "device\enrollment-token")) {
    $path = Join-Path $DataRoot $leftover
    if (Test-Path $path) {
        Write-Error "  STILL PRESENT: $path"
        $failed = $true
    }
}

if ($failed) {
    throw "prepare-image did NOT finish cleanly. Do not capture this image."
}

Write-Log "clean: this machine can be captured as a golden image"
Write-Log "each clone will generate its own device key and register separately"

if ($RunSysprep) {
    # Generalizing is what gives each clone its own machine GUID, which the
    # fingerprint check relies on to tell the machines apart.
    Write-Log "running sysprep /generalize; the machine will shut down"
    & "$env:SystemRoot\System32\Sysprep\sysprep.exe" /generalize /oobe /shutdown /quiet
}
else {
    Write-Log "sysprep was NOT run. Run it with -RunSysprep, or by hand, before capturing:"
    Write-Log "  sysprep /generalize /oobe /shutdown"
}
