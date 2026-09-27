#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# NeoPrint dev-doctor: finds what stops `docker compose up` on this machine
# before compose does (docs/DEV_ENVIRONMENT.md, "Problem olarsa").
#
#   tools/dev-doctor/dev-doctor.sh          (from anywhere in the repository)
#
# Checks: Docker is running; .env.openbao-seed matches the example (schema
# version, missing and unknown keys); nothing else holds a port compose
# publishes; no container or volume is left from a service compose no longer
# has. Read-only: it changes nothing, it says what to run.
#
# Every docker call costs seconds on Docker Desktop, so each kind of
# information is fetched once and looked up locally.
# ---------------------------------------------------------------------------
set -u

cd "$(dirname "$0")/../.." || exit 2

problems=0
ok()   { printf '  \033[32m✔\033[0m %s\n' "$*"; }
bad()  { printf '  \033[31m✘\033[0m %s\n' "$*"; problems=$((problems + 1)); }
hint() { printf '      → %s\n' "$*"; }

project=neoprint

echo "1. Docker"
# The server version answers only when the daemon does; cheaper than docker info.
if ! server=$(docker version --format '{{.Server.Version}}' 2>/dev/null) || [ -z "$server" ]; then
    bad "Docker işləmir və ya əlçatan deyil."
    hint "Docker Desktop-u açın, sonra yenidən işlədin."
    echo; echo "Nəticə: $problems problem."; exit 1
fi
ok "Docker işləyir ($server)."

echo "2. Seed faylı (.env.openbao-seed)"
keys_of() { tr -d '\r' <"$1" | grep -E '^[[:space:]]*[A-Za-z_][A-Za-z0-9_]*=' | sed -e 's/^[[:space:]]*//' -e 's/=.*//' | sort -u; }
value_of() { tr -d '\r' <"$2" | grep -E "^[[:space:]]*$1=" | tail -n 1 | sed -e 's/^[^=]*=//'; }

if [ ! -f .env.openbao-seed ]; then
    bad ".env.openbao-seed yoxdur."
    hint "cp .env.openbao-seed.example .env.openbao-seed"
else
    expected=$(value_of SEED_SCHEMA_VERSION .env.openbao-seed.example)
    actual=$(value_of SEED_SCHEMA_VERSION .env.openbao-seed)
    missing=$(comm -23 <(keys_of .env.openbao-seed.example) <(keys_of .env.openbao-seed) | tr '\n' ' ')
    unknown=$(comm -13 <(keys_of .env.openbao-seed.example) <(keys_of .env.openbao-seed) | tr '\n' ' ')

    if [ "$actual" = "$expected" ] && [ -z "$missing$unknown" ]; then
        ok "Nümunə ilə uyğundur (versiya $actual)."
    else
        [ "$actual" != "$expected" ] && bad "Versiya ${actual:-(yoxdur)}, nümunədə $expected."
        [ -n "$missing" ] && bad "Çatışmayan açarlar: $missing"
        [ -n "$unknown" ] && bad "Tanınmayan açarlar (köhnə servisdən qala bilər): $unknown"
        hint "docs/DEV_ENVIRONMENT.md → \"Seed faylı\". Öz dəyəriniz yoxdursa: cp .env.openbao-seed.example .env.openbao-seed"
    fi
fi

# What compose declares, and what Docker has - fetched once each, all at the
# same time: a docker call takes seconds on Docker Desktop, in parallel the
# whole check takes about as long as the slowest one.
scratch=$(mktemp -d)
trap 'rm -rf "$scratch"' EXIT
docker compose config >"$scratch/config" 2>/dev/null &
docker compose config --services >"$scratch/services" 2>/dev/null &
docker compose config --volumes >"$scratch/volumes" 2>/dev/null &
docker ps --filter "label=com.docker.compose.project=$project" --format '{{.Names}}' >"$scratch/own" &
docker ps -a --format '{{.Names}}|{{.Label "com.docker.compose.project"}}|{{.Label "com.docker.compose.service"}}' >"$scratch/containers" &
docker volume ls --format '{{.Name}}|{{.Label "com.docker.compose.project"}}|{{.Label "com.docker.compose.volume"}}' >"$scratch/all_volumes" &
docker ps --format '{{.Names}}|{{.Ports}}' >"$scratch/ports" &
wait

