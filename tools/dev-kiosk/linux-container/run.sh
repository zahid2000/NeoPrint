#!/bin/sh
# Starts a screen and the kiosk agent on it, which starts the UI
# (tools/dev-kiosk/linux-container/Dockerfile; docs/KIOSK_UPDATES.md, T-321).
#
#   run.sh [--api-url URL] [--enrollment-token TOKEN]
#
# The arguments become the UI's settings: the agent starts the UI without any.
while [ $# -gt 0 ]; do
    case "$1" in
        --api-url) export NEOPRINT_KIOSK_Kiosk__ApiBaseUrl="$2"; shift 2 ;;
        --enrollment-token) export NEOPRINT_KIOSK_Kiosk__EnrollmentToken="$2"; shift 2 ;;
        *) shift ;;
    esac
done

rm -f /tmp/.X99-lock /tmp/.X11-unix/X99
Xvfb :99 -screen 0 1920x1080x24 -nolisten tcp &
sleep 2

# No window manager here, so FullScreen has nobody to honour it: every kiosk
# window - the UI starts again after an update - is sized by hand.
(while true; do
    xdotool search --name NeoPrint 2>/dev/null | while read -r w; do xdotool windowmove "$w" 0 0 windowsize "$w" 1920 1080 2>/dev/null; done
    sleep 3
done) &

# What systemd does on a kiosk: the guard before each start, and the agent
# started again whenever it exits (its own update, RestartAgent).
while true; do
    /usr/local/lib/neoprint/neoprint-agent-guard
    /opt/neoprint/agent/current/NeoPrint.Kiosk.Agent
    echo "run.sh: the agent exited with $?; starting it again" >&2
    sleep 2
done
