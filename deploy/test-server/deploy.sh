#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# NeoPrint — bring the test server to a given build (docs/TEST_SERVER.md).
#
# Run ON THE SERVER, from anywhere, by .github/workflows/deploy-test.yml:
#
#   printf '%s' "$REGISTRY_TOKEN" | \
#     NEOPRINT_HOST=203-0-113-7.sslip.io \
#     NEOPRINT_API_IMAGE=ghcr.io/owner/neoprint-api:<sha> \
#     NEOPRINT_ADMIN_IMAGE=ghcr.io/owner/neoprint-admin:<sha> \
#     REGISTRY_USER=<github user> \
#     bash deploy/test-server/deploy.sh
#
# The registry token arrives on stdin, never as an argument or in the
# environment, and the login is removed again before the script ends.
#
# With no images given it starts what is already there again, which is also
# what unseals OpenBao after the server restarted:
#
#   bash deploy/test-server/deploy.sh
#
# It is idempotent. On first run it writes the server's own credentials -
# .env.openbao-seed and .test-server/credentials - with random values; they
# never leave the server and THIS SCRIPT NEVER PRINTS ONE: its output ends up
# in the log of a public repository's workflow.
# ---------------------------------------------------------------------------
set -euo pipefail

PROJECT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$PROJECT_DIR"

STATE_DIR="$PROJECT_DIR/.test-server"
CADDY_STATE_DIR="$STATE_DIR/caddy"
CREDENTIALS_FILE="$STATE_DIR/credentials"
RELEASE_FILE="$PROJECT_DIR/.env"
SEED_FILE="$PROJECT_DIR/.env.openbao-seed"
SEED_EXAMPLE="$PROJECT_DIR/.env.openbao-seed.example"

# The image the password hash is made with: the one the stack runs.
CADDY_IMAGE="$(sed -n 's/^[[:space:]]*image:[[:space:]]*\(caddy:[^[:space:]]*\).*/\1/p' deploy/test-server/compose.override.yml | head -n 1)"

# The oldest Compose that understands !reset in the override file.
MIN_COMPOSE="2.24.4"

log() { printf '[deploy] %s\n' "$*"; }
die() { printf '[deploy] ERROR: %s\n' "$*" >&2; exit 1; }

# Taken off stdin before anything else can read it. Only when a registry user
# was named: run by hand over ssh, stdin is a pipe that never ends.
registry_token=""
if [ -n "${REGISTRY_USER:-}" ]; then
    IFS= read -r registry_token || true
    [ -n "$registry_token" ] || die "REGISTRY_USER is set but no registry token came on stdin"
fi

# ---------------------------------------------------------------------------
# 1. Docker.
# ---------------------------------------------------------------------------
if docker info >/dev/null 2>&1; then
    DOCKER=(docker)
elif sudo -n docker info >/dev/null 2>&1; then
    DOCKER=(sudo -n docker)
else
    die "Docker is not installed, not running, or this user may not use it. \
Run deploy/test-server/server-setup.sh on the server once (docs/TEST_SERVER.md, step 1)."
fi

compose() {
    "${DOCKER[@]}" compose \
        --project-directory "$PROJECT_DIR" \
        -f docker-compose.yml \
        -f deploy/test-server/compose.override.yml \
        "$@"
}

compose_version="$("${DOCKER[@]}" compose version --short 2>/dev/null | sed 's/^v//')" \
    || die "the Docker Compose plugin is missing (docs/TEST_SERVER.md, step 1)."

# sort -V puts the older of the two first.
if [ "$(printf '%s\n%s\n' "$MIN_COMPOSE" "$compose_version" | sort -V | head -n 1)" != "$MIN_COMPOSE" ]; then
    die "Docker Compose $compose_version is too old; $MIN_COMPOSE or later is needed. \
Run deploy/test-server/server-setup.sh again (docs/TEST_SERVER.md, step 1)."
fi

[ -n "$CADDY_IMAGE" ] || die "could not read the Caddy image from compose.override.yml"

# ---------------------------------------------------------------------------
# 2. What to run. Given by the workflow, otherwise what ran last time.
# ---------------------------------------------------------------------------
previous() {
    [ -f "$RELEASE_FILE" ] || return 0
    sed -n "s/^$1=//p" "$RELEASE_FILE" | tail -n 1
}

NEOPRINT_HOST="${NEOPRINT_HOST:-$(previous NEOPRINT_HOST)}"
NEOPRINT_API_IMAGE="${NEOPRINT_API_IMAGE:-$(previous NEOPRINT_API_IMAGE)}"
NEOPRINT_ADMIN_IMAGE="${NEOPRINT_ADMIN_IMAGE:-$(previous NEOPRINT_ADMIN_IMAGE)}"
# "-" is how the workflow says "no contact address"; unset keeps the last one.
NEOPRINT_ACME_EMAIL="${NEOPRINT_ACME_EMAIL-$(previous NEOPRINT_ACME_EMAIL)}"
[ "$NEOPRINT_ACME_EMAIL" = "-" ] && NEOPRINT_ACME_EMAIL=""

