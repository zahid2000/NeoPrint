#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# NeoPrint dev-kiosk: puts the current kiosk code into the Linux container
# kiosk (neoprint-kshot, KSK-9002) - publishes NeoPrint.Kiosk for linux-x64,
# copies it over /opt/neoprint and restarts the container. Its activation and
# spool live in the neoprint-kshot-data volume and are kept
# (docs/DEV_ENVIRONMENT.md, 7j). Run from Git Bash, WSL or Linux:
#
#   bash tools/dev-kiosk/update-linux-kiosk.sh
# ---------------------------------------------------------------------------
set -euo pipefail

container="${1:-neoprint-kshot}"
root="$(cd "$(dirname "$0")/../.." && pwd)"
out="$(mktemp -d)"
trap 'rm -rf "$out"' EXIT

docker inspect "$container" >/dev/null 2>&1 || { echo "Container $container not found - nothing changed."; exit 1; }

dotnet publish "$root/desktop/src/NeoPrint.Kiosk/NeoPrint.Kiosk.csproj" \
  -c Release -r linux-x64 --self-contained false -p:EnableWindowsTargeting=true -p:ReleaseKeySet=staging -o "$out" -v q

# Git Bash would turn "container:/opt/..." into a Windows path; the source is
# handed over as a Windows path instead.
src="$out"
if command -v cygpath >/dev/null 2>&1; then src="$(cygpath -w "$out")"; fi
MSYS_NO_PATHCONV=1 docker cp "$src/." "$container:/opt/neoprint/"
docker restart "$container" >/dev/null

# Proof it took: the kiosk assembly in the container is the one just built.
MSYS_NO_PATHCONV=1 docker exec "$container" sh -c "ls -l --time-style=+%F_%T /opt/neoprint/NeoPrint.Kiosk.dll"
echo "Kiosk in $container updated and restarted."
