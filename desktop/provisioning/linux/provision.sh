#!/bin/bash
# ---------------------------------------------------------------------------
# NeoPrint kiosk provisioning — Ubuntu 24.04 LTS Server (plan section 7.12)
#
# Turns a minimal server install into a kiosk: installs the dependencies,
# creates the accounts, sets up cage as the display session, locks down the
# console and installs the NeoPrint agent and UI.
#
# IDEMPOTENT: running it again only corrects what has drifted. That is what
# makes the remote ReapplyProvisioning command safe.
#
#   sudo ./provision.sh [--api-url URL] [--enrollment-token TOKEN]
#                       [--channel pilot|stable] [--offline-package PATH]
#
# Nothing here identifies a kiosk. --enrollment-token is the one optional
# per-kiosk input, it is single use, and the kiosk's own parameters all come
# from the backend after activation (plan section 7.11).
#
# Every change this script makes is listed in docs/KIOSK_PROVISIONING.md.
# ---------------------------------------------------------------------------
set -euo pipefail

PROVISIONING_VERSION="0.1.0"

API_URL=""
ENROLLMENT_TOKEN=""
CHANNEL="stable"
OFFLINE_PACKAGE=""

INSTALL_ROOT="/opt/neoprint"
DATA_ROOT="/var/lib/neoprint"
LOG_ROOT="/var/log/neoprint"
LOG_FILE="$LOG_ROOT/provision.log"

SERVICE_USER="neoprint"
KIOSK_USER="kiosk"

# ---------------------------------------------------------------------------
# Arguments
# ---------------------------------------------------------------------------
while [ $# -gt 0 ]; do
    case "$1" in
        --api-url)           API_URL="${2:?--api-url needs a value}"; shift 2 ;;
        --enrollment-token)  ENROLLMENT_TOKEN="${2:?--enrollment-token needs a value}"; shift 2 ;;
        --channel)           CHANNEL="${2:?--channel needs a value}"; shift 2 ;;
        --offline-package)   OFFLINE_PACKAGE="${2:?--offline-package needs a value}"; shift 2 ;;
        -h|--help)
            sed -n '2,20p' "$0"
            exit 0 ;;
        *)
            echo "Unknown argument: $1" >&2
            exit 2 ;;
    esac
done

if [ "$(id -u)" -ne 0 ]; then
    echo "This script must run as root." >&2
    exit 1
fi

mkdir -p "$LOG_ROOT"
exec > >(tee -a "$LOG_FILE") 2>&1

log() { printf '[provision %s] %s\n' "$(date -u +%FT%TZ)" "$*"; }

log "NeoPrint kiosk provisioning $PROVISIONING_VERSION starting (channel: $CHANNEL)"

# ---------------------------------------------------------------------------
# 1. Dependencies
#
# No desktop environment and no notification daemon: nothing must be able to
# put a window on the screen except the kiosk itself.
# ---------------------------------------------------------------------------
log "installing packages"

export DEBIAN_FRONTEND=noninteractive

apt-get update
apt-get install -y --no-install-recommends \
    cage \
    cups cups-ipp-utils \
    udisks2 \
    libreoffice-core libreoffice-writer libreoffice-calc libreoffice-impress \
    ghostscript \
    fonts-dejavu fonts-liberation fonts-crosextra-carlito fonts-crosextra-caladea \
    fontconfig \
    chrony \
    ca-certificates curl

fc-cache -f >/dev/null

# ---------------------------------------------------------------------------
# 2. Accounts
#
# Two accounts on purpose: the agent needs enough privilege to restart things,
# the UI must have as little as possible.
# ---------------------------------------------------------------------------
log "creating accounts"

if ! id -u "$SERVICE_USER" >/dev/null 2>&1; then
    useradd --system --create-home --shell /usr/sbin/nologin "$SERVICE_USER"
fi

if ! id -u "$KIOSK_USER" >/dev/null 2>&1; then
    useradd --create-home --shell /bin/false "$KIOSK_USER"
fi

# The agent manages print queues, so it joins the printer admin group.
usermod -aG lpadmin "$SERVICE_USER" || true
# The UI needs the video and input devices cage hands it.
usermod -aG video,input,render "$KIOSK_USER" || true

