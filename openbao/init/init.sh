#!/bin/sh
# ---------------------------------------------------------------------------
# OpenBao bootstrap for the NeoPrint development environment.
#
# Runs once per `docker compose up` as the `openbao-init` container and:
#   1. initialises OpenBao if it has never been initialised,
#   2. unseals it,
#   3. mounts KV v2 at `neoprint`, enables AppRole auth and the file audit
#      device,
#   4. writes the neoprint-api / neoprint-worker policies and roles,
#   5. hands the API container a role_id and a secret_id file,
#   6. seeds the development secrets from .env.openbao-seed.
#
# It is idempotent: running it again only fixes what is missing and never
# overwrites a secret that already has a value.
#
# This script is for DEVELOPMENT ONLY. In production OpenBao is initialised by
# the operations team and this container is not deployed (docs/OPENBAO.md).
# ---------------------------------------------------------------------------
set -eu

BAO_ADDR="${BAO_ADDR:-http://openbao:8200}"
export BAO_ADDR

OUT_DIR="${OUT_DIR:-/openbao-out}"
POLICY_DIR="${POLICY_DIR:-/openbao-policies}"
SEED_FILE="${SEED_FILE:-/openbao-seed/.env.openbao-seed}"
KV_MOUNT="${BAO_KV_MOUNT:-neoprint}"

# The unprivileged user the API container runs as; it must be able to read the
# AppRole credential files written below.
API_UID="${NEOPRINT_API_UID:-2000}"
API_GID="${NEOPRINT_API_GID:-2000}"

INIT_FILE="$OUT_DIR/init.json"

# AppRole credentials live in their own directory: the API container mounts it,
# and must not also get sight of the unseal key and root token in init.json.
CREDS_DIR="$OUT_DIR/creds"

log() { printf '[openbao-init] %s\n' "$*"; }
die() { printf '[openbao-init] ERROR: %s\n' "$*" >&2; exit 1; }

# ---------------------------------------------------------------------------
# 1. Wait for the server to answer.
# ---------------------------------------------------------------------------
log "waiting for OpenBao at $BAO_ADDR"
attempt=0
until bao status >/dev/null 2>&1 || [ "$(bao status 2>&1 | grep -c 'Sealed\|Initialized')" -gt 0 ]; do
    attempt=$((attempt + 1))
    [ "$attempt" -ge 60 ] && die "OpenBao did not become reachable within 60 attempts"
    sleep 1
done
log "OpenBao is reachable"

is_initialized() {
    bao status 2>/dev/null | grep -q '^Initialized *true'
}

is_sealed() {
    bao status 2>/dev/null | grep -q '^Sealed *true'
}

# ---------------------------------------------------------------------------
# 2. Initialise (once) and unseal (every start).
# ---------------------------------------------------------------------------
mkdir -p "$OUT_DIR" "$CREDS_DIR"

if is_initialized; then
    log "already initialised"
    [ -f "$INIT_FILE" ] || die "OpenBao is initialised but $INIT_FILE is missing; \
remove the openbao volume to start over, or restore the file from your backup"
else
    log "initialising (1 unseal key, threshold 1 — development only)"
    bao operator init -key-shares=1 -key-threshold=1 >"$OUT_DIR/init.txt" 2>&1 \
        || die "bao operator init failed: $(cat "$OUT_DIR/init.txt")"

    unseal_key=$(grep -i 'Unseal Key 1:' "$OUT_DIR/init.txt" | sed 's/.*: *//' | tr -d '\r')
    root_token=$(grep -i 'Initial Root Token:' "$OUT_DIR/init.txt" | sed 's/.*: *//' | tr -d '\r')

    [ -n "$unseal_key" ] || die "could not read the unseal key from bao operator init"
    [ -n "$root_token" ] || die "could not read the root token from bao operator init"

    printf '{\n  "unseal_key": "%s",\n  "root_token": "%s"\n}\n' "$unseal_key" "$root_token" >"$INIT_FILE"
    chmod 600 "$INIT_FILE"
    rm -f "$OUT_DIR/init.txt"
    log "initialised; unseal key and root token written to .openbao/init.json (gitignored)"
fi

unseal_key=$(sed -n 's/.*"unseal_key": *"\([^"]*\)".*/\1/p' "$INIT_FILE")
root_token=$(sed -n 's/.*"root_token": *"\([^"]*\)".*/\1/p' "$INIT_FILE")
[ -n "$unseal_key" ] && [ -n "$root_token" ] || die "$INIT_FILE is malformed"

if is_sealed; then
    log "unsealing"
    bao operator unseal "$unseal_key" >/dev/null || die "unseal failed"
fi
log "unsealed"

BAO_TOKEN="$root_token"
export BAO_TOKEN

# ---------------------------------------------------------------------------
# 3. Mounts, auth methods and the audit device.
# ---------------------------------------------------------------------------
if bao secrets list 2>/dev/null | grep -q "^$KV_MOUNT/"; then
    log "KV v2 mount '$KV_MOUNT' already present"
else
    log "enabling KV v2 at '$KV_MOUNT'"
    bao secrets enable -path="$KV_MOUNT" -version=2 kv >/dev/null
fi

if bao auth list 2>/dev/null | grep -q '^approle/'; then
    log "AppRole auth already enabled"
else
    log "enabling AppRole auth"
    bao auth enable approle >/dev/null
fi

# From OpenBao 2.6 an audit device can no longer be enabled through the API; it
# is declared in openbao/config/openbao.hcl and comes up with the server. All
# that is left here is to confirm it is actually there, because plan section
# 2.13 requires the audit device to be active.
if bao audit list 2>/dev/null | grep -q '^file/'; then
    log "audit device already enabled"
else
    die "the file audit device is not enabled. It is declared in \
openbao/config/openbao.hcl; check the server log for a configuration error."
fi

