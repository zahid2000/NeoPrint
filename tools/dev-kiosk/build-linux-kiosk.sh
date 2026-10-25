#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# NeoPrint dev-kiosk: builds the Linux container kiosk image (Xvfb, xdotool,
# LibreOffice, Ghostscript, fonts - linux-container/Dockerfile) with the
# current kiosk and agent code, laid out as a provisioned kiosk
# (/opt/neoprint/{app,agent,provisioning}/versions + current; T-321), and
# (re)creates the container on the compose network. Its activation and spool
# live in a data volume and are kept, so an activated kiosk stays activated
# (docs/DEV_ENVIRONMENT.md, 7j). For a code-only change update-linux-kiosk.sh
# is faster. Run from Git Bash, WSL or Linux, with the compose stack up:
#
#   bash tools/dev-kiosk/build-linux-kiosk.sh                                   # neoprint-kshot (KSK-9002)
#   bash tools/dev-kiosk/build-linux-kiosk.sh --enrollment-token <TOKEN>        # first activation
#   CONTAINER=neoprint-kupd VERSION=0.8.0-m17.1 bash tools/dev-kiosk/build-linux-kiosk.sh --enrollment-token <TOKEN>
#
# CONTAINER (default neoprint-kshot) and VOLUME (default <container>-data) name
# the container and its data; VERSION (default 0.0.0-dev) is the version the
# build reports and the update checks compare (T-321). KBPS limits package
# downloads (0: none) - for watching a download that breaks off.
# ---------------------------------------------------------------------------
set -euo pipefail

container="${CONTAINER:-neoprint-kshot}"
volume="${VOLUME:-$container-data}"
version="${VERSION:-0.0.0-dev}"
kbps="${KBPS:-0}"
root="$(cd "$(dirname "$0")/../.." && pwd)"
context="$(mktemp -d)"
trap 'rm -rf "$context"' EXIT

docker network inspect neoprint_default >/dev/null 2>&1 || { echo "The compose network neoprint_default is not up - run docker compose up first."; exit 1; }

cp "$root/tools/dev-kiosk/linux-container/Dockerfile" "$root/tools/dev-kiosk/linux-container/run.sh" "$context/"
for project in NeoPrint.Kiosk NeoPrint.Kiosk.Agent; do
  out="$context/app"
  [ "$project" = NeoPrint.Kiosk.Agent ] && out="$context/agent"
  dotnet publish "$root/desktop/src/$project/$project.csproj" \
    -c Release -r linux-x64 --self-contained false -p:EnableWindowsTargeting=true -p:ReleaseKeySet=staging \
    -p:Version="$version" -o "$out" -v q
done
cp -R "$root/desktop/provisioning/linux" "$context/provisioning"

# Git Bash would turn the context into a path docker cannot use; hand it over
# as a Windows path there.
ctx="$context"
if command -v cygpath >/dev/null 2>&1; then ctx="$(cygpath -w "$context")"; fi
MSYS_NO_PATHCONV=1 docker build -t "$container-image" --build-arg VERSION="$version" "$ctx"

docker rm -f "$container" >/dev/null 2>&1 || true
MSYS_NO_PATHCONV=1 docker run -d --name "$container" --network neoprint_default \
  --tmpfs /media/kiosk/NEOSTICK:size=20m \
  -v "$volume":/var/lib/neoprint \
  -e NEOPRINT_KIOSK_Kiosk__UpdateDownloadKBps="$kbps" \
  "$container-image" --api-url http://neoprint-api:8080 "$@" >/dev/null

# Proof it took: the converters are there, the agent runs and has started the UI.
sleep 8
MSYS_NO_PATHCONV=1 docker exec "$container" sh -c 'soffice --version | head -1; gs --version; readlink /opt/neoprint/app/current; pgrep -f NeoPrint.Kiosk.Agent >/dev/null && echo "agent running"; pgrep -xf "/opt/neoprint/app/current/NeoPrint.Kiosk" >/dev/null && echo "kiosk running"'
echo "Kiosk container $container ($version) built and started."
