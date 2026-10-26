<#
.SYNOPSIS
    Proves that a production kiosk build accepts a real production package -
    and nothing else (T-315, T-327, docs/RELEASE_SIGNING.md section 3a).

.DESCRIPTION
    After the owner's two public keys are written into
    desktop/src/NeoPrint.Kiosk.Core/Updates/KeySets/ReleaseKeys.production.cs,
    and the first production package is signed offline:

        pwsh -NoProfile -File tools\release-sign\production-check.ps1 `
            -Package   D:\rel\neoprint-kiosk-agent-0.9.0-win-x64-production.zip `
            -Manifest  D:\rel\neoprint-kiosk-agent-0.9.0-win-x64-production.zip.manifest.json `
            -Signature D:\rel\neoprint-kiosk-agent-0.9.0-win-x64-production.zip.manifest.json.sig

    1. The agent is built for win-x64 with -p:ReleaseKeySet=production from
       this repository, and release-sign check-build finds only the production
       set in it.
    2. That build's own check (verify-package, the one a kiosk and the setup
       stick run) accepts the package and names its version.
    3. The same package with one byte changed is refused.

    -SelfTest does the same before the real keys exist, in a copy of the
    repository made for the run: two throwaway key pairs are made with
    release-sign keygen, their public keys written into the copy's
    ReleaseKeys.production.cs, a package signed with release-sign sign - and,
    on top, a build of the real repository (whose production keys may still be
    empty) is shown to refuse it. The throwaway keys and the copy are deleted.
    No private key leaves this machine in either mode; nothing is uploaded.

    Needs PowerShell 7 and the .NET SDK.
#>
[CmdletBinding(DefaultParameterSetName = "Real")]
param(
    [Parameter(ParameterSetName = "Real", Mandatory)][string] $Package,
    [Parameter(ParameterSetName = "Real", Mandatory)][string] $Manifest,
    [Parameter(ParameterSetName = "Real", Mandatory)][string] $Signature,
    [Parameter(ParameterSetName = "Real")][ValidateSet("app", "agent", "provisioning")][string] $Component = "agent",
    [Parameter(ParameterSetName = "SelfTest", Mandatory)][switch] $SelfTest
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repo = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$work = Join-Path $env:TEMP ("neoprint-production-check-" + [Guid]::NewGuid().ToString("N"))
$failures = 0
function Check([bool] $Condition, [string] $Text) {
    if ($Condition) { Write-Host "ok     $Text" -ForegroundColor Green } else { Write-Host "WRONG  $Text" -ForegroundColor Red; $script:failures++ }
}
function Step([string] $Text) { Write-Host "== $Text" }
function Invoke-Dotnet {
    & dotnet @args
    if ($LASTEXITCODE) { throw "dotnet $($args -join ' ') failed ($LASTEXITCODE)" }
}

# Builds the agent for win-x64 with the production set, from a source tree.
function Publish-ProductionAgent([string] $Root, [string] $Out) {
    Invoke-Dotnet publish (Join-Path $Root "desktop\src\NeoPrint.Kiosk.Agent\NeoPrint.Kiosk.Agent.csproj") -c Release -r win-x64 `
        --self-contained false "-p:ReleaseKeySet=production" -o $Out -v q | Out-Null
    return (Join-Path $Out "NeoPrint.Kiosk.Agent.exe")
}

# The check a kiosk runs: 0 and the version, or 2 and why not.
function Test-Package([string] $Agent, [string] $File, [string] $ManifestFile, [string] $SignatureFile, [string] $Kind) {
    $output = & $Agent verify-package $File $ManifestFile $SignatureFile $Kind 2>&1
    return [pscustomobject]@{ Code = $LASTEXITCODE; Output = ("$output").Trim() }
}

function Assert-ProductionBuild([string] $Root, [string] $Out) {
    $agent = Publish-ProductionAgent $Root $Out
    Invoke-Dotnet build (Join-Path $Root "tools\release-sign") -c Release -v q | Out-Null
    & dotnet (Join-Path $Root "tools\release-sign\bin\Release\net8.0\release-sign.dll") check-build --dir $Out --key-set production | Out-Null
    Check ($LASTEXITCODE -eq 0) "the production build carries the production key set and nothing of staging"
    return $agent
}

function Assert-Accepts([string] $Agent, [string] $File, [string] $ManifestFile, [string] $SignatureFile, [string] $Kind) {
    $accepted = Test-Package $Agent $File $ManifestFile $SignatureFile $Kind
    Check ($accepted.Code -eq 0) "the production build accepts $(Split-Path $File -Leaf): version $($accepted.Output)"

    $changed = Join-Path $work ("changed-" + (Split-Path $File -Leaf))
    Copy-Item $File $changed
    [System.IO.File]::AppendAllText($changed, "x")
    $refused = Test-Package $Agent $changed $ManifestFile $SignatureFile $Kind
    Check ($refused.Code -eq 2) "and refuses it with one byte changed: $($refused.Output)"
}

