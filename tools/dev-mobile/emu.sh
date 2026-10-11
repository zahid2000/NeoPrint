#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# NeoPrint dev-mobile: drives the app on an Android emulator (or a phone over
# USB) from the shell - screenshots, taps and typing through adb, and a look
# at what the app keeps on the device (docs/DEV_ENVIRONMENT.md, section 6,
# "Emulyatorda real axın"). Run from Git Bash, WSL or Linux:
#
#   bash tools/dev-mobile/emu.sh install              # the debug APK that flutter built
#   bash tools/dev-mobile/emu.sh start                # open the app
#   bash tools/dev-mobile/emu.sh shot out.png         # screenshot (1080x2220 on the Pixel AVD)
#   bash tools/dev-mobile/emu.sh tap 540 2090         # device pixels, not the scaled picture's
#   bash tools/dev-mobile/emu.sh text 'dev-user@neoprint.local'
#   bash tools/dev-mobile/emu.sh key 4                # 4 = back, 66 = enter
#   bash tools/dev-mobile/emu.sh swipe 540 1700 540 400
#   bash tools/dev-mobile/emu.sh cache                # scan pages must not stay here
#   bash tools/dev-mobile/emu.sh storage              # names of the sealed values, never the values
#
# Only a debug build lets adb look inside the app (run-as).
# ---------------------------------------------------------------------------
set -euo pipefail

app="az.neoprint.neoprint"
root="$(cd "$(dirname "$0")/../.." && pwd)"
adb="${ADB:-${LOCALAPPDATA:+$LOCALAPPDATA/Android/Sdk/platform-tools/adb.exe}}"
adb="${adb:-adb}"

# Git Bash would turn "/sdcard/..." and "cache/..." arguments into Windows paths.
export MSYS_NO_PATHCONV=1

"$adb" get-state >/dev/null 2>&1 || { echo "No emulator or phone is connected (adb devices) - nothing done."; exit 1; }

command="${1:-}"
shift || true

case "$command" in
  install)
    apk="$root/mobile/build/app/outputs/flutter-apk/app-debug.apk"
    [ -f "$apk" ] || { echo "No debug APK - run: cd mobile && flutter build apk --debug"; exit 1; }
    MSYS_NO_PATHCONV=0 "$adb" install -r "$apk"
    ;;
  start)
    "$adb" shell monkey -p "$app" -c android.intent.category.LAUNCHER 1 >/dev/null
    ;;
  stop)
    "$adb" shell am force-stop "$app"
    ;;
  shot)
    [ $# -eq 1 ] || { echo "usage: emu.sh shot <file.png>"; exit 1; }
    "$adb" exec-out screencap -p > "$1"
    echo "$1"
    ;;
  tap)
    "$adb" shell input tap "$1" "$2"
    ;;
  text)
    # A slow emulator drops characters of a long string: type in short pieces
    # and look at a screenshot before going on.
    "$adb" shell input text "$1"
    ;;
  key)
    "$adb" shell input keyevent "$1"
    ;;
  swipe)
    "$adb" shell input swipe "$1" "$2" "$3" "$4" "${5:-400}"
    ;;
  cache)
    "$adb" shell "run-as $app ls -R cache"
    ;;
  storage)
    # The file holds sealed values only; even so, only their names are shown.
    "$adb" shell "run-as $app cat shared_prefs/neoprint_secure_storage.xml" \
      | sed -n 's/.*<string name="\([^"]*\)">.*/\1 (sealed)/p'
    ;;
  *)
    sed -n '2,19p' "$0"
    exit 1
    ;;
esac
