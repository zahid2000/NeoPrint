#!/bin/sh
# ---------------------------------------------------------------------------
# Create the buckets the backend expects on every development object store,
# then exit.
#
# Runs as the one-shot `rustfs-init` container, from the same pinned RustFS
# image the stores run. That image ships curl, which signs S3 requests itself
# (--aws-sigv4), so no second image - and no second thing to keep pullable -
# is needed.
#
# Idempotent: a bucket that already exists is left alone.
#
# Objects are encrypted by the application before they are written (plan
# section 2.13), so the buckets stay private and no public policy is set.
# ---------------------------------------------------------------------------
set -eu

log() { echo "[rustfs-init] $*"; }
fail() { echo "[rustfs-init] ERROR: $*" >&2; exit 1; }

[ -n "${RUSTFS_ACCESS_KEY:-}" ] && [ -n "${RUSTFS_SECRET_KEY:-}" ] \
    || fail "RUSTFS_ACCESS_KEY and RUSTFS_SECRET_KEY are required"

ENDPOINTS=$(echo "${RUSTFS_ENDPOINTS:-http://rustfs:9000}" | tr ',' ' ')
BUCKETS="${NEOPRINT_STORAGE_SEED_BUCKET_FILES:-neoprint-files} \
${NEOPRINT_STORAGE_SEED_BUCKET_RECEIPTS:-neoprint-receipts} \
${NEOPRINT_STORAGE_SEED_BUCKET_TEMP:-neoprint-temp}"

# A signed S3 request. The credentials go to curl on stdin, not on its
# command line, where any process in the container could read them.
s3() {
    method=$1
    url=$2
    printf 'user = "%s:%s"\n' "$RUSTFS_ACCESS_KEY" "$RUSTFS_SECRET_KEY" \
        | curl -sS -o /tmp/rustfs-init.out -w '%{http_code}' -K - \
            --aws-sigv4 "aws:amz:us-east-1:s3" -X "$method" "$url" || echo 000
}

for endpoint in $ENDPOINTS; do
    log "preparing $endpoint"

    # The healthcheck says the store is ready; a signed request says the keys
    # work. Only a store that is not answering yet is worth waiting for - a
    # wrong key is a configuration error that retrying will not fix.
    attempt=0
    while :; do
        code=$(s3 GET "$endpoint/")
        case "$code" in
            200) break ;;
            000|502|503|504)
                attempt=$((attempt + 1))
                [ "$attempt" -lt 60 ] || fail "could not reach $endpoint (HTTP $code)"
                sleep 2
                ;;
            *) fail "$endpoint refused the request (HTTP $code): $(head -c 300 /tmp/rustfs-init.out)" ;;
        esac
    done

    for bucket in $BUCKETS; do
        code=$(s3 PUT "$endpoint/$bucket")
        case "$code" in
            200) log "$endpoint: $bucket ready" ;;
            409) log "$endpoint: $bucket already exists" ;;
            *) fail "$endpoint: could not create $bucket (HTTP $code): $(head -c 300 /tmp/rustfs-init.out)" ;;
        esac
    done
done

log "done"
