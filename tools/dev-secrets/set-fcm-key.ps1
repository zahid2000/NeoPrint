<#
.SYNOPSIS
    Writes a Firebase service account key into the local OpenBao, nowhere else.

.DESCRIPTION
    Plan section 2.13: the backend takes NEOPRINT_FCM_SERVICE_ACCOUNT_JSON from
    OpenBao (neoprint/backend/providers). This script reads the JSON file the
    Firebase Console downloaded, checks it is a service account key, and pipes
    it into `bao kv patch` through stdin - the key never appears on a command
    line, in the console, in a log or in the repository. The OpenBao root token
    is read from .openbao/init.json (gitignored) and handed to docker through
    the environment, not the command line.

    Afterwards delete the downloaded file and restart the api container.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\dev-secrets\set-fcm-key.ps1 -Path C:\Users\me\Downloads\neoprint-firebase-adminsdk.json
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$Path,

    [string]$Container = 'neoprint-openbao'
)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')

if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
    Write-Host "Fayl tapılmadı: $Path" -ForegroundColor Red
    exit 1
}

$raw = Get-Content -LiteralPath $Path -Raw -Encoding UTF8
try {
    $key = $raw | ConvertFrom-Json
} catch {
    Write-Host 'Fayl JSON deyil. Firebase Console → Service accounts → "Generate new private key" ilə yüklənən faylı verin.' -ForegroundColor Red
    exit 1
}

if ($key.type -ne 'service_account' -or -not $key.private_key -or -not $key.client_email -or -not $key.project_id) {
    Write-Host 'Bu, service account açarı deyil (type, private_key, client_email, project_id lazımdır). google-services.json və GoogleService-Info.plist buraya aid deyil.' -ForegroundColor Red
    exit 1
}

$initFile = Join-Path $repo '.openbao\init.json'
if (-not (Test-Path -LiteralPath $initFile)) {
    Write-Host ".openbao\init.json tapılmadı. Əvvəl stack-i qaldırın: docker compose up -d --remove-orphans" -ForegroundColor Red
    exit 1
}

$running = docker ps --filter "name=^$Container$" --format '{{.Names}}'
if ($running -ne $Container) {
    Write-Host "$Container konteyneri işləmir. Əvvəl: docker compose up -d --remove-orphans" -ForegroundColor Red
    exit 1
}

# Only for the docker child process: `-e BAO_TOKEN` with no value copies it from here.
$env:BAO_TOKEN = (Get-Content -LiteralPath $initFile -Raw | ConvertFrom-Json).root_token
try {
    $raw | docker exec -i -e BAO_ADDR=http://127.0.0.1:8200 -e BAO_TOKEN $Container `
        bao kv patch -mount=neoprint backend/providers NEOPRINT_FCM_SERVICE_ACCOUNT_JSON=- | Out-Null
    $code = $LASTEXITCODE
} finally {
    Remove-Item Env:\BAO_TOKEN -ErrorAction SilentlyContinue
}

if ($code -ne 0) {
    Write-Host "OpenBao-ya yazılmadı (bao exit $code). OpenBao möhürlənibsə: docker compose up -d openbao-init (docs/DEV_ENVIRONMENT.md)." -ForegroundColor Red
    exit 1
}

# Identifiers only - never the key.
Write-Host "FCM açarı OpenBao-ya yazıldı: layihə $($key.project_id), hesab $($key.client_email)." -ForegroundColor Green
Write-Host 'İndi:'
Write-Host "  1. Yüklənmiş faylı silin: Remove-Item '$Path'"
Write-Host '  2. API-ni yenidən başladın: docker compose restart api'
