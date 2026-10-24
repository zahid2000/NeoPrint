<#
.SYNOPSIS
  Staging paketlərini bir əmrlə hazırlayır: açar, son CI paketləri, imza, yoxlama (T-320).

.DESCRIPTION
  1. Lazımi proqramları yoxlayır (dotnet, gh) və gh-də girişi.
  2. Staging açar cütü yoxdursa yaradır (%USERPROFILE%\.neoprint\release-keys\staging),
     parolu terminalda soruşur. Varsa, onun repodakı staging açarı olduğunu yoxlayır.
  3. "Release packages" workflow-unun son uğurlu işinin staging artefaktlarını endirir.
  4. Hər paketi `release-sign sign-staging` ilə imzalayır və `verify` ilə yoxlayır.
  5. Panelə hansı faylların yüklənəcəyini Azərbaycanca yazır.

  Heç nə serverə göndərilmir, özəl açar kompüterdən çıxmır.
  Ətraflı: docs/RELEASE_SIGNING.md, "Staging: sürətli başlanğıc".

.PARAMETER Platform
  linux-x64, win-x64 və ya all (standart: all).

.PARAMETER RunId
  Konkret workflow işi (gh run list --workflow release-package.yml). Verilməsə — son uğurlu iş.

.PARAMETER OutDir
  Paketlərin endiriləcəyi qovluq. Standart: %USERPROFILE%\.neoprint\staging-packages\<iş nömrəsi>.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\release-sign\staging-quickstart.ps1
#>
param(
    [ValidateSet('all', 'linux-x64', 'win-x64')]
    [string]$Platform = 'all',
    [string]$RunId = '',
    [string]$OutDir = ''
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$tool = Join-Path $repo 'tools\release-sign'
$keyDir = Join-Path $env:USERPROFILE '.neoprint\release-keys\staging'
$dll = Join-Path $tool 'bin\Release\net8.0\release-sign.dll'
$stagingKeysFile = Join-Path $repo 'desktop\src\NeoPrint.Kiosk.Core\Updates\KeySets\ReleaseKeys.staging.cs'

function Say([string]$text) { Write-Host $text }
function Step([string]$text) { Write-Host ''; Write-Host "== $text" -ForegroundColor Cyan }
function Good([string]$text) { Write-Host "   OK: $text" -ForegroundColor Green }
function Stop-With([string]$what, [string]$todo) {
    Write-Host ''
    Write-Host "DAYANDI: $what" -ForegroundColor Red
    Write-Host "Nə etmək lazımdır: $todo" -ForegroundColor Yellow
    exit 1
}

# The base64 body of a PEM public key, without line breaks or indentation.
function Get-PemBody([string]$pem) {
    $m = [regex]::Match($pem, '-----BEGIN PUBLIC KEY-----(.*?)-----END PUBLIC KEY-----', 'Singleline')
    if (-not $m.Success) { return '' }
    return ($m.Groups[1].Value -replace '\s', '')
}

# Runs release-sign attached to this console, so its passphrase prompt shows at once; returns its exit code.
function Invoke-ReleaseSign([string[]]$arguments) {
    $quoted = @("`"$dll`"") + ($arguments | ForEach-Object { "`"$_`"" })
    $process = Start-Process -FilePath 'dotnet' -ArgumentList $quoted -NoNewWindow -Wait -PassThru
    return $process.ExitCode
}

# ---------------------------------------------------------------- 1. Proqramlar
Step '1/5  Lazımi proqramlar yoxlanılır'
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Stop-With '.NET SDK tapılmadı.' '.NET 8 SDK quraşdırın (https://dotnet.microsoft.com/download) və terminalı yenidən açın.'
}
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    Stop-With 'GitHub CLI (gh) tapılmadı.' 'GitHub CLI quraşdırın (https://cli.github.com), sonra "gh auth login" əmrini işlədin.'
}
& gh auth status *> $null
if ($LASTEXITCODE -ne 0) {
    Stop-With 'GitHub-a giriş edilməyib.' '"gh auth login" əmrini işlədin, brauzerdə təsdiqləyin, sonra bu skripti yenidən başladın.'
}
Good 'dotnet və gh var, GitHub-a giriş edilib'