# ---------------------------------------------------------------------------
# 3. Directories
# ---------------------------------------------------------------------------
log "creating directories"

install -d -o "$SERVICE_USER" -g "$SERVICE_USER" -m 700 "$DATA_ROOT" "$DATA_ROOT/device" "$DATA_ROOT/spool" "$DATA_ROOT/state"
install -d -o "$SERVICE_USER" -g "$SERVICE_USER" -m 750 "$LOG_ROOT"
install -d -m 755 "$INSTALL_ROOT" "$INSTALL_ROOT/versions"

# ---------------------------------------------------------------------------
# 4. Enrollment token
#
# Optional, single use, and deliberately not a secret that identifies the
# kiosk on its own: it names which kiosk record the backend should bind this
# device to (plan section 7.11).
# ---------------------------------------------------------------------------
if [ -n "$ENROLLMENT_TOKEN" ]; then
    log "writing the enrollment token for zero-touch activation"
    printf '%s' "$ENROLLMENT_TOKEN" >"$DATA_ROOT/device/enrollment-token"
    chown "$SERVICE_USER:$SERVICE_USER" "$DATA_ROOT/device/enrollment-token"
    chmod 600 "$DATA_ROOT/device/enrollment-token"
fi

# ---------------------------------------------------------------------------
# 5. Console lockdown
#
# No virtual terminals to switch to, no reboot from the keyboard, no SysRq and
# no kernel messages on the screen the customer is looking at.
# ---------------------------------------------------------------------------
log "locking down the console"

install -d -m 755 /etc/systemd/logind.conf.d
cat >/etc/systemd/logind.conf.d/neoprint-kiosk.conf <<'CONF'
# NeoPrint kiosk (plan section 7.12).
[Login]
# No spare virtual terminals: nothing to Ctrl+Alt+F2 into.
NAutoVTs=0
ReserveVT=0
# The power button on an unattended machine is a customer pressing it.
HandlePowerKey=ignore
HandleSuspendKey=ignore
HandleHibernateKey=ignore
HandleLidSwitch=ignore
CONF

# Ctrl+Alt+Del must not reboot a kiosk.
systemctl mask ctrl-alt-del.target >/dev/null 2>&1 || true

cat >/etc/sysctl.d/99-neoprint-kiosk.conf <<'CONF'
# NeoPrint kiosk: the SysRq combinations can kill processes and reboot.
kernel.sysrq = 0
CONF
sysctl --system >/dev/null

# Quiet boot, no blanking: the screen shows NeoPrint, never a console.
if [ -f /etc/default/grub ]; then
    sed -i 's/^GRUB_TIMEOUT=.*/GRUB_TIMEOUT=0/' /etc/default/grub
    sed -i 's/^GRUB_CMDLINE_LINUX_DEFAULT=.*/GRUB_CMDLINE_LINUX_DEFAULT="quiet splash consoleblank=0"/' /etc/default/grub
    update-grub >/dev/null 2>&1 || true
fi

# ---------------------------------------------------------------------------
# 6. Automatic updates
#
# Security updates yes, automatic reboots no: the agent reboots inside the
# update window when the queue is empty (plan section 7.12).
# ---------------------------------------------------------------------------
log "configuring unattended upgrades"

apt-get install -y --no-install-recommends unattended-upgrades >/dev/null

cat >/etc/apt/apt.conf.d/52neoprint-kiosk <<'CONF'
// NeoPrint kiosk (plan section 7.12): security updates only, and never an
// automatic reboot - the agent chooses the moment.
Unattended-Upgrade::Automatic-Reboot "false";
Unattended-Upgrade::Remove-Unused-Dependencies "true";
CONF

# Crash reporter dialogs have no place on a kiosk screen.
if [ -f /etc/default/apport ]; then
    sed -i 's/^enabled=.*/enabled=0/' /etc/default/apport
fi
systemctl disable --now apport.service >/dev/null 2>&1 || true