# ---------------------------------------------------------------------------
# 4. Policies and roles.
# ---------------------------------------------------------------------------
for role in neoprint-api neoprint-worker; do
    policy_file="$POLICY_DIR/$role.hcl"
    [ -f "$policy_file" ] || die "missing policy file $policy_file"

    log "writing policy $role"
    bao policy write "$role" "$policy_file" >/dev/null

    log "configuring AppRole $role"
    bao write "auth/approle/role/$role" \
        token_policies="$role" \
        token_ttl=1h \
        token_max_ttl=24h \
        secret_id_num_uses=0 \
        secret_id_ttl=0 >/dev/null

    role_id=$(bao read -field=role_id "auth/approle/role/$role/role-id")
    secret_id=$(bao write -f -field=secret_id "auth/approle/role/$role/secret-id")

    # Remove first: a previous run may have left these read-only, and a bind
    # mount from a Windows or macOS host refuses to overwrite such a file even
    # for root.
    rm -f "$CREDS_DIR/$role.role_id" "$CREDS_DIR/$role.secret_id"

    printf '%s' "$role_id" >"$CREDS_DIR/$role.role_id"
    printf '%s' "$secret_id" >"$CREDS_DIR/$role.secret_id"

    # The API container runs as an unprivileged user (uid 2000), so it has to be
    # able to read these. Hand them over where the filesystem allows it; on a
    # bind mount from a Windows or macOS host chown does not stick, and the
    # files are made readable instead.
    #
    # This relaxation is DEVELOPMENT ONLY. In production the secret_id arrives
    # as a Docker secret, which the runtime mounts with the right ownership,
    # and this container is not deployed at all (docs/OPENBAO.md).
    for file in "$CREDS_DIR/$role.role_id" "$CREDS_DIR/$role.secret_id"; do
        chmod 600 "$file"
        if chown "$API_UID:$API_GID" "$file" 2>/dev/null; then
            chmod 640 "$file"
        else
            chmod 444 "$file"
        fi
    done
done
log "role_id and secret_id files written to .openbao/creds/"

# ---------------------------------------------------------------------------
# 5. Seed the development secrets.
# ---------------------------------------------------------------------------

# Decide which KV path a seed key belongs to (plan section 2.13).
# The first matching branch wins, so exact names are listed before wildcards.
route_for_key() {
    case "$1" in
        NEOPRINT_MONGO_URI | NEOPRINT_REDIS | NEOPRINT_RABBITMQ_URI | NEOPRINT_JWT_SIGNING_KEY)
            echo "backend/core" ;;
        NEOPRINT_KEK_CURRENT_ID | NEOPRINT_KEK__* | NEOPRINT_STORAGE_SECRETS_KEY | \
            NEOPRINT_DATA_PROTECTION_KEY | NEOPRINT_QR_HMAC_KEY)
            echo "backend/crypto" ;;
        NEOPRINT_STORAGE_SEED_* | NEOPRINT_SEED_ADMIN_*)
            echo "backend/seed" ;;
        NEOPRINT_SMTP_* | NEOPRINT_FCM_* | NEOPRINT_TELEGRAM_* | NEOPRINT_FISCAL_* | NEOPRINT_BANK_*)
            echo "backend/providers" ;;
        *)
            echo "" ;;
    esac
}

# Keys that must never be empty and are generated locally when the seed file
# leaves them blank, so that every developer machine has its own values.
needs_generated_value() {
    case "$1" in
        NEOPRINT_JWT_SIGNING_KEY | NEOPRINT_KEK__* | NEOPRINT_STORAGE_SECRETS_KEY | \
            NEOPRINT_DATA_PROTECTION_KEY | NEOPRINT_QR_HMAC_KEY)
            return 0 ;;
        *)
            return 1 ;;
    esac
}

random_base64_256() {
    if [ -r /dev/urandom ]; then
        head -c 32 /dev/urandom | base64 | tr -d '\n\r'
        return 0
    fi
    die "no source of randomness available to generate a key"
}

path_has_value() {
    bao kv get -mount="$KV_MOUNT" "$1" >/dev/null 2>&1
}

seed_path() {
    target="$1"

    if path_has_value "$target"; then
        log "seed: $KV_MOUNT/$target already has a value, leaving it untouched"
        return 0
    fi

    set --
    while IFS= read -r line || [ -n "$line" ]; do
        line=$(printf '%s' "$line" | tr -d '\r')
        case "$line" in '' | '#'*) continue ;; esac
        case "$line" in *=*) : ;; *) continue ;; esac

        key=${line%%=*}
        value=${line#*=}
        key=$(printf '%s' "$key" | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')

        [ "$(route_for_key "$key")" = "$target" ] || continue

        if [ -z "$value" ] && needs_generated_value "$key"; then
            value=$(random_base64_256)
            log "seed: generated a random value for $key"
        fi

        [ -n "$value" ] || continue
        set -- "$@" "$key=$value"
    done <"$SEED_FILE"

    if [ "$#" -eq 0 ]; then
        log "seed: nothing to write to $KV_MOUNT/$target"
        return 0
    fi

    bao kv put -mount="$KV_MOUNT" "$target" "$@" >/dev/null
    log "seed: wrote $# key(s) to $KV_MOUNT/$target"
}

if [ -f "$SEED_FILE" ]; then
    log "seeding development secrets from $(basename "$SEED_FILE")"
    seed_path "backend/core"
    seed_path "backend/crypto"
    seed_path "backend/providers"
    seed_path "backend/seed"
else
    log "WARNING: $SEED_FILE not found."
    log "         Copy .env.openbao-seed.example to .env.openbao-seed and run again."
    log "         The API will not start without the mandatory secrets."
fi

log "done"
