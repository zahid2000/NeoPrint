#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# NeoPrint dev-kiosk: a signed STAGING release package of the current code,
# for checking updates on the container kiosk (docs/KIOSK_UPDATES.md, T-321).
# Builds what the release workflow builds for one component (linux-x64,
# staging keys), writes its manifest and signs it with the staging key on this
# machine (release-sign sign-staging; docs/RELEASE_SIGNING.md).
#
#   bash tools/dev-kiosk/make-release.sh app 0.8.0-m17.2
#   bash tools/dev-kiosk/make-release.sh app 0.8.0-m17.3 --broken     # starts and dies at once: for the rollback
#   bash tools/dev-kiosk/make-release.sh agent 0.8.0-m17.4
#   bash tools/dev-kiosk/make-release.sh provisioning 0.5.0
#
# Prints the folder with <package>, <package>.manifest.json and .sig - what
# node tools/dev-kiosk/updates.mjs upload takes. Framework-dependent builds, as
# the container kiosk runs (the release workflow's are self-contained).
# ---------------------------------------------------------------------------
set -euo pipefail

component="${1:?app, agent or provisioning}"
version="${2:?version, e.g. 0.8.0-m17.2}"
broken="${3:-}"
root="$(cd "$(dirname "$0")/../.." && pwd)"
out="${RELEASES:-${TMPDIR:-/tmp}/neoprint-releases}/$component-$version"
build="$(mktemp -d)"
trap 'rm -rf "$build"' EXIT

case "$component" in
  app) project=NeoPrint.Kiosk; name=App ;;
  agent) project=NeoPrint.Kiosk.Agent; name=Agent ;;
  provisioning) project=""; name=Provisioning ;;
  *) echo "app, agent or provisioning" >&2; exit 2 ;;
esac

if [ -n "$project" ]; then
  dotnet publish "$root/desktop/src/$project/$project.csproj" \
    -c Release -r linux-x64 --self-contained false -p:EnableWindowsTargeting=true -p:ReleaseKeySet=staging \
    -p:Version="$version" -o "$build/files" -v q
else
  cp -R "$root/desktop/provisioning/linux" "$build/files"
fi

if [ "$broken" = "--broken" ]; then
  # Signed like any other, and the UI cannot start: the health check has to catch it.
  echo "not an assembly" >"$build/files/$project.dll"
fi

rm -rf "$out"
mkdir -p "$out"
package="neoprint-kiosk-$component-$version-linux-x64-staging.tar.gz"
tar --force-local -czf "$out/$package" -C "$build/files" .

dotnet build "$root/tools/release-sign" -c Release -nologo -v q >/dev/null
tool="dotnet $root/tools/release-sign/bin/Release/net8.0/release-sign.dll"
$tool manifest --package "$out/$package" --component "$name" --platform linux-x64 --key-set staging \
  --version "$version" --min-agent-version 0.1.0 --commit "$(git -C "$root" rev-parse --short HEAD)" --out "$out/$package.manifest.json" >/dev/null
$tool sign-staging --manifest "$out/$package.manifest.json" --package "$out/$package" | tail -1

echo "$out"