New-Item -ItemType Directory -Path $work | Out-Null
try {
    if (-not $SelfTest) {
        $keys = Get-Content (Join-Path $repo "desktop\src\NeoPrint.Kiosk.Core\Updates\KeySets\ReleaseKeys.production.cs") -Raw
        if ($keys -notmatch "BEGIN PUBLIC KEY") {
            throw "ReleaseKeys.production.cs has no public key yet: write the two public keys in first (T-315, docs/RELEASE_SIGNING.md section 3a)."
        }

        Step "the production build of this repository"
        $agent = Assert-ProductionBuild $repo (Join-Path $work "agent")
        Step "the real package"
        Assert-Accepts $agent $Package $Manifest $Signature $Component
    }
    else {
        Step "a copy of the repository for the run"
        $copy = Join-Path $work "repo"
        foreach ($item in @("global.json", "Directory.Build.props", "Directory.Packages.props")) {
            Copy-Item (Join-Path $repo $item) (Join-Path $copy $item) -Force -ErrorAction SilentlyContinue
            if (-not (Test-Path (Join-Path $copy $item))) { New-Item -ItemType Directory -Path $copy -Force | Out-Null; Copy-Item (Join-Path $repo $item) (Join-Path $copy $item) }
        }
        foreach ($folder in @("desktop", "backend\shared", "tools\release-sign")) {
            & robocopy (Join-Path $repo $folder) (Join-Path $copy $folder) /E /XD bin obj /NFL /NDL /NJH /NJS /NP | Out-Null
            if ($LASTEXITCODE -ge 8) { throw "copying $folder failed" }
        }

        Step "two throwaway key pairs (release-sign keygen)"
        Invoke-Dotnet build (Join-Path $copy "tools\release-sign") -c Release -v q | Out-Null
        $sign = Join-Path $copy "tools\release-sign\bin\Release\net8.0\release-sign.dll"
        $keyDir = Join-Path $work "keys"
        $passphrase = [Convert]::ToBase64String([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(24))
        foreach ($name in @("current", "next")) {
            "$passphrase`n$passphrase`n" | & dotnet $sign keygen --name $name --out $keyDir | Out-Null
            if ($LASTEXITCODE) { throw "keygen $name failed" }
        }

        # Written as the owner's will be: the two public keys, nothing else.
        $file = Join-Path $copy "desktop\src\NeoPrint.Kiosk.Core\Updates\KeySets\ReleaseKeys.production.cs"
        $text = Get-Content $file -Raw
        foreach ($name in @("Current", "Next")) {
            $pem = (Get-Content (Join-Path $keyDir "$($name.ToLowerInvariant()).public.pem") -Raw).Trim() -split "`r?`n" | ForEach-Object { "        $_" }
            $literal = "`"`"`"`n" + ($pem -join "`n") + "`n        `"`"`""
            $text = $text -replace "public const string $name = `"`";", "public const string $name = $literal;"
        }
        Set-Content -Path $file -Value $text -NoNewline
        Check ($text -match "BEGIN PUBLIC KEY") "the copy's ReleaseKeys.production.cs carries the two public keys"

        Step "the production build of the copy"
        Invoke-Dotnet build (Join-Path $copy "tools\release-sign") -c Release -v q | Out-Null
        $agent = Assert-ProductionBuild $copy (Join-Path $work "agent")

        Step "a production package, signed as the owner signs it (release-sign sign)"
        $version = "0.9.2-selftest"
        $rel = Join-Path $work "rel"
        New-Item -ItemType Directory -Path $rel | Out-Null
        $name = "neoprint-kiosk-agent-$version-win-x64-production.zip"
        $zip = Join-Path $rel $name
        Compress-Archive -Path (Join-Path $work "agent\*") -DestinationPath $zip
        Invoke-Dotnet $sign manifest --package $zip --component Agent --platform win-x64 --key-set production --version $version `
            --min-agent-version 0.1.0 --commit c0ffee1 --out "$zip.manifest.json" | Out-Null
        "$passphrase`n" | & dotnet $sign sign --manifest "$zip.manifest.json" --package $zip --key (Join-Path $keyDir "current.private.pem") | Out-Null
        Check ($LASTEXITCODE -eq 0 -and (Test-Path "$zip.manifest.json.sig")) "signed with the current key"

        Step "the check"
        Assert-Accepts $agent $zip "$zip.manifest.json" "$zip.manifest.json.sig" "agent"

        Step "a build of this repository, with the keys it has today"
        $today = Publish-ProductionAgent $repo (Join-Path $work "agent-today")
        $refused = Test-Package $today $zip "$zip.manifest.json" "$zip.manifest.json.sig" "agent"
        Check ($refused.Code -eq 2) "refuses a package signed with keys it does not carry: $($refused.Output)"
    }
}
finally {
    Remove-Item -Path $work -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures) { Write-Host "$failures check(s) failed."; exit 1 }
Write-Host "Every check passed."
