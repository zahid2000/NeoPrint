#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# NeoPrint — prepare an Ubuntu server for the test deployment, once
# (docs/TEST_SERVER.md, step 1).
#
#   sudo bash server-setup.sh [deploy-user] [directory]
#
# deploy-user  the account the deploy workflow signs in as; the one who ran
#              sudo when left out.
# directory    where the stack lives; /opt/neoprint when left out.
#
# It installs Docker Engine and the Compose plugin from Docker's own apt
# repository (the distribution's packages lag behind the Compose the override
# file needs), lets the deploy user use Docker, creates the directory and, when
# ufw is on, opens 22, 80 and 443. Running it again changes nothing that is
# already right.
# ---------------------------------------------------------------------------
set -euo pipefail

log() { printf '[setup] %s\n' "$*"; }
die() { printf '[setup] ERROR: %s\n' "$*" >&2; exit 1; }

[ "$(id -u)" -eq 0 ] || die "run this with sudo"

DEPLOY_USER="${1:-${SUDO_USER:-root}}"
TARGET_DIR="${2:-/opt/neoprint}"

id "$DEPLOY_USER" >/dev/null 2>&1 || die "there is no user '$DEPLOY_USER' on this server"

. /etc/os-release
[ "${ID:-}" = "ubuntu" ] || die "this script is written for Ubuntu; this is ${PRETTY_NAME:-something else}"

export DEBIAN_FRONTEND=noninteractive

# ---------------------------------------------------------------------------
# Docker Engine + Compose plugin.
# ---------------------------------------------------------------------------
if docker compose version >/dev/null 2>&1 && [ -f /etc/apt/sources.list.d/docker.list ]; then
    log "Docker is already installed from Docker's repository; bringing it up to date"
else
    log "installing Docker Engine and the Compose plugin"
fi

apt-get update -qq
apt-get install -y -qq ca-certificates curl >/dev/null

install -m 0755 -d /etc/apt/keyrings
curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
chmod a+r /etc/apt/keyrings/docker.asc

printf 'deb [arch=%s signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/ubuntu %s stable\n' \
    "$(dpkg --print-architecture)" "${UBUNTU_CODENAME:-$VERSION_CODENAME}" >/etc/apt/sources.list.d/docker.list

apt-get update -qq
apt-get install -y -qq docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin >/dev/null

systemctl enable --now docker >/dev/null

log "$(docker --version)"
log "$(docker compose version)"

# ---------------------------------------------------------------------------
# The deploy user and the directory.
# ---------------------------------------------------------------------------
if [ "$DEPLOY_USER" != "root" ]; then
    # Membership of this group is root on the machine; it is given to the one
    # account the workflow signs in as and to no other.
    usermod -aG docker "$DEPLOY_USER"
    log "'$DEPLOY_USER' may use Docker (takes effect at that user's next sign-in)"
fi

install -d -m 0750 -o "$DEPLOY_USER" -g "$(id -gn "$DEPLOY_USER")" "$TARGET_DIR"
log "$TARGET_DIR belongs to '$DEPLOY_USER'"

# ---------------------------------------------------------------------------
# Firewall. Docker publishes its ports past ufw, so this only matters for ssh
# and for a server whose ufw would otherwise drop 80 and 443 before Docker.
# ---------------------------------------------------------------------------
if command -v ufw >/dev/null 2>&1 && ufw status | grep -q '^Status: active'; then
    ufw allow OpenSSH >/dev/null || ufw allow 22/tcp >/dev/null
    ufw allow 80/tcp >/dev/null
    ufw allow 443/tcp >/dev/null
    ufw allow 443/udp >/dev/null
    log "ufw: 22, 80 and 443 are open"
else
    log "ufw is not active; nothing changed there. If the hosting provider has its own firewall, open 80 and 443 in it"
fi

log "done. Next: docs/TEST_SERVER.md, step 2"
