#!/bin/bash
# ---------------------------------------------------------------------------
# Initiate the single-node replica set once, then exit.
#
# Runs as the one-shot `mongo-init` container. Idempotent: if the replica set
# is already configured it reports that and exits successfully.
# ---------------------------------------------------------------------------
set -euo pipefail

HOST="${MONGO_HOST:-mongo:27017}"
USER="${MONGO_INITDB_ROOT_USERNAME:?MONGO_INITDB_ROOT_USERNAME is required}"
PASS="${MONGO_INITDB_ROOT_PASSWORD:?MONGO_INITDB_ROOT_PASSWORD is required}"
REPLICA_SET="${MONGO_REPLICA_SET:-rs0}"

log() { printf '[mongo-init] %s\n' "$*"; }

mongo_eval() {
    mongosh --quiet --host "$HOST" -u "$USER" -p "$PASS" --authenticationDatabase admin --eval "$1"
}

log "waiting for mongod at $HOST"
for attempt in $(seq 1 60); do
    if mongo_eval 'db.adminCommand({ ping: 1 })' >/dev/null 2>&1; then
        break
    fi
    if [ "$attempt" -eq 60 ]; then
        echo "[mongo-init] ERROR: mongod did not become reachable" >&2
        exit 1
    fi
    sleep 1
done

if mongo_eval 'rs.status().ok' 2>/dev/null | grep -q '^1$'; then
    log "replica set $REPLICA_SET is already initiated"
else
    log "initiating replica set $REPLICA_SET"
    mongo_eval "rs.initiate({ _id: '$REPLICA_SET', members: [{ _id: 0, host: '$HOST' }] })"
fi

log "waiting for the node to become PRIMARY"
for attempt in $(seq 1 60); do
    if mongo_eval 'db.hello().isWritablePrimary' 2>/dev/null | grep -q '^true$'; then
        log "node is PRIMARY; transactions are available"
        exit 0
    fi
    if [ "$attempt" -eq 60 ]; then
        echo "[mongo-init] ERROR: the node did not become PRIMARY" >&2
        exit 1
    fi
    sleep 1
done
