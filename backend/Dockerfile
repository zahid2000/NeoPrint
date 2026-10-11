# ---------------------------------------------------------------------------
# NeoPrint backend image.
#
# The runtime needs more than the .NET runtime: document conversion runs as
# separate processes (plan section 2.2), so LibreOffice, the Ghostscript family
# and the PDFium native library are part of the image. The same image is the
# API and the converter container; everything that parses a user's file -
# those programs and the converter tool, NeoPrint.Converter.dll, published
# next to the API - runs only in the converter (docs/adr/ADR-0005-isolated-converter.md).
#
# The build context is the REPOSITORY ROOT, not backend/, because the build
# needs global.json and the central MSBuild property files:
#
#   docker build -t neoprint-api -f backend/Dockerfile .
# ---------------------------------------------------------------------------

# ---------------------------------------------------------------------------
# Stage 1 — build GhostPCL and GhostXPS from source.
#
# Artifex publishes Linux binaries for gs only; gpcl6 (PCL/PRN) and gxps
# (XPS/OXPS) have to be built. They are used as separate, unmodified processes,
# which is what their AGPL-3.0 licence allows here (plan section 2.0).
# ---------------------------------------------------------------------------
FROM debian:bookworm-slim AS ghostpdl

ARG GHOSTPDL_VERSION=10.08.0
ARG GHOSTPDL_SHA256=

RUN set -eux; \
    apt-get update; \
    apt-get install -y --no-install-recommends \
        build-essential \
        ca-certificates \
        curl \
        libfontconfig1-dev \
        libfreetype6-dev \
        libjpeg62-turbo-dev \
        libpng-dev \
        zlib1g-dev; \
    rm -rf /var/lib/apt/lists/*

WORKDIR /src
RUN set -eux; \
    # Artifex tags releases as gs<version without dots>, e.g. 10.08.0 -> gs10080.
    tag="gs$(echo "${GHOSTPDL_VERSION}" | tr -d '.')"; \
    curl -fsSL -o ghostpdl.tar.gz \
        "https://github.com/ArtifexSoftware/ghostpdl-downloads/releases/download/${tag}/ghostpdl-${GHOSTPDL_VERSION}.tar.gz"; \
    if [ -n "${GHOSTPDL_SHA256}" ]; then echo "${GHOSTPDL_SHA256}  ghostpdl.tar.gz" | sha256sum -c -; fi; \
    tar -xzf ghostpdl.tar.gz; \
    rm ghostpdl.tar.gz

WORKDIR /src/ghostpdl-${GHOSTPDL_VERSION}
RUN set -eux; \
    ./configure \
        --prefix=/opt/ghostpdl \
        --without-x \
        --without-tesseract \
        --disable-gtk \
        --disable-cups; \
    make -j"$(nproc)" gpcl6 gxps; \
    mkdir -p /opt/ghostpdl/bin; \
    cp bin/gpcl6 bin/gxps /opt/ghostpdl/bin/; \
    strip /opt/ghostpdl/bin/gpcl6 /opt/ghostpdl/bin/gxps

# ---------------------------------------------------------------------------
# Stage 2 — fetch the PDFium native library.
#
# NeoPrint.Pdfium is our own P/Invoke wrapper (plan section 2.0); only the
# prebuilt native library is taken from bblanchon/pdfium-binaries.
# ---------------------------------------------------------------------------
FROM debian:bookworm-slim AS pdfium

ARG PDFIUM_RELEASE=chromium/8066
# The archive is checked before it is unpacked (T-100); the kiosk build pins
# the same value (desktop/src/NeoPrint.Kiosk/NeoPrint.Kiosk.csproj).
ARG PDFIUM_SHA256=0b43f405477cf2cfc4dbff06905093c3309756c6bca1fb9da99234a2ca97fed2

RUN set -eux; \
    apt-get update; \
    apt-get install -y --no-install-recommends ca-certificates curl; \
    rm -rf /var/lib/apt/lists/*; \
    mkdir -p /pdfium; \
    curl -fsSL -o /tmp/pdfium.tgz \
        "https://github.com/bblanchon/pdfium-binaries/releases/download/${PDFIUM_RELEASE}/pdfium-linux-x64.tgz"; \
    echo "${PDFIUM_SHA256}  /tmp/pdfium.tgz" | sha256sum -c -; \
    tar -xzf /tmp/pdfium.tgz -C /pdfium; \
    test -f /pdfium/lib/libpdfium.so

# ---------------------------------------------------------------------------
# Stage 3 — build the .NET application.
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build

WORKDIR /build

# Restore first, against the project files only, so that a source-only change
# does not invalidate the restore layer.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY backend/NeoPrint.sln ./backend/
COPY backend/src/ ./backend/src/
COPY backend/shared/ ./backend/shared/

RUN dotnet restore backend/src/NeoPrint.Api/NeoPrint.Api.csproj

RUN dotnet publish backend/src/NeoPrint.Api/NeoPrint.Api.csproj \
        --configuration Release \
        --no-restore \
        --output /app/publish

# ---------------------------------------------------------------------------
# Stage 4 — runtime.
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime

ENV DEBIAN_FRONTEND=noninteractive

RUN set -eux; \
    apt-get update; \
    apt-get install -y --no-install-recommends \
        # Office documents -> PDF (plan section 2.2). GUI packages are left out
        # deliberately; conversion always runs headless.
        libreoffice-core \
        libreoffice-writer \
        libreoffice-calc \
        libreoffice-impress \
        # PostScript / EPS / PDF handling.
        ghostscript \
        # Fonts, so that a converted document looks the same as on the kiosk.
        fonts-dejavu \
        fonts-liberation \
        fonts-crosextra-carlito \
        fonts-crosextra-caladea \
        fontconfig \
        # SkiaSharp needs fontconfig and freetype at runtime.
        libfontconfig1 \
        libfreetype6 \
        # Health checks.
        curl; \
    rm -rf /var/lib/apt/lists/*; \
    fc-cache -f

# GhostPCL / GhostXPS for PCL, PRN, XPS and OXPS.
COPY --from=ghostpdl /opt/ghostpdl/bin/gpcl6 /usr/local/bin/gpcl6
COPY --from=ghostpdl /opt/ghostpdl/bin/gxps  /usr/local/bin/gxps

# PDFium for page counts and preview rendering.
COPY --from=pdfium /pdfium/lib/libpdfium.so /usr/local/lib/libpdfium.so
RUN ldconfig

# Run as a non-root user.
RUN set -eux; \
    groupadd --system --gid 2000 neoprint; \
    useradd --system --uid 2000 --gid neoprint --no-create-home --shell /usr/sbin/nologin neoprint; \
    mkdir -p /var/lib/neoprint /tmp/neoprint; \
    chown -R neoprint:neoprint /var/lib/neoprint /tmp/neoprint

WORKDIR /app
COPY --from=build --chown=neoprint:neoprint /app/publish ./

USER neoprint

ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    HOME=/tmp/neoprint

EXPOSE 8080

ENTRYPOINT ["dotnet", "NeoPrint.Api.dll"]
