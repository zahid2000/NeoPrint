#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# NeoPrint dev-kiosk: builds and tests the kiosk solution on Linux inside the
# .NET SDK container, the way the CI's Linux leg does (docs/DEV_ENVIRONMENT.md,
# section 5). Needs only Docker; run from Git Bash, WSL or Linux:
#
#   bash tools/dev-kiosk/linux-check.sh
#
# The repository is mounted read-only and copied without bin/ and obj/, so the
# Windows build output on this machine is never touched. NuGet packages are
# kept in the "neoprint-nuget" Docker volume between runs. fontconfig is
# installed because SkiaSharp (document previews) needs it; the CI runner
# already has it.
# ---------------------------------------------------------------------------
set -euo pipefail

root="$(cd "$(dirname "$0")/../.." && pwd)"
# Git Bash hands Docker a Windows path; elsewhere the plain path is right.
if command -v cygpath >/dev/null 2>&1; then root="$(cygpath -w "$root")"; fi

MSYS_NO_PATHCONV=1 docker run --rm \
  -v "$root:/src:ro" \
  -v neoprint-nuget:/root/.nuget/packages \
  mcr.microsoft.com/dotnet/sdk:8.0 \
  bash -c '
    set -euo pipefail
    apt-get update -qq >/dev/null
    apt-get install -y -qq libfontconfig1 fonts-dejavu-core >/dev/null
    mkdir -p /w
    cd /src
    tar --exclude=bin --exclude=obj -cf - global.json Directory.Build.props Directory.Packages.props desktop backend/shared | tar -xf - -C /w
    cd /w
    dotnet build desktop/NeoPrint.Kiosk.sln -c Release -p:EnableWindowsTargeting=true -v q
    dotnet test desktop/NeoPrint.Kiosk.sln -c Release --no-build -p:EnableWindowsTargeting=true
  '