# ---------------------------------------------------------------------------
# 7. Removable media
#
# udisks2 mounts a stick for the kiosk account only, and noexec: the kiosk
# reads documents off it, it never runs anything from it.
# ---------------------------------------------------------------------------
log "installing the udisks2 polkit rule"

install -d -m 755 /etc/polkit-1/rules.d
install -m 644 "$(dirname "$0")/polkit/50-neoprint-udisks.rules" \
    /etc/polkit-1/rules.d/50-neoprint-udisks.rules 2>/dev/null || true

install -d -m 755 /etc/udisks2
cat >/etc/udisks2/mount_options.conf <<'CONF'
# NeoPrint kiosk: never execute anything off a customer's USB stick.
[defaults]
defaults=noexec,nosuid,nodev
CONF

# ---------------------------------------------------------------------------
# 8. Narrow sudo rule
#
# Exactly the commands the agent needs, and nothing else.
# ---------------------------------------------------------------------------
log "installing the sudoers rule"

cat >/etc/sudoers.d/neoprint <<CONF
# NeoPrint agent (plan section 7.12): only these commands, no password.
$SERVICE_USER ALL=(root) NOPASSWD: /usr/bin/systemctl reboot
$SERVICE_USER ALL=(root) NOPASSWD: /usr/bin/systemctl restart neoprint-kiosk
$SERVICE_USER ALL=(root) NOPASSWD: /usr/bin/systemctl restart neoprint-agent
$SERVICE_USER ALL=(root) NOPASSWD: /usr/sbin/lpadmin
$SERVICE_USER ALL=(root) NOPASSWD: /usr/sbin/cupsenable
CONF
chmod 440 /etc/sudoers.d/neoprint
visudo -c -f /etc/sudoers.d/neoprint >/dev/null

# ---------------------------------------------------------------------------
# 9. Firewall
#
# Nothing reaches a kiosk from outside; it only talks out to the backend.
# ---------------------------------------------------------------------------
log "configuring the firewall"

apt-get install -y --no-install-recommends ufw >/dev/null
ufw --force reset >/dev/null
ufw default deny incoming >/dev/null
ufw default allow outgoing >/dev/null
ufw --force enable >/dev/null

# ---------------------------------------------------------------------------
# 10. Time
# ---------------------------------------------------------------------------
log "setting the time zone to Asia/Baku"
timedatectl set-timezone Asia/Baku
systemctl enable --now chrony >/dev/null 2>&1 || true

# ---------------------------------------------------------------------------
# 11. NeoPrint services
# ---------------------------------------------------------------------------
log "installing the systemd units"

script_dir="$(cd "$(dirname "$0")" && pwd)"

for unit in neoprint-agent.service neoprint-kiosk.service; do
    if [ -f "$script_dir/systemd/$unit" ]; then
        install -m 644 "$script_dir/systemd/$unit" "/etc/systemd/system/$unit"
    fi
done

# The API address identifies the environment, not the kiosk, so it is written
# as a drop-in rather than baked into the package.
if [ -n "$API_URL" ]; then
    install -d -m 755 /etc/systemd/system/neoprint-kiosk.service.d
    cat >/etc/systemd/system/neoprint-kiosk.service.d/10-api-url.conf <<CONF
[Service]
Environment=NEOPRINT_KIOSK_Kiosk__ApiBaseUrl=$API_URL
CONF
fi

systemctl daemon-reload

if [ -n "$OFFLINE_PACKAGE" ]; then
    log "NOTE: installing the NeoPrint package from $OFFLINE_PACKAGE is implemented with the updater in step 3"
else
    log "NOTE: downloading the NeoPrint package and verifying its signature is implemented with the updater in step 3"
fi

systemctl enable neoprint-agent.service >/dev/null 2>&1 || true
systemctl enable neoprint-kiosk.service >/dev/null 2>&1 || true

printf '%s' "$PROVISIONING_VERSION" >"$DATA_ROOT/state/provisioning-version"
chown "$SERVICE_USER:$SERVICE_USER" "$DATA_ROOT/state/provisioning-version"

log "provisioning finished; a reboot is required to enter kiosk mode"
log "log written to $LOG_FILE"
