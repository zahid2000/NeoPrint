#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# NeoPrint staging (M20, T-335, docs/DEPLOY.md section 3a): the whole stack in
# Production, as a second compose project beside development on this machine -
# its own OpenBao, its own data, a clean database with the production seed only.
#
#   bash tools/staging/staging.sh up       # seed file (first time), build, start, wait
#   bash tools/staging/staging.sh check    # the database is clean: the seed and nothing else
#   bash tools/staging/staging.sh t169     # a mandatory secret gone: the API refuses to start; put back
#   bash tools/staging/staging.sh down     # stop; the data stays
#   bash tools/staging/staging.sh reset --yes   # stop and delete its data, seed file and OpenBao folder
#
# From the repository root, in Git Bash, WSL or Linux. Prints no secret: the
# random credentials are in .env.staging-seed and .openbao-staging/ (both
# gitignored, owner-only), the first SuperAdmin's in the seed file.
# ---------------------------------------------------------------------------
set -euo pipefail

root="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$root"

env_file="deploy/staging/staging.env"
seed=".env.staging-seed"
bao_dir=".openbao-staging"
project="neoprint-staging"
api="neoprint-staging-api"
openbao="neoprint-staging-openbao"
mongo="neoprint-staging-mongo"
api_url="http://localhost:25080"
admin_url="http://localhost:28080"

compose() { docker compose -p "$project" --env-file "$env_file" "$@"; }
log() { printf '[staging] %s\n' "$*"; }
die() { printf '[staging] %s\n' "$*" >&2; exit 1; }
failures=0
check() { if [ "$1" = ok ]; then echo "ok     $2"; else echo "WRONG  $2"; failures=$((failures + 1)); fi; }

random_hex() { head -c "$1" /dev/urandom | od -An -tx1 | tr -d ' \n'; }
# Upper case, lower case, a digit and a symbol, whatever the random part is.
random_password() { printf 'Np!%sZq7' "$(random_hex 12)"; }

# A value from the seed file, never printed.
seed_value() { grep -m1 "^$1=" "$seed" | cut -d= -f2-; }

make_seed() {
    [ -f "$seed" ] && { log "$seed is already there; left as it is"; return; }
    if docker volume inspect "${project}_mongo-data" >/dev/null 2>&1; then
        die "$seed is gone but the staging data is still there: restore the file, or start over (reset --yes)"
    fi
    log "writing $seed with staging's own random credentials"
    local tmp
    tmp="$(mktemp "$root/.env.staging-seed.XXXXXX")"
    # The same replacements as the test server's (deploy/test-server/deploy.sh).
    sed \
        -e "s/devMongoPassword/$(random_hex 24)/g" \
        -e "s/devRedisPassword/$(random_hex 24)/g" \
        -e "s/devRabbitPassword/$(random_hex 24)/g" \
        -e "s/devRustfsPassword/$(random_hex 24)/g" \
        -e "s/mock-callback-shared-secret/$(random_hex 32)/g" \
        -e "s/ChangeMe!2026/$(random_password)/g" \
        -e "s/DevTest!2026/$(random_password)/g" \
        .env.openbao-seed.example >"$tmp"
    if grep -q -E '^[^#]*(dev[A-Za-z]*Password|ChangeMe|DevTest|shared-secret)' "$tmp"; then
        rm -f "$tmp"
        die "the seed example has a development credential this script does not replace; update it"
    fi
    mv "$tmp" "$seed"
    chmod 600 "$seed"
}

wait_healthy() { # container seconds
    local state=""
    for _ in $(seq 1 "$2"); do
        state="$(docker inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' "$1" 2>/dev/null || true)"
        [ "$state" = healthy ] && return 0
        sleep 2
    done
    return 1
}