services=$(cat "$scratch/services")
volumes=$(cat "$scratch/volumes")
ports=$(grep -E '^[[:space:]]+published:' "$scratch/config" | grep -oE '[0-9]+' | sort -un)
own=$(cat "$scratch/own")
containers=$(cat "$scratch/containers")
all_volumes=$(cat "$scratch/all_volumes")

# "name port" for every published port on the machine, ranges such as
# 0.0.0.0:9000-9001->9000-9001/tcp expanded.
# One awk, not a loop of subshells: forking is slow in Git Bash on Windows.
published=$(awk -F'|' '
    {
        n = split($2, mappings, ",")
        for (i = 1; i <= n; i++) {
            if (index(mappings[i], "->") == 0) continue
            host = mappings[i]
            sub(/->.*/, "", host)
            sub(/.*:/, "", host)
            first = host; last = host
            if (index(host, "-") > 0) { split(host, bounds, "-"); first = bounds[1]; last = bounds[2] }
            for (p = first + 0; p <= last + 0; p++) print $1, p
        }
    }' "$scratch/ports" | sort -u)

echo "3. Portlar"
port_problem=0
for port in $ports; do
    holder=$(printf '%s\n' "$published" | awk -v p="$port" '$2 == p { print $1; exit }')
    if [ -n "$holder" ]; then
        if ! printf '%s\n' "$own" | grep -qx "$holder"; then
            bad "Port $port-u başqa konteyner tutur: $holder"
            hint "docker stop $holder   (və ya: docker rm -f $holder)"
            port_problem=1
        fi
    elif (exec 3<>"/dev/tcp/127.0.0.1/$port") 2>/dev/null; then
        bad "Port $port-u Docker-dən kənar bir proqram tutur."
        hint "Windows: netstat -ano | findstr :$port   ·   Linux/macOS: lsof -i :$port"
        port_problem=1
    fi
done
[ "$port_problem" = 0 ] && ok "Compose-un portları boşdur və ya NeoPrint-in öz konteynerlərindədir ($(echo $ports | tr ' ' ','))."

echo "4. Compose-dan kənarda qalanlar"
leftover=0

# Containers named neoprint-* that compose does not declare (or that were not
# made by compose at all).
while IFS='|' read -r name owner service; do
    case "$name" in "$project"-*) ;; *) continue ;; esac
    if [ "$owner" != "$project" ] || ! printf '%s\n' "$services" | grep -qx "$service"; then
        bad "Konteyner $name compose faylında olmayan servisdəndir (${service:-etiketsiz})."
        hint "docker rm -f $name"
        leftover=1
    fi
done <<< "$containers"

# Volumes of this project compose no longer declares, and neoprint_* volumes
# made outside compose.
while IFS='|' read -r name owner volume; do
    [ -z "$name" ] && continue
    if [ "$owner" = "$project" ]; then
        printf '%s\n' "$volumes" | grep -qx "$volume" && continue
    else
        case "$name" in "$project"_*) ;; *) continue ;; esac
        printf '%s\n' "$volumes" | grep -qx "${name#"${project}_"}" && [ -n "$owner" ] && continue
    fi
    bad "Volume $name compose faylında yoxdur."
    hint "docker volume rm $name   (içindəki data silinir)"
    leftover=1
done <<< "$all_volumes"

[ "$leftover" = 0 ] && ok "Köhnə konteyner və ya volume yoxdur."

echo
if [ "$problems" -eq 0 ]; then
    echo "Nəticə: problem yoxdur — docker compose up --remove-orphans -d"
    exit 0
fi
echo "Nəticə: $problems problem. Yuxarıdakı → əmrləri icra edib yenidən yoxlayın."
exit 1
