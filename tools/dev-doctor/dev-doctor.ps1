# ---------------------------------------------------------------------------
# NeoPrint dev-doctor (Windows PowerShell): finds what stops
# `docker compose up` on this machine before compose does
# (docs/DEV_ENVIRONMENT.md, "Problem olarsa"). The same checks as
# dev-doctor.sh:
#
#   powershell -ExecutionPolicy Bypass -File tools\dev-doctor\dev-doctor.ps1
#
# Docker is running; .env.openbao-seed matches the example; nothing else holds
# a port compose publishes; no container or volume is left from a service
# compose no longer has. Read-only: it changes nothing, it says what to run.
# ---------------------------------------------------------------------------
$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

Set-Location (Join-Path $PSScriptRoot '..\..')

$script:problems = 0
$project = 'neoprint'

function Ok([string]$text)   { Write-Host "  + $text" -ForegroundColor Green }
function Bad([string]$text)  { Write-Host "  x $text" -ForegroundColor Red; $script:problems++ }
function Hint([string]$text) { Write-Host "      -> $text" }

Write-Host '1. Docker'
$server = (docker version --format '{{.Server.Version}}' 2>$null)
if (-not $server) {
    Bad 'Docker işləmir və ya əlçatan deyil.'
    Hint 'Docker Desktop-u açın, sonra yenidən işlədin.'
    Write-Host ''
    Write-Host "Nəticə: $script:problems problem."
    exit 1
}
Ok "Docker işləyir ($server)."

Write-Host '2. Seed faylı (.env.openbao-seed)'
function KeysOf([string]$path) {
    Get-Content $path | Where-Object { $_ -match '^\s*[A-Za-z_][A-Za-z0-9_]*=' } |
        ForEach-Object { ($_ -replace '^\s*', '') -replace '=.*$', '' } | Sort-Object -Unique
}
function ValueOf([string]$key, [string]$path) {
    $line = Get-Content $path | Where-Object { $_ -match "^\s*$key=" } | Select-Object -Last 1
    if ($line) { return ($line -replace '^[^=]*=', '').Trim() }
    return ''
}

if (-not (Test-Path '.env.openbao-seed')) {
    Bad '.env.openbao-seed yoxdur.'
    Hint 'Copy-Item .env.openbao-seed.example .env.openbao-seed'
} else {
    $expected = ValueOf 'SEED_SCHEMA_VERSION' '.env.openbao-seed.example'
    $actual = ValueOf 'SEED_SCHEMA_VERSION' '.env.openbao-seed'
    $exampleKeys = @(KeysOf '.env.openbao-seed.example')
    $seedKeys = @(KeysOf '.env.openbao-seed')
    $missing = @($exampleKeys | Where-Object { $seedKeys -notcontains $_ })
    $unknown = @($seedKeys | Where-Object { $exampleKeys -notcontains $_ })

    if ($actual -eq $expected -and $missing.Count -eq 0 -and $unknown.Count -eq 0) {
        Ok "Nümunə ilə uyğundur (versiya $actual)."
    } else {
        if ($actual -ne $expected) { Bad "Versiya $(if ($actual) { $actual } else { '(yoxdur)' }), nümunədə $expected." }
        if ($missing.Count -gt 0) { Bad "Çatışmayan açarlar: $($missing -join ' ')" }
        if ($unknown.Count -gt 0) { Bad "Tanınmayan açarlar (köhnə servisdən qala bilər): $($unknown -join ' ')" }
        Hint 'docs/DEV_ENVIRONMENT.md -> "Seed faylı". Öz dəyəriniz yoxdursa: Copy-Item .env.openbao-seed.example .env.openbao-seed'
    }
}

# What compose declares, and what Docker has - fetched once each.
$config = docker compose config --format json 2>$null | ConvertFrom-Json
$services = @($config.services.PSObject.Properties.Name)
$volumes = @(if ($config.volumes) { $config.volumes.PSObject.Properties.Name })
$ports = @($config.services.PSObject.Properties.Value |
    ForEach-Object { $_.ports } | Where-Object { $_ } |
    ForEach-Object { [int]$_.published } | Sort-Object -Unique)
