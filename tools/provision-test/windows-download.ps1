<#
.SYNOPSIS
    provision.ps1 -Download on this Windows machine, without changing it (T-327).

.DESCRIPTION
    Runs the script's own Get-LatestRelease - taken out of provision.ps1, not
    copied - against a stand-in backend on this machine: the UI and the agent
    for win-x64 are published, packed, given manifests and signed with the
    staging release key (docs/RELEASE_SIGNING.md, staging), and served as
    GET api/releases/latest would serve them. The checker is an agent built
    with the staging keys, as the stick's is built with the production ones.

    1. Both are downloaded, pass the check on the "stick" and are unpacked.
    2. A package changed on the way is refused, and nothing of it is unpacked.

    Needs the .NET SDK, Node.js and the staging key in
    %USERPROFILE%\.neoprint\release-keys\staging. Installs nothing; the real
    run as Administrator on a kiosk is M19b (T-331).

        pwsh -NoProfile -File tools\provision-test\windows-download.ps1
#>
$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$root = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$work = Join-Path $env:TEMP ("neoprint-windl-" + [Guid]::NewGuid().ToString("N"))
$version = "0.9.1-m19a"
$port = 8099
$failures = 0
function Check([bool] $Condition, [string] $Text) {
    if ($Condition) { Write-Host "ok     $Text" } else { Write-Host "WRONG  $Text"; $script:failures++ }
}

$keys = Join-Path $env:USERPROFILE ".neoprint\release-keys\staging\current.private.pem"
if (-not (Test-Path $keys)) {
    Write-Host "skipped: no staging release key at $keys (docs/RELEASE_SIGNING.md)"
    exit 0
}

New-Item -ItemType Directory -Path $work | Out-Null
$server = $null
try {
    Write-Host "== packages, manifests, signatures"
    $rel = Join-Path $work "rel"
    New-Item -ItemType Directory -Path $rel | Out-Null
    foreach ($project in @("NeoPrint.Kiosk", "NeoPrint.Kiosk.Agent")) {
        $component = if ($project -eq "NeoPrint.Kiosk") { "app" } else { "agent" }
        $out = Join-Path $work "out\$component"
        dotnet publish (Join-Path $root "desktop\src\$project\$project.csproj") -c Release -r win-x64 --self-contained false `
            -p:ReleaseKeySet=staging -p:Version=$version -o $out -v q | Out-Null
        if ($LASTEXITCODE) { throw "publish $project failed" }
        Compress-Archive -Path (Join-Path $out "*") -DestinationPath (Join-Path $rel "neoprint-kiosk-$component-$version-win-x64-staging.zip")
    }
    dotnet build (Join-Path $root "tools\release-sign") -c Release -v q | Out-Null
    $sign = Join-Path $root "tools\release-sign\bin\Release\net8.0\release-sign.dll"
    foreach ($component in @("App", "Agent")) {
        $name = "neoprint-kiosk-$($component.ToLowerInvariant())-$version-win-x64-staging.zip"
        dotnet $sign manifest --package (Join-Path $rel $name) --component $component --platform win-x64 --key-set staging `
            --version $version --min-agent-version 0.1.0 --commit c0ffee1 --out (Join-Path $rel "$name.manifest.json") | Out-Null
        if ($LASTEXITCODE) { throw "manifest $component failed" }
    }
    dotnet $sign sign-staging --dir $rel | Out-Null
    if ($LASTEXITCODE) { throw "signing failed" }

    Write-Host "== a stand-in backend"
    $srv = Join-Path $work "srv"
    foreach ($component in @("app", "agent")) {
        $name = "neoprint-kiosk-$component-$version-win-x64-staging.zip"
        $files = Join-Path $srv "api\releases\$component\win-x64\$version"
        New-Item -ItemType Directory -Path $files, (Join-Path $srv "api\releases\latest\$component") -Force | Out-Null
        Copy-Item (Join-Path $rel $name) (Join-Path $files "package")
        Copy-Item (Join-Path $rel "$name.manifest.json") (Join-Path $files "manifest")
        Copy-Item (Join-Path $rel "$name.manifest.json.sig") (Join-Path $files "signature")
        Set-Content -Path (Join-Path $srv "api\releases\latest\$component\win-x64") -NoNewline -Encoding ascii -Value (
            '{"component":"' + $component + '","platform":"win-x64","version":"' + $version + '","packagePath":"api/releases/' + $component + '/win-x64/' + $version + '/package"}')
    }
    $serve = Join-Path $work "serve.js"
    Set-Content -Path $serve -Encoding ascii -Value @"
const http = require('http'), fs = require('fs'), path = require('path');
const root = process.argv[2];
http.createServer((req, res) => {
  const file = path.join(root, decodeURIComponent(req.url.split('?')[0]));
  if (!file.startsWith(root) || !fs.existsSync(file) || fs.statSync(file).isDirectory()) { res.writeHead(404); res.end(); return; }
  res.writeHead(200, { 'Content-Type': file.endsWith('win-x64') ? 'application/json' : 'application/octet-stream' });
  fs.createReadStream(file).pipe(res);
}).listen($port, '127.0.0.1');
"@
    $server = Start-Process -FilePath node -ArgumentList $serve, $srv -PassThru -WindowStyle Hidden
    Start-Sleep -Seconds 1

    Write-Host "== provision.ps1's Get-LatestRelease"
    $ast = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $root "desktop\provisioning\windows\provision.ps1"), [ref]$null, [ref]$null)
    $function = $ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq "Get-LatestRelease" }, $true) | Select-Object -First 1
    if (-not $function) { throw "provision.ps1 has no Get-LatestRelease" }
    function Write-Log([string] $Message) { Write-Host "  [provision] $Message" }
    . ([scriptblock]::Create($function.Extent.Text))

    $ApiUrl = "http://127.0.0.1:$port"
    $Channel = "pilot"
    $Verifier = Join-Path $work "out\agent\NeoPrint.Kiosk.Agent.exe"

    $app = Get-LatestRelease "app"
    $agent = Get-LatestRelease "agent"
    Check ($app.Version -eq $version -and (Test-Path (Join-Path $app.Folder "NeoPrint.Kiosk.exe"))) "the UI was downloaded, checked on the stick and unpacked"
    Check ($agent.Version -eq $version -and (Test-Path (Join-Path $agent.Folder "NeoPrint.Kiosk.Agent.exe"))) "and the agent"

    # A package changed on the way.
    Add-Content -Path (Join-Path $srv "api\releases\agent\win-x64\$version\package") -Value "x" -NoNewline
    $refused = $null
    try { Get-LatestRelease "agent" | Out-Null } catch { $refused = $_.Exception.Message }
    Check ($refused -match "did NOT pass the signature check") "a package changed on the way is refused: $refused"
}
finally {
    if ($server) { Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue }
    Get-ChildItem -Path $env:TEMP -Filter "neoprint-download-*" -Directory -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -Path $work -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures) { Write-Host "$failures check(s) failed."; exit 1 }
Write-Host "Every check passed."
