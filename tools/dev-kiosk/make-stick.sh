#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# NeoPrint dev-kiosk: a setup stick for a real Linux kiosk on the TEST server
# (T-333): what docs/KIOSK_SETUP.md section 11.3 puts on a stick, from the
# current code, with the STAGING keys - the test server's - built in. Not for
# a production kiosk: that stick is made from a signed production release.
#
#   bash tools/dev-kiosk/make-stick.sh --api-url https://<HOST> [--out <folder>] [--version 0.9.0-m19b]
#
# <HOST> is the test server's name (docs/TEST_SERVER.md), never written into
# the repository. The folder it prints is copied, as it is, onto a USB stick
# (FAT32 or exFAT); on the kiosk:
#
#   sudo bash /media/$USER/*/install.sh
#
# install.sh asks for an enrollment token (Enter for none: the kiosk then shows
# an activation code) and runs provisioning/provision.sh with the package on
# the stick. Nothing secret is on the stick: no token, no key, no password.
# Self-contained linux-x64 builds, as the release workflow makes them.
# ---------------------------------------------------------------------------
set -euo pipefail

api_url=""
version="0.9.0-m19b"
out="${TMPDIR:-/tmp}/neoprint-stick"
while [ $# -gt 0 ]; do
    case "$1" in
        --api-url) api_url="${2:?--api-url needs a value}"; shift 2 ;;
        --out)     out="${2:?--out needs a value}"; shift 2 ;;
        --version) version="${2:?--version needs a value}"; shift 2 ;;
        *) echo "unknown option: $1" >&2; exit 2 ;;
    esac
done
case "$api_url" in
    https://*) ;;
    *) echo "--api-url https://<HOST>: the test server's name from docs/TEST_SERVER.md" >&2; exit 2 ;;
esac

root="$(cd "$(dirname "$0")/../.." && pwd)"
win() { if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else printf '%s' "$1"; fi; }

rm -rf "$out"
mkdir -p "$out/package"
for project in NeoPrint.Kiosk NeoPrint.Kiosk.Agent; do
    target="$out/package/app"
    [ "$project" = NeoPrint.Kiosk.Agent ] && target="$out/package/agent"
    echo "publishing $project $version (linux-x64, self-contained, staging keys)"
    dotnet publish "$(win "$root/desktop/src/$project/$project.csproj")" -c Release -r linux-x64 --self-contained true \
        -p:EnableWindowsTargeting=true -p:ReleaseKeySet=staging -p:Version="$version" -o "$(win "$target")" -nologo -v q >/dev/null
done
printf '%s' "$version" >"$out/package/VERSION"
cp -R "$root/desktop/provisioning/linux" "$out/provisioning"

# The one command on the kiosk. The token is read without being shown and is
# never written to the stick.
cat >"$out/install.sh" <<SCRIPT
#!/bin/bash
# NeoPrint kiosk setup from this stick (docs/KIOSK_SETUP.md section 11).
#   sudo bash /media/\$USER/*/install.sh
set -euo pipefail
here="\$(cd "\$(dirname "\$0")" && pwd)"
if [ "\$(id -u)" -ne 0 ]; then echo "Run it with sudo: sudo bash \$0" >&2; exit 1; fi
echo "NeoPrint $version - server: $api_url"
read -r -s -p "Enrollment token from the admin panel (Enter for none): " token
echo
args=(--api-url "$api_url" --offline-package "\$here/package" --channel pilot)
if [ -n "\$token" ]; then args+=(--enrollment-token "\$token"); fi
bash "\$here/provisioning/provision.sh" "\${args[@]}"
echo
echo "Done. Take the stick out and restart the computer: sudo reboot"
SCRIPT

echo
echo "The stick's files: $out"
echo "Copy everything in that folder onto a USB stick; on the kiosk: sudo bash /media/\$USER/*/install.sh"
