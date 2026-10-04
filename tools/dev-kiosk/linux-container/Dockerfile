# The Linux container kiosk for the real-stack checks (neoprint-kshot, KSK-9002;
# docs/DEV_ENVIRONMENT.md 7j). Built by tools/dev-kiosk/build-linux-kiosk.sh,
# which publishes NeoPrint.Kiosk for linux-x64 into ./app first.
#
# A screen (Xvfb) and xdotool / ImageMagick to drive and photograph it, and the
# converters a provisioned kiosk has (desktop/provisioning/linux/provision.sh):
# LibreOffice for Office files, Ghostscript for PostScript / PCL / XPS, and the
# same fonts - without them an Office file from the flash drive cannot be
# opened here (T-168). The converters run as the application under prlimit
# (KioskSettings.ConverterUser is empty here); the separate offline account is
# the provisioning script's and is checked on real devices (T-094).
FROM mcr.microsoft.com/dotnet/runtime:8.0

RUN apt-get update && apt-get install -y --no-install-recommends \
      xvfb xdotool imagemagick libx11-6 libice6 libsm6 libfontconfig1 libxrandr2 libxi6 libxcursor1 \
      libreoffice-core libreoffice-writer libreoffice-calc libreoffice-impress \
      ghostscript \
      fonts-dejavu fonts-dejavu-core fonts-noto-core fonts-liberation fonts-crosextra-carlito fonts-crosextra-caladea \
      ca-certificates procps \
    && rm -rf /var/lib/apt/lists/*

COPY app /opt/neoprint
COPY run.sh /run.sh
RUN chmod +x /run.sh

ENV NEOPRINT_KIOSK_ENVIRONMENT=Development DISPLAY=:99
ENTRYPOINT ["/run.sh"]