mongo_eval() { # javascript - run against the staging database as its root user
    MSYS_NO_PATHCONV=1 docker exec -e "U=$(seed_value MONGO_INITDB_ROOT_USERNAME)" -e "P=$(seed_value MONGO_INITDB_ROOT_PASSWORD)" "$mongo" \
        sh -c 'mongosh --quiet -u "$U" -p "$P" --authenticationDatabase admin "mongodb://127.0.0.1:27017/neoprint?directConnection=true" --eval "$0"' "$1"
}

bao() { # args - the bao CLI in staging's OpenBao, with its root token (never printed)
    local token
    token="$(sed -n 's/.*"root_token": *"\([^"]*\)".*/\1/p' "$bao_dir/init.json")"
    [ -n "$token" ] || die "no root token in $bao_dir/init.json"
    MSYS_NO_PATHCONV=1 docker exec -i -e BAO_ADDR=http://127.0.0.1:8200 -e "BAO_TOKEN=$token" "$openbao" bao "$@"
}

case "${1:-}" in
up)
    [ -f .env.openbao-seed.example ] || die "run it from the repository"
    make_seed
    mkdir -p "$bao_dir"
    log "building and starting (the first time takes a while)"
    compose up -d --build
    wait_healthy "$api" 120 || die "the API did not become healthy: docker logs $api"
    wait_healthy neoprint-staging-admin 60 || die "the panel did not become healthy: docker logs neoprint-staging-admin"
    log "up: panel $admin_url, API $api_url (Production)"
    log "first sign-in: the SuperAdmin in $seed (NEOPRINT_SEED_ADMIN_EMAIL / _PASSWORD); the password must be changed"
    ;;

check)
    log "the database is clean: the production seed and nothing else (T-300)"
    counts="$(mongo_eval 'const c = n => db.getCollection(n).countDocuments(); print(JSON.stringify({
        admins: c("admin_users"), roles: c("admin_roles"), settings: c("admin_settings"), legal: c("legal_documents"),
        pricing: c("pricing_profiles"), users: c("users"), kiosks: c("kiosks"), devices: c("kiosk_devices"),
        tokens: c("enrollment_tokens"), jobs: c("print_jobs"), payments: c("payments"), wallets: c("wallets"),
        files: c("files"), incidents: c("incidents"),
        adminEmails: db.admin_users.find({}, {Email: 1}).toArray().map(a => a.Email) }))')"
    n() { node -e "const c=JSON.parse(process.argv[1]);console.log(c[process.argv[2]])" "$counts" "$1"; }
    seed_admin="$(seed_value NEOPRINT_SEED_ADMIN_EMAIL)"
    check "$([ "$(n admins)" = 1 ] && echo ok)" "one admin account: the first SuperAdmin ($(n admins))"
    check "$(node -e "const c=JSON.parse(process.argv[1]);process.stdout.write(c.adminEmails.length===1&&c.adminEmails[0]===process.argv[2]?'ok':'')" "$counts" "$seed_admin")" "and it is the seed's, no dev-* account"
    check "$([ "$(n roles)" -gt 0 ] && echo ok)" "the role templates are seeded ($(n roles))"
    check "$([ "$(n settings)" -gt 0 ] && echo ok)" "the settings are seeded ($(n settings))"
    check "$([ "$(n legal)" -gt 0 ] && echo ok)" "the legal documents are seeded ($(n legal))"
    check "$([ "$(n pricing)" -ge 1 ] && echo ok)" "the default pricing profile is seeded ($(n pricing))"
    for empty in users kiosks devices tokens jobs payments wallets files incidents; do
        check "$([ "$(n $empty)" = 0 ] && echo ok)" "no $empty from development ($(n $empty))"
    done
    check "$(is_prod="$(docker inspect -f '{{range .Config.Env}}{{println .}}{{end}}' "$api" | grep -c '^ASPNETCORE_ENVIRONMENT=Production$')"; [ "$is_prod" = 1 ] && echo ok)" "the API runs in Production"
    dev="$(curl -s -o /dev/null -w '%{http_code}' -X POST "$api_url/api/dev/payments/00000000-0000-0000-0000-000000000000/simulate-success")"
    check "$([ "$dev" = 404 ] && echo ok)" "the Development-only endpoints do not exist ($dev)"
    echo
    [ "$failures" -eq 0 ] && echo "Every check passed." || { echo "$failures check(s) failed."; exit 1; }
    ;;