Say '   release-sign aləti yığılır (ilk dəfə bir neçə dəqiqə çəkə bilər)...'
& dotnet build $tool -c Release -nologo -v q *> $null
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $dll)) {
    Stop-With 'release-sign aləti yığılmadı.' "Bu əmri əl ilə işlədib xətanı görün: dotnet build `"$tool`" -c Release"
}
Good 'release-sign hazırdır'

# ---------------------------------------------------------------- 2. Açar
Step '2/5  Staging açarı'
$currentPrivate = Join-Path $keyDir 'current.private.pem'
$currentPublic = Join-Path $keyDir 'current.public.pem'

if (-not (Test-Path $currentPrivate)) {
    Say "   $keyDir qovluğunda staging açarı yoxdur — indi iki açar yaradılır: current və next."
    Say '   Hər açar üçün parol iki dəfə soruşulacaq (ən azı 12 simvol). Yazarkən ekranda heç nə görünmür — bu normaldır.'
    Say '   Parolu yadda saxlayın: imzalayarkən yenə soruşulacaq.'
    foreach ($name in 'current', 'next') {
        if (Test-Path (Join-Path $keyDir "$name.private.pem")) { continue }
        $code = Invoke-ReleaseSign @('keygen', '--name', $name, '--out', $keyDir)
        if ($code -ne 0) {
            Stop-With "'$name' açarı yaradılmadı (ehtimal ki, parollar fərqli idi və ya qısa idi)." 'Skripti yenidən başladın; yaradılmış açarlar silinmir və ikinci dəfə soruşulmur.'
        }
    }
    Good 'açarlar yaradıldı'
}

$builtIn = Get-PemBody ([IO.File]::ReadAllText($stagingKeysFile).Split(@('public const string Next'), 'None')[0])
$mine = Get-PemBody ([IO.File]::ReadAllText($currentPublic))
if ($mine -ne $builtIn) {
    Write-Host ''
    Write-Host 'DİQQƏT: bu kompüterdəki staging açarı repodakı staging açarı deyil.' -ForegroundColor Yellow
    Say '   Kiosklar və test serveri yalnız repoya yazılmış staging public açarlarını tanıyır.'
    Say '   Bu açarla imzalanan paketi server "UnknownKey" ilə rədd edərdi, ona görə imzalama edilmir.'
    Say ''
    Say '   Nə etmək lazımdır (bir dəfəlik):'
    Say "   1. Bu iki faylı göndərin — YALNIZ .public.pem fayllarını, .private.pem və passphrase.txt-ni HEÇ VAXT:"
    Say "        $currentPublic"
    Say "        $(Join-Path $keyDir 'next.public.pem')"
    Say '   2. Onlar ReleaseKeys.staging.cs-ə yazılacaq, sonra "Release packages" workflow-u yenidən işlədiləcək.'
    Say '   3. Sonra bu skripti yenidən başladın.'
    exit 1
}
Good "açar yerindədir və repodakı staging açarıdır ($keyDir)"

# ---------------------------------------------------------------- 3. Endirmə
Step '3/5  Son CI paketləri endirilir'
if (-not $RunId) {
    $RunId = (& gh run list --repo zahid2000/NeoPrint --workflow release-package.yml --status success --limit 1 --json databaseId --jq '.[0].databaseId' 2>$null)
    if (-not $RunId) {
        Stop-With '"Release packages" workflow-unun uğurlu işi tapılmadı.' 'GitHub-da Actions → "Release packages" → "Run workflow" (versiya, məs. 0.8.0-pilot.2), bitəndən sonra skripti yenidən başladın.'
    }
}
$RunId = "$RunId".Trim()
if (-not $OutDir) { $OutDir = Join-Path $env:USERPROFILE ".neoprint\staging-packages\$RunId" }
if (Test-Path $OutDir) {
    Stop-With "$OutDir artıq var (bu iş əvvəl endirilib)." "Köhnə qovluğu silin və ya başqa yer seçin: -OutDir C:\yol\qovluq"
}

$pattern = 'kiosk-release-*-staging'
if ($Platform -ne 'all') { $pattern = "kiosk-release-*-$Platform-staging" }
Say "   İş: $RunId → $OutDir"
& gh run download $RunId --repo zahid2000/NeoPrint --dir $OutDir --pattern $pattern
if ($LASTEXITCODE -ne 0) {
    Stop-With 'Artefakt endirilmədi.' 'Artefaktlar 30 gün saxlanılır — köhnədirsə "Release packages" workflow-unu yenidən işlədin. İnternet bağlantısını yoxlayın.'
}

$dirs = @(Get-ChildItem $OutDir -Directory | Where-Object { Get-ChildItem $_.FullName -Filter '*.manifest.json' })
if ($dirs.Count -eq 0 -and (Get-ChildItem $OutDir -Filter '*.manifest.json')) { $dirs = @(Get-Item $OutDir) }
if ($dirs.Count -eq 0) {
    Stop-With 'Endirilən qovluqda manifest yoxdur.' "Qovluğa baxın: $OutDir. Bu işdə staging paketləri olmaya bilər — yeni iş üçün skripti -RunId olmadan başladın."
}
Good "$($dirs.Count) artefakt endirildi"

# ---------------------------------------------------------------- 4. İmza və yoxlama
Step '4/5  İmzalanır və yoxlanılır'
if (-not (Test-Path (Join-Path $keyDir 'passphrase.txt')) -and -not $env:NEOPRINT_STAGING_SIGNING_PASSPHRASE) {
    Say '   Staging açarının parolu soruşulacaq (hər artefakt üçün bir dəfə).'
}
foreach ($dir in $dirs) {
    Say "   $($dir.Name)"
    $code = Invoke-ReleaseSign @('sign-staging', '--dir', $dir.FullName, '--key-dir', $keyDir)
    if ($code -ne 0) {
        Stop-With "$($dir.Name) imzalanmadı." 'Yuxarıdakı ingiliscə sətirə baxın: "wrong passphrase" — parol səhvdir; "not the file in the manifest" — paket zədələnib, qovluğu silib skripti yenidən başladın; "production package" — bu staging paketi deyil.'
    }
    foreach ($manifest in Get-ChildItem $dir.FullName -Filter '*.manifest.json') {
        $package = $manifest.FullName.Substring(0, $manifest.FullName.Length - '.manifest.json'.Length)
        $code = Invoke-ReleaseSign @('verify', '--manifest', $manifest.FullName, '--package', $package, '--key-set', 'staging')
        if ($code -ne 0) {
            Stop-With "$($manifest.Name) yoxlamadan keçmədi." 'Bu paketi yükləməyin. Qovluğu silib skripti yenidən başladın; təkrarlanarsa yuxarıdakı mətni göndərin.'
        }
    }
}
Good 'bütün paketlər imzalandı və staging kioskunun yoxlamasından keçdi'

# ---------------------------------------------------------------- 5. Nəticə
Step '5/5  Panelə yükləmə'
Say "   Paketlər bu qovluqdadır: $OutDir"
Say ''
Say '   Hər paket üçün panelə ÜÇ fayl yüklənir:'
foreach ($dir in $dirs) {
    foreach ($manifest in Get-ChildItem $dir.FullName -Filter '*.manifest.json') {
        $package = Split-Path -Leaf ($manifest.FullName.Substring(0, $manifest.FullName.Length - '.manifest.json'.Length))
        Say ''
        Say "   • $package   (qovluq: $($dir.Name))"
        Say "       Paket    → $package"
        Say "       Manifest → $($manifest.Name)"
        Say "       İmza     → $($manifest.Name).sig"
    }
}
Say ''
Say '   Paneldə:'
Say '   1. Test serverinin admin panelinə SuperAdmin ilə daxil olun → menyu "İdarəetmə" → "Buraxılışlar".'
Say '   2. "Paket yüklə" → üç faylı yuxarıdakı kimi seçin → kanal "Pilot" → "Yüklə".'
Say '   3. Nə görməlisiniz: siyahıda yeni sətir — komponent, versiya, platforma, açar dəsti "staging",'
Say '      açar e097a6082f6d3545 (və ya fa8b3e094c232de1), kanal "Pilot".'
Say '   Xəta görsəniz:'
Say '   • "...açarlarından biri ilə imzalanmayıb" və ya "...dəsti üçün yığılmayıb" — server başqa dəstdədir'
Say '     və ya paket production üçündür; test serveri staging paketi qəbul edir.'
Say '   • "Paket faylı manifestdəki fayl deyil" və ya "faylının adı manifestdəki addan fərqlidir" — fayllar qarışıb:'
Say '     eyni adla başlayan paketi, onun .manifest.json-unu və .manifest.json.sig-ini seçin.'
Say '   • "...versiyası artıq başqa paketlə yüklənib" — bu versiya əvvəl yüklənib; yeni versiya ilə workflow-u işlədin.'
Say '   • Eyni paketi ikinci dəfə yükləmək xəta vermir — siyahıda eyni sətir qalır.'
Say ''
Say 'Hazırdır.'
