#!/bin/sh
# ---------------------------------------------------------------------------
# Create the buckets the backend expects, then exit.
#
# Runs as the one-shot `minio-init` container. Idempotent: `mc mb -p` is a
# no-op when the bucket already exists.
# ---------------------------------------------------------------------------
set -eu

ENDPOINT="${MINIO_ENDPOINT:-http://minio:9000}"
ACCESS_KEY="${MINIO_ROOT_USER:?MINIO_ROOT_USER is required}"
SECRET_KEY="${MINIO_ROOT_PASSWORD:?MINIO_ROOT_PASSWORD is required}"

BUCKET_FILES="${NEOPRINT_STORAGE_SEED_BUCKET_FILES:-neoprint-files}"
BUCKET_RECEIPTS="${NEOPRINT_STORAGE_SEED_BUCKET_RECEIPTS:-neoprint-receipts}"
BUCKET_TEMP="${NEOPRINT_STORAGE_SEED_BUCKET_TEMP:-neoprint-temp}"

log() { printf '[minio-init] %s\n' "$*"; }

log "connecting to $ENDPOINT"
attempt=0
until mc alias set neoprint "$ENDPOINT" "$ACCESS_KEY" "$SECRET_KEY" >/dev/null 2>&1; do
    attempt=$((attempt + 1))
    if [ "$attempt" -ge 30 ]; then
        echo "[minio-init] ERROR: could not connect to MinIO at $ENDPOINT" >&2
        exit 1
    fi
    sleep 2
done

for bucket in "$BUCKET_FILES" "$BUCKET_RECEIPTS" "$BUCKET_TEMP"; do
    log "ensuring bucket $bucket"
    mc mb -p "neoprint/$bucket" >/dev/null 2>&1 || true
done

# Objects are encrypted by the application before they are written (plan
# section 2.13), so the buckets stay private and no anonymous policy is set.
log "done"
