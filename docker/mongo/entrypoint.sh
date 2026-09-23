#!/bin/bash
# ---------------------------------------------------------------------------
# MongoDB entrypoint wrapper for the development environment.
#
# The backend needs transactions (MassTransit's transactional outbox and the
# wallet updates), which MongoDB only offers on a replica set. A replica set
# with authorization enabled also requires internal key-file authentication.
#
# A key file has to be owned by the mongodb user and be mode 400. A file
# bind-mounted from a Windows or macOS host cannot satisfy that, so the key is
# generated inside the container on first start and kept in the configdb volume.
# ---------------------------------------------------------------------------
set -euo pipefail

KEYFILE=/data/configdb/replica.key

if [ ! -f "$KEYFILE" ]; then
    echo "[mongo-entrypoint] generating replica set key file"
    mkdir -p "$(dirname "$KEYFILE")"
    openssl rand -base64 756 >"$KEYFILE"
fi

chmod 400 "$KEYFILE"
chown mongodb:mongodb "$KEYFILE" 2>/dev/null || true

exec /usr/local/bin/docker-entrypoint.sh "$@"