[ -n "$NEOPRINT_HOST" ] || die "NEOPRINT_HOST is not set and there is no previous deployment to take it from"
[ -n "$NEOPRINT_API_IMAGE" ] || die "NEOPRINT_API_IMAGE is not set and there is no previous deployment to take it from"
[ -n "$NEOPRINT_ADMIN_IMAGE" ] || die "NEOPRINT_ADMIN_IMAGE is not set and there is no previous deployment to take it from"

# These go into files Docker and Caddy read; nothing but the expected
# characters is let through.
safe='^[A-Za-z0-9._:/@+-]+$'
[[ "$NEOPRINT_HOST" =~ ^[A-Za-z0-9.-]+$ ]] || die "NEOPRINT_HOST is not a host name"
[[ "$NEOPRINT_API_IMAGE" =~ $safe ]] || die "NEOPRINT_API_IMAGE is not an image reference"
[[ "$NEOPRINT_ADMIN_IMAGE" =~ $safe ]] || die "NEOPRINT_ADMIN_IMAGE is not an image reference"
[ -z "$NEOPRINT_ACME_EMAIL" ] || [[ "$NEOPRINT_ACME_EMAIL" =~ ^[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+$ ]] \
    || die "NEOPRINT_ACME_EMAIL is not an email address"

# ---------------------------------------------------------------------------
# 3. The server's own secrets. Written once, never replaced, never printed.
# ---------------------------------------------------------------------------
umask 077
mkdir -p "$STATE_DIR" "$CADDY_STATE_DIR"
# Caddy runs as root in its container; the files below hold a hash and a
# contact address, not a password.
chmod 755 "$STATE_DIR" "$CADDY_STATE_DIR"

random_hex() { head -c "$1" /dev/urandom | od -An -tx1 | tr -d ' \n'; }

# Upper case, lower case, a digit and a symbol, whatever the random part is.
random_password() { printf 'Np!%sZq7' "$(random_hex 12)"; }

if [ ! -f "$SEED_FILE" ]; then
    [ -f "$SEED_EXAMPLE" ] || die "$SEED_EXAMPLE is missing"

    # A new seed file next to old data would lock the stack out of its own
    # database: the old volumes were initialised with the old passwords.
    if "${DOCKER[@]}" volume inspect neoprint_mongo-data >/dev/null 2>&1; then
        die ".env.openbao-seed is gone but the data volumes are still there. Restore the file \
from your copy, or start over (docs/TEST_SERVER.md, \"Sıfırdan başlamaq\")."
    fi

    log "writing .env.openbao-seed with this server's own random credentials"
    tmp="$(mktemp "$PROJECT_DIR/.env.openbao-seed.XXXXXX")"

    # Every development password in the example is replaced wherever it
    # appears, so a connection string follows the password it carries.
    sed \
        -e "s/devMongoPassword/$(random_hex 24)/g" \
        -e "s/devRedisPassword/$(random_hex 24)/g" \
        -e "s/devRabbitPassword/$(random_hex 24)/g" \
        -e "s/devRustfsPassword/$(random_hex 24)/g" \
        -e "s/mock-callback-shared-secret/$(random_hex 32)/g" \
        -e "s/ChangeMe!2026/$(random_password)/g" \
        -e "s/DevTest!2026/$(random_password)/g" \
        "$SEED_EXAMPLE" >"$tmp"

    # A development password this script does not know about - one added to
    # the example later - must not reach a server on the internet unnoticed.
    # (One grep, not a pipe of two: under pipefail a grep -q that stops early
    # fails the pipe, and the check would pass for the wrong reason.)
    if grep -q -E '^[^#]*(dev[A-Za-z]*Password|ChangeMe|DevTest|shared-secret)' "$tmp"; then
        rm -f "$tmp"
        die "the seed example has a development credential deploy.sh does not replace; update deploy.sh"
    fi

    mv "$tmp" "$SEED_FILE"
    chmod 600 "$SEED_FILE"
else
    log ".env.openbao-seed is already there; left as it is"
fi

if [ ! -f "$CREDENTIALS_FILE" ]; then
    log "writing the test tools' user and password to .test-server/credentials"
    printf 'TEST_TOOLS_USER=tester\nTEST_TOOLS_PASSWORD=%s\n' "$(random_hex 16)" >"$CREDENTIALS_FILE"
    chmod 600 "$CREDENTIALS_FILE"
    rm -f "$CADDY_STATE_DIR/tools-auth.caddy"
fi

if [ ! -f "$CADDY_STATE_DIR/tools-auth.caddy" ]; then
    tools_user="$(sed -n 's/^TEST_TOOLS_USER=//p' "$CREDENTIALS_FILE")"
    # On stdin, so the password is in no process list.
    tools_hash="$(sed -n 's/^TEST_TOOLS_PASSWORD=//p' "$CREDENTIALS_FILE" \
        | "${DOCKER[@]}" run --rm -i "$CADDY_IMAGE" caddy hash-password 2>/dev/null | tail -n 1)"
    [[ "$tools_hash" == \$2* ]] || die "could not hash the test tools' password"

    printf 'basic_auth {\n\t%s %s\n}\n' "$tools_user" "$tools_hash" >"$CADDY_STATE_DIR/tools-auth.caddy"
    chmod 644 "$CADDY_STATE_DIR/tools-auth.caddy"
fi

if [ -n "$NEOPRINT_ACME_EMAIL" ]; then
    printf 'email %s\n' "$NEOPRINT_ACME_EMAIL" >"$CADDY_STATE_DIR/acme.caddy"
else
    : >"$CADDY_STATE_DIR/acme.caddy"
fi
chmod 644 "$CADDY_STATE_DIR/acme.caddy"

# Made here, by this user: left to Docker it would belong to root and the
# workflow could not put the APK into it.
mkdir -p "$PROJECT_DIR/downloads"
chmod 755 "$PROJECT_DIR/downloads"

# Read by Compose for the override file's variables. No secret in it.
cat >"$RELEASE_FILE" <<EOF
# Written by deploy/test-server/deploy.sh on every deployment.
NEOPRINT_HOST=$NEOPRINT_HOST
NEOPRINT_API_IMAGE=$NEOPRINT_API_IMAGE
NEOPRINT_ADMIN_IMAGE=$NEOPRINT_ADMIN_IMAGE
NEOPRINT_ACME_EMAIL=$NEOPRINT_ACME_EMAIL
EOF
chmod 644 "$RELEASE_FILE"

# ---------------------------------------------------------------------------
# 4. Pull. The login lasts exactly as long as the pull.
# ---------------------------------------------------------------------------
registry="${NEOPRINT_API_IMAGE%%/*}"
logged_in=false

cleanup() {
    if [ "$logged_in" = true ]; then
        "${DOCKER[@]}" logout "$registry" >/dev/null 2>&1 || true
    fi
}
trap cleanup EXIT

if [ -n "$registry_token" ]; then
    log "signing in to $registry"
    printf '%s' "$registry_token" \
        | "${DOCKER[@]}" login "$registry" --username "$REGISTRY_USER" --password-stdin >/dev/null 2>&1 \
        || die "could not sign in to $registry"
    logged_in=true
    registry_token=""

    log "pulling $NEOPRINT_API_IMAGE"
    "${DOCKER[@]}" pull --quiet "$NEOPRINT_API_IMAGE" >/dev/null || die "could not pull the API image"
    log "pulling $NEOPRINT_ADMIN_IMAGE"
    "${DOCKER[@]}" pull --quiet "$NEOPRINT_ADMIN_IMAGE" >/dev/null || die "could not pull the panel image"
else
    log "no registry sign-in given; starting the images that are already on the server"
fi

# ---------------------------------------------------------------------------
# 5. Up. openbao-init runs on every "up": it unseals and adds what is missing.
# ---------------------------------------------------------------------------
log "starting the stack"
if ! compose up --detach --remove-orphans --quiet-pull; then
    # The init containers name the key or the step that failed, never a value.
    for init in neoprint-openbao-init neoprint-mongo-init neoprint-rustfs-init; do
        printf '\n[deploy] last lines of %s:\n' "$init" >&2
        "${DOCKER[@]}" logs --tail 25 "$init" >&2 2>&1 || true
    done
    die "docker compose up failed"
fi

# Caddy reads its files only when it starts, and "up" leaves a container
# alone when nothing in its definition changed.
if ! compose exec -T caddy caddy reload --config /etc/caddy/site/Caddyfile --adapter caddyfile >/dev/null 2>&1; then
    log "caddy did not reload its configuration; restarting it"
    compose restart caddy >/dev/null
fi

# ---------------------------------------------------------------------------
# 6. Wait until it answers.
# ---------------------------------------------------------------------------
health() { "${DOCKER[@]}" inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' "$1" 2>/dev/null || echo missing; }

log "waiting for the API, the panel and Caddy"
deadline=$((SECONDS + 420))
while :; do
    api="$(health neoprint-api)"
    admin="$(health neoprint-admin)"
    caddy="$(health neoprint-caddy)"

    if [ "$api" = healthy ] && [ "$admin" = healthy ] && [ "$caddy" = healthy ]; then
        break
    fi

    if [ "$SECONDS" -ge "$deadline" ]; then
        printf '\n' >&2
        compose ps --all >&2 || true
        # The API's own log is not printed: this output is public, and a log
        # may name a tester. Read it on the server: docker logs neoprint-api
        die "not healthy in time (api: $api, panel: $admin, caddy: $caddy). On the server: docker logs neoprint-api"
    fi

    sleep 5
done

# The images of earlier deployments, once nothing runs from them.
"${DOCKER[@]}" image prune --force >/dev/null 2>&1 || true

log "done"
log "panel:      https://$NEOPRINT_HOST/"
log "API:        https://$NEOPRINT_HOST/api/"
log "mail:       https://mail.$NEOPRINT_HOST/   (user and password: .test-server/credentials on the server)"
log "passwords:  docs/TEST_SERVER.md says where each one is on the server"
