#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# NeoPrint dev-kiosk: puts the current kiosk and agent code into the Linux
# container kiosk (neoprint-kshot, KSK-9002) - publishes both for linux-x64,
# copies them over the versions "current" points at and restarts the
# container. Its activation and spool live in its data volume and are kept
# (docs/DEV_ENVIRONMENT.md, 7j). A shortcut for development: a real kiosk gets
# a new version through a signed package and the agent (T-321). Run from Git
# Bash, WSL or Linux:
#
#   bash tools/dev-kiosk/update-linux-kiosk.sh [container]
# ---------------------------------------------------------------------------
set -euo pipefail

container="${1:-neoprint-kshot}"
root="$(cd "$(dirname "$0")/../.." && pwd)"
out="$(mktemp -d)"
trap 'rm -rf "$out"' EXIT

docker inspect "$container" >/dev/null 2>&1 || { echo "Container $container not found - nothing changed."; exit 1; }

# The version the container runs now: the code changes, the version it reports does not.
version="$(MSYS_NO_PATHCONV=1 docker exec "$container" sh -c 'basename "$(readlink /opt/neoprint/app/current)"')"

for project in NeoPrint.Kiosk NeoPrint.Kiosk.Agent; do
  dotnet publish "$root/desktop/src/$project/$project.csproj" \
    -c Release -r linux-x64 --self-contained false -p:EnableWindowsTargeting=true -p:ReleaseKeySet=staging \
    -p:Version="$version" -o "$out/$project" -v q
done

# Git Bash would turn "container:/opt/..." into a Windows path; the source is
# handed over as a Windows path instead.
for pair in "NeoPrint.Kiosk:app" "NeoPrint.Kiosk.Agent:agent"; do
  src="$out/${pair%%:*}"
  if command -v cygpath >/dev/null 2>&1; then src="$(cygpath -w "$src")"; fi
  MSYS_NO_PATHCONV=1 docker cp "$src/." "$container:/opt/neoprint/${pair##*:}/current/"
done
docker restart "$container" >/dev/null

# Proof it took: the kiosk assembly in the container is the one just built.
MSYS_NO_PATHCONV=1 docker exec "$container" sh -c "ls -l --time-style=+%F_%T /opt/neoprint/app/current/NeoPrint.Kiosk.dll"
echo "Kiosk in $container ($version) updated and restarted."
