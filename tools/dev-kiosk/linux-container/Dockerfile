# The Linux container kiosk for the real-stack checks (neoprint-kshot, KSK-9002;
# neoprint-kupd for the update checks; docs/DEV_ENVIRONMENT.md 7j). Built by
# tools/dev-kiosk/build-linux-kiosk.sh, which publishes NeoPrint.Kiosk and the
# agent for linux-x64 into ./app and ./agent and copies the provisioning
# scripts into ./provisioning first.
#
# A screen (Xvfb) and xdotool / ImageMagick to drive and photograph it, and the
# converters a provisioned kiosk has (desktop/provisioning/linux/provision.sh):
# LibreOffice for Office files, Ghostscript for PostScript / PCL / XPS, and the
# same fonts - without them an Office file from the flash drive cannot be
# opened here (T-168). The converters run as the application under prlimit
# (KioskSettings.ConverterUser is empty here); the separate offline account is
# the provisioning script's and is checked on real devices (T-094).
#
# The layout is a provisioned kiosk's (docs/KIOSK_UPDATES.md section 2, T-321):
# /opt/neoprint/{app,agent,provisioning}/versions/<version> and a "current"
# link each. The agent is the main process and starts the UI; run.sh starts the
# agent again when it exits, as systemd does, with the same guard before it.
FROM mcr.microsoft.com/dotnet/runtime:8.0

RUN apt-get update && apt-get install -y --no-install-recommends \
      xvfb xdotool imagemagick libx11-6 libice6 libsm6 libfontconfig1 libxrandr2 libxi6 libxcursor1 \
      libreoffice-core libreoffice-writer libreoffice-calc libreoffice-impress \
      ghostscript \
      fonts-dejavu fonts-dejavu-core fonts-noto-core fonts-liberation fonts-crosextra-carlito fonts-crosextra-caladea \
      ca-certificates procps \
    && rm -rf /var/lib/apt/lists/*

ARG VERSION=0.0.0-dev
ARG PROVISIONING_VERSION=0.4.0

COPY app /opt/neoprint/app/versions/${VERSION}
COPY agent /opt/neoprint/agent/versions/${VERSION}
COPY provisioning /opt/neoprint/provisioning/versions/${PROVISIONING_VERSION}
COPY run.sh /run.sh

RUN set -eu; \
    for component in app agent; do \
      printf '%s' "${VERSION}" > "/opt/neoprint/$component/versions/${VERSION}/.neoprint-complete"; \
      ln -s "versions/${VERSION}" "/opt/neoprint/$component/current"; \
    done; \
    printf '%s' "${PROVISIONING_VERSION}" > "/opt/neoprint/provisioning/versions/${PROVISIONING_VERSION}/.neoprint-complete"; \
    ln -s "versions/${PROVISIONING_VERSION}" /opt/neoprint/provisioning/current; \
    install -d /usr/local/lib/neoprint; \
    install -m 755 "/opt/neoprint/provisioning/versions/${PROVISIONING_VERSION}/neoprint-agent-guard" /usr/local/lib/neoprint/neoprint-agent-guard; \
    chmod +x /run.sh /opt/neoprint/app/current/NeoPrint.Kiosk /opt/neoprint/agent/current/NeoPrint.Kiosk.Agent \
      "/opt/neoprint/provisioning/versions/${PROVISIONING_VERSION}/provision.sh"

ENV NEOPRINT_KIOSK_ENVIRONMENT=Development \
    DISPLAY=:99 \
    NEOPRINT_AGENT_Agent__UiExecutablePath=/opt/neoprint/app/current/NeoPrint.Kiosk \
    NEOPRINT_AGENT_Agent__UiHost=watchdog \
    NEOPRINT_AGENT_Agent__ProvisioningHelper= \
    NEOPRINT_AGENT_Agent__ProvisioningScriptArgs=--dry-run

ENTRYPOINT ["/run.sh"]
