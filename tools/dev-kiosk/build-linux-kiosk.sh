#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# NeoPrint dev-kiosk: builds the Linux container kiosk image (neoprint-kshot;
# Xvfb, xdotool, LibreOffice, Ghostscript, fonts - linux-container/Dockerfile)
# with the current kiosk code and (re)creates the container on the compose
# network. Its activation and spool live in the neoprint-kshot-data volume and
# are kept, so an activated kiosk stays activated (docs/DEV_ENVIRONMENT.md, 7j).
# For a code-only change update-linux-kiosk.sh is faster. Run from Git Bash,
# WSL or Linux, with the compose stack up:
#
#   bash tools/dev-kiosk/build-linux-kiosk.sh
#   bash tools/dev-kiosk/build-linux-kiosk.sh --enrollment-token <TOKEN>   # first activation
# ---------------------------------------------------------------------------
set -euo pipefail

container=neoprint-kshot
root="$(cd "$(dirname "$0")/../.." && pwd)"
context="$(mktemp -d)"
trap 'rm -rf "$context"' EXIT

docker network inspect neoprint_default >/dev/null 2>&1 || { echo "The compose network neoprint_default is not up - run docker compose up first."; exit 1; }

cp "$root/tools/dev-kiosk/linux-container/Dockerfile" "$root/tools/dev-kiosk/linux-container/run.sh" "$context/"
dotnet publish "$root/desktop/src/NeoPrint.Kiosk/NeoPrint.Kiosk.csproj" \
  -c Release -r linux-x64 --self-contained false -p:EnableWindowsTargeting=true -o "$context/app" -v q

# Git Bash would turn the context into a path docker cannot use; hand it over
# as a Windows path there.
ctx="$context"
if command -v cygpath >/dev/null 2>&1; then ctx="$(cygpath -w "$context")"; fi
MSYS_NO_PATHCONV=1 docker build -t neoprint-kshot "$ctx"

docker rm -f "$container" >/dev/null 2>&1 || true
MSYS_NO_PATHCONV=1 docker run -d --name "$container" --network neoprint_default \
  --tmpfs /media/kiosk/NEOSTICK:size=20m \
  -v neoprint-kshot-data:/var/lib/neoprint \
  neoprint-kshot --api-url http://neoprint-api:8080 "$@" >/dev/null

# Proof it took: the converters are there and the kiosk is running.
sleep 5
MSYS_NO_PATHCONV=1 docker exec "$container" sh -c 'soffice --version | head -1; gs --version; pidof dotnet >/dev/null && echo "kiosk running"'
echo "Kiosk container $container built and started."