$own = @(docker ps --filter "label=com.docker.compose.project=$project" --format '{{.Names}}')

# "port -> container" for every published port, ranges expanded.
$published = @{}
foreach ($line in (docker ps --format '{{.Names}}|{{.Ports}}')) {
    $name, $mapping = $line -split '\|', 2
    foreach ($entry in ($mapping -split ',')) {
        if ($entry -notmatch '->') { continue }
        $hostPart = ($entry -replace '->.*$', '') -replace '^.*:', ''
        $bounds = $hostPart.Trim() -split '-'
        $first = [int]$bounds[0]; $last = [int]$bounds[-1]
        for ($p = $first; $p -le $last; $p++) { if (-not $published.ContainsKey($p)) { $published[$p] = $name } }
    }
}

Write-Host '3. Portlar'
$portProblem = $false
$listening = @(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue | ForEach-Object { $_.LocalPort })
foreach ($port in $ports) {
    if ($published.ContainsKey($port)) {
        $holder = $published[$port]
        if ($own -notcontains $holder) {
            Bad "Port $port-u başqa konteyner tutur: $holder"
            Hint "docker stop $holder   (və ya: docker rm -f $holder)"
            $portProblem = $true
        }
    } elseif ($listening -contains $port) {
        Bad "Port $port-u Docker-dən kənar bir proqram tutur."
        Hint "netstat -ano | findstr :$port   (sonuncu sütun proses ID-sidir)"
        $portProblem = $true
    }
}
if (-not $portProblem) { Ok "Compose-un portları boşdur və ya NeoPrint-in öz konteynerlərindədir ($($ports -join ','))." }

# Windows PowerShell drops the quotes inside a native argument, so a
# {{.Label "..."}} template cannot be passed; the whole label list is read and
# picked apart here instead.
function LabelOf([string]$labels, [string]$key) {
    foreach ($pair in ($labels -split ',')) {
        if ($pair.StartsWith("$key=")) { return $pair.Substring($key.Length + 1) }
    }
    return ''
}

Write-Host '4. Compose-dan kənarda qalanlar'
$leftover = $false
foreach ($line in (docker ps -a --format '{{.Names}}|{{.Labels}}')) {
    $name, $labels = $line -split '\|', 2
    $owner = LabelOf $labels 'com.docker.compose.project'
    $service = LabelOf $labels 'com.docker.compose.service'
    if (-not $name.StartsWith("$project-")) { continue }
    if ($owner -ne $project -or $services -notcontains $service) {
        Bad "Konteyner $name compose faylında olmayan servisdəndir ($(if ($service) { $service } else { 'etiketsiz' }))."
        Hint "docker rm -f $name"
        $leftover = $true
    }
}
foreach ($line in (docker volume ls --format '{{.Name}}|{{.Labels}}')) {
    $name, $labels = $line -split '\|', 2
    $owner = LabelOf $labels 'com.docker.compose.project'
    $volume = LabelOf $labels 'com.docker.compose.volume'
    if ($owner -eq $project) {
        if ($volumes -contains $volume) { continue }
    } elseif ($name.StartsWith("${project}_")) {
        if ($owner -and $volumes -contains $name.Substring($project.Length + 1)) { continue }
    } else {
        continue
    }
    Bad "Volume $name compose faylında yoxdur."
    Hint "docker volume rm $name   (içindəki data silinir)"
    $leftover = $true
}
if (-not $leftover) { Ok 'Köhnə konteyner və ya volume yoxdur.' }

Write-Host ''
if ($script:problems -eq 0) {
    Write-Host 'Nəticə: problem yoxdur - docker compose up --remove-orphans -d'
    exit 0
}
Write-Host "Nəticə: $script:problems problem. Yuxarıdakı -> əmrləri icra edib yenidən yoxlayın."
exit 1
