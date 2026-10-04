#!/bin/sh
# Starts a screen and the kiosk on it (tools/dev-kiosk/linux-container/Dockerfile).
rm -f /tmp/.X99-lock /tmp/.X11-unix/X99
Xvfb :99 -screen 0 1920x1080x24 -nolisten tcp &
sleep 2
# No window manager here, so FullScreen has nobody to honour it: size the window by hand.
(sleep 8; xdotool search --sync --name NeoPrint | while read w; do xdotool windowmove $w 0 0 windowsize $w 1920 1080; done) &
exec dotnet /opt/neoprint/NeoPrint.Kiosk.dll "$@"