t169)
    # T-169: "in production the API does not start when a mandatory key is
    # missing" - on staging's own OpenBao, never the shared development one.
    key="NEOPRINT_JWT_SIGNING_KEY"
    path="neoprint/backend/core"
    log "T-169: $key taken out of $path; the API must refuse to start, name the key and never print a value"
    wait_healthy "$api" 60 || die "start from a healthy staging: bash tools/staging/staging.sh up"

    current="$(bao kv get -format=json "$path")"
    version="$(node -e "console.log(JSON.parse(process.argv[1]).data.metadata.version)" "$current")"
    value_sha="$(node -e "const v=JSON.parse(process.argv[1]).data.data[process.argv[2]]||'';console.log(require('crypto').createHash('sha256').update(v).digest('hex'))" "$current" "$key")"
    # The secret without the key, through a pipe: nothing is written to disk.
    node -e "const d=JSON.parse(process.argv[1]).data.data;delete d[process.argv[2]];process.stdout.write(JSON.stringify(d))" "$current" "$key" \
        | bao kv put "$path" - >/dev/null
    restore() { bao kv rollback -version="$version" "$path" >/dev/null 2>&1 || true; }
    trap restore EXIT

    check "$(bao kv get -format=json "$path" | node -e "let s='';process.stdin.on('data',d=>s+=d).on('end',()=>console.log(process.argv[1] in JSON.parse(s).data.data?'':'ok'))" "$key")" "the key is gone from OpenBao (version $version kept for the rollback)"

    since="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    docker restart "$api" >/dev/null
    sleep 25
    logs="$(docker logs --since "$since" "$api" 2>&1 || true)"
    check "$(wait_healthy "$api" 5 || echo ok)" "the API does not become healthy"
    check "$(grep -q "mandatory secrets are missing from OpenBao: .*$key" <<<"$logs" && echo ok)" "it says which key is missing"
    check "$(grep -q 'MissingSecretsException\|did not start\|Unhandled exception' <<<"$logs" && echo ok)" "and stops, rather than running without it"
    leaked="$(node -e "const crypto=require('crypto');const lines=process.argv[1].split(/[\s\"'=:,]+/);console.log(lines.some(w=>w.length>16&&crypto.createHash('sha256').update(w).digest('hex')===process.argv[2])?'leaked':'')" "$logs" "$value_sha")"
    check "$([ -z "$leaked" ] && echo ok)" "no secret value in its log"

    log "putting $key back (kv rollback to version $version)"
    trap - EXIT
    bao kv rollback -version="$version" "$path" >/dev/null
    check "$(bao kv get -format=json "$path" | node -e "let s='';process.stdin.on('data',d=>s+=d).on('end',()=>console.log(process.argv[1] in JSON.parse(s).data.data?'ok':''))" "$key")" "the key is back"
    docker restart "$api" >/dev/null
    check "$(wait_healthy "$api" 90 && echo ok)" "and the API starts again, healthy"
    echo
    [ "$failures" -eq 0 ] && echo "Every check passed." || { echo "$failures check(s) failed."; exit 1; }
    ;;

down)
    compose down
    ;;

reset)
    [ "${2:-}" = --yes ] || die "reset deletes staging's data, seed file and OpenBao folder: add --yes"
    compose down -v
    rm -rf "$bao_dir" "$seed"
    log "staging deleted; 'up' starts it afresh"
    ;;

*)
    sed -n '2,16p' "$0" | sed 's/^# \{0,1\}//'
    exit 64
    ;;
esac
