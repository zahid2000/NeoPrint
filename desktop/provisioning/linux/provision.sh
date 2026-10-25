#!/bin/bash
# ---------------------------------------------------------------------------
# NeoPrint kiosk provisioning — Ubuntu 24.04 LTS Server (plan section 7.12)
#
# Turns a minimal server install into a kiosk: installs the dependencies,
# creates the accounts, sets up cage as the display session, locks down the
# console, the network and the converters, and installs the NeoPrint agent
# and UI.
#
# IDEMPOTENT: running it again only corrects what has drifted. That is what
# makes the remote ReapplyProvisioning command safe.
#
#   sudo ./provision.sh [--api-url URL] [--enrollment-token TOKEN]
#                       [--channel pilot|stable] [--offline-package PATH]
#   sudo ./provision.sh --repair      # the agent's ReapplyProvisioning / a new
#                                     # provisioning version: everything but
#                                     # installing NeoPrint itself
#   ./provision.sh --dry-run          # what would be done; changes nothing
#
# Nothing here identifies a kiosk. --enrollment-token is the one optional
# per-kiosk input, it is single use, and the kiosk's own parameters all come
# from the backend after activation (plan section 7.11).
#
# Every change this script makes is listed in docs/KIOSK_PROVISIONING.md.
# ---------------------------------------------------------------------------
set -euo pipefail

PROVISIONING_VERSION="0.4.1"

API_URL=""
ENROLLMENT_TOKEN=""
CHANNEL="stable"
OFFLINE_PACKAGE=""
REPAIR=0
DRY_RUN=0

INSTALL_ROOT="/opt/neoprint"
DATA_ROOT="/var/lib/neoprint"
LOG_ROOT="/var/log/neoprint"
LOG_FILE="$LOG_ROOT/provision.log"
LIB_ROOT="/usr/local/lib/neoprint"

SERVICE_USER="neoprint"
KIOSK_USER="kiosk"
CONVERT_USER="neoprint-convert"

# Where network printers live: the kiosk prints and reads page counters on
# the local network only.
LOCAL_NETWORKS="10.0.0.0/8 172.16.0.0/12 192.168.0.0/16"

# ---------------------------------------------------------------------------
# Arguments
# ---------------------------------------------------------------------------
while [ $# -gt 0 ]; do
    case "$1" in
        --api-url)           API_URL="${2:?--api-url needs a value}"; shift 2 ;;
        --enrollment-token)  ENROLLMENT_TOKEN="${2:?--enrollment-token needs a value}"; shift 2 ;;
        --channel)           CHANNEL="${2:?--channel needs a value}"; shift 2 ;;
        --offline-package)   OFFLINE_PACKAGE="${2:?--offline-package needs a value}"; shift 2 ;;
        --repair)            REPAIR=1; shift ;;
        --dry-run)           DRY_RUN=1; shift ;;
        -h|--help)
            sed -n '2,20p' "$0"
            exit 0 ;;
        *)
            echo "Unknown argument: $1" >&2
            exit 2 ;;
    esac
done

case "$CHANNEL" in
    pilot|stable) ;;
    *) echo "--channel must be pilot or stable" >&2; exit 2 ;;
esac

script_dir="$(cd "$(dirname "$0")" && pwd)"

# --dry-run (the container kiosk's ReapplyProvisioning, T-321): the scripts are
# all here and readable, and nothing on the machine is changed.
if [ "$DRY_RUN" -eq 1 ]; then
    for file in systemd/neoprint-agent.service systemd/neoprint-kiosk.service neoprint-agent-guard neoprint-reprovision neoprint-convert; do
        [ -r "$script_dir/$file" ] || { echo "[provision dry-run] missing: $file" >&2; exit 1; }
    done
    echo "[provision dry-run] NeoPrint kiosk provisioning $PROVISIONING_VERSION (repair: $REPAIR): every script is in place; nothing was changed"
    exit 0
fi

if [ "$(id -u)" -ne 0 ]; then
    echo "This script must run as root." >&2
    exit 1
fi

mkdir -p "$LOG_ROOT"
exec > >(tee -a "$LOG_FILE") 2>&1

log() { printf '[provision %s] %s\n' "$(date -u +%FT%TZ)" "$*"; }

log "NeoPrint kiosk provisioning $PROVISIONING_VERSION starting (channel: $CHANNEL, repair: $REPAIR)"

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
    cups cups-ipp-utils printer-driver-cups-pdf \
    udisks2 \
    libreoffice-core libreoffice-writer libreoffice-calc libreoffice-impress \
    ghostscript \
    fonts-dejavu fonts-liberation fonts-crosextra-carlito fonts-crosextra-caladea \
    fontconfig \
    chrony \
    sudo \
    unattended-upgrades \
    ufw \
    ca-certificates curl

fc-cache -f >/dev/null

# CUPS keeps job history by default; the kiosk reads it after a restart to
# learn whether a job printed (plan section 7.12). Printers are not shared.
systemctl enable --now cups >/dev/null 2>&1 || true
cupsctl --no-share-printers >/dev/null 2>&1 || true

# ---------------------------------------------------------------------------
# 2. Accounts
#
# Three accounts on purpose: the agent needs enough privilege to restart
# things, the UI as little as possible, and the converters none at all - not
# even the network (docs/TASKS.md T-075).
# ---------------------------------------------------------------------------
log "creating accounts"

if ! id -u "$SERVICE_USER" >/dev/null 2>&1; then
    useradd --system --create-home --shell /usr/sbin/nologin "$SERVICE_USER"
fi

if ! id -u "$KIOSK_USER" >/dev/null 2>&1; then
    useradd --create-home --shell /bin/false "$KIOSK_USER"
fi

if ! id -u "$CONVERT_USER" >/dev/null 2>&1; then
    useradd --system --no-create-home --home-dir /nonexistent --shell /usr/sbin/nologin "$CONVERT_USER"
fi

# The agent manages print queues, so it joins the printer admin group.
usermod -aG lpadmin "$SERVICE_USER" || true
# The UI needs the video and input devices cage hands it.
usermod -aG video,input,render "$KIOSK_USER" || true
# ...and reads what the converter account leaves in the work folder.
usermod -aG "$CONVERT_USER" "$KIOSK_USER" || true

# ---------------------------------------------------------------------------
# 3. Directories
#
# The UI (kiosk) owns its data: the device key, the encrypted spool, the job
# journal. The agent (group neoprint) may read the state. The converter
# account may only pass through the spool into the work folder, where the
# UI puts one document at a time (docs/TASKS.md T-078, T-075).
# ---------------------------------------------------------------------------
log "creating directories"

# 751: the converter account must pass through to spool/work; it can list
# nothing, and device (700) and state (750) stay closed to it.
install -d -o "$KIOSK_USER" -g "$SERVICE_USER" -m 751 "$DATA_ROOT"
install -d -o "$KIOSK_USER" -g "$KIOSK_USER" -m 700 "$DATA_ROOT/device"
install -d -o "$KIOSK_USER" -g "$CONVERT_USER" -m 710 "$DATA_ROOT/spool"
install -d -o "$KIOSK_USER" -g "$CONVERT_USER" -m 2770 "$DATA_ROOT/spool/work"
install -d -o "$KIOSK_USER" -g "$SERVICE_USER" -m 750 "$DATA_ROOT/state"
install -d -o "$KIOSK_USER" -g "$SERVICE_USER" -m 770 "$LOG_ROOT"
install -d -m 755 "$INSTALL_ROOT" "$LIB_ROOT"

# Updates (docs/KIOSK_UPDATES.md section 2, T-321): each component has its
# versions and a "current" link. The agent moves the UI's and its own; the
# provisioning scripts run as root and stay root's - only the root-owned
# helper below installs them.
for component in app agent; do
    install -d -o "$SERVICE_USER" -g "$SERVICE_USER" -m 755 "$INSTALL_ROOT/$component" "$INSTALL_ROOT/$component/versions"
done
install -d -o root -g root -m 755 "$INSTALL_ROOT/provisioning" "$INSTALL_ROOT/provisioning/versions"
# What the UI writes for the agent (downloads, requests, health), and what the
# agent writes for the UI (state, results); the agent's own copy stays its own.
install -d -o "$KIOSK_USER" -g "$SERVICE_USER" -m 2770 "$DATA_ROOT/updates"
install -d -o "$SERVICE_USER" -g "$KIOSK_USER" -m 750 "$DATA_ROOT/agent"
install -d -o "$SERVICE_USER" -g "$SERVICE_USER" -m 700 "$DATA_ROOT/agent/incoming"

# A second run corrects what drifted underneath, too.
chown -R "$KIOSK_USER:$KIOSK_USER" "$DATA_ROOT/device"
chmod -R go-rwx "$DATA_ROOT/device"

# ---------------------------------------------------------------------------
# 4. Enrollment token
#
# Optional, single use, and read by the UI, which does the enrollment
# (plan section 7.11).
# ---------------------------------------------------------------------------
if [ -n "$ENROLLMENT_TOKEN" ] && [ "$REPAIR" -eq 0 ]; then
    log "writing the enrollment token for zero-touch activation"
    printf '%s' "$ENROLLMENT_TOKEN" >"$DATA_ROOT/device/enrollment-token"
    chown "$KIOSK_USER:$KIOSK_USER" "$DATA_ROOT/device/enrollment-token"
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
install -m 644 "$script_dir/logind.conf.d/neoprint-kiosk.conf" /etc/systemd/logind.conf.d/neoprint-kiosk.conf

# Ctrl+Alt+Del must not reboot a kiosk.
systemctl mask ctrl-alt-del.target >/dev/null 2>&1 || true

cat >/etc/sysctl.d/99-neoprint-kiosk.conf <<'CONF'
# NeoPrint kiosk: the SysRq combinations can kill processes and reboot.
kernel.sysrq = 0
# Kernel messages stay off the kiosk's screen.
kernel.printk = 3 3 3 3
CONF
sysctl --system >/dev/null || true

# Quiet boot, no blanking: the screen shows NeoPrint, never a console.
if [ -f /etc/default/grub ]; then
    sed -i 's/^GRUB_TIMEOUT=.*/GRUB_TIMEOUT=0/' /etc/default/grub
    sed -i 's/^GRUB_CMDLINE_LINUX_DEFAULT=.*/GRUB_CMDLINE_LINUX_DEFAULT="quiet splash loglevel=3 consoleblank=0 vt.global_cursor_default=0"/' /etc/default/grub
    update-grub >/dev/null 2>&1 || true
fi

# ---------------------------------------------------------------------------
# 6. Automatic updates
#
# Security updates yes, automatic reboots no: the agent reboots inside the
# update window when the queue is empty (plan section 7.12).
# ---------------------------------------------------------------------------
log "configuring unattended upgrades"

cat >/etc/apt/apt.conf.d/52neoprint-kiosk <<'CONF'
// NeoPrint kiosk (plan section 7.12): security updates only, and never an
// automatic reboot - the agent chooses the moment.
Unattended-Upgrade::Allowed-Origins { "${distro_id}:${distro_codename}-security"; };
Unattended-Upgrade::Automatic-Reboot "false";
Unattended-Upgrade::Remove-Unused-Dependencies "true";
CONF

cat >/etc/apt/apt.conf.d/20auto-upgrades <<'CONF'
APT::Periodic::Update-Package-Lists "1";
APT::Periodic::Unattended-Upgrade "1";
CONF

# Crash reporter dialogs have no place on a kiosk screen.
if [ -f /etc/default/apport ]; then
    sed -i 's/^enabled=.*/enabled=0/' /etc/default/apport
fi
systemctl disable --now apport.service >/dev/null 2>&1 || true

# ---------------------------------------------------------------------------
# 7. Removable media
#
# The kiosk mounts a stick itself through udisks2 (there is no automounter
# without a desktop); only the kiosk account may, and never with exec rights:
# the kiosk reads documents off it, it never runs anything from it.
# ---------------------------------------------------------------------------
log "installing the udisks2 polkit rule"

install -d -m 755 /etc/polkit-1/rules.d
install -m 644 "$script_dir/polkit/50-neoprint-udisks.rules" /etc/polkit-1/rules.d/50-neoprint-udisks.rules

install -d -m 755 /etc/udisks2
cat >/etc/udisks2/mount_options.conf <<'CONF'
# NeoPrint kiosk: never execute anything off a customer's USB stick.
[defaults]
defaults=noexec,nosuid,nodev
CONF

# ---------------------------------------------------------------------------
# 8. Converter sandbox (docs/TASKS.md T-075)
#
# A root-owned wrapper runs one converter as neoprint-convert, under its own
# timeout and resource limits. The firewall (section 10) rejects every packet
# of that account.
# ---------------------------------------------------------------------------
log "installing the converter sandbox"

install -o root -g root -m 755 "$script_dir/neoprint-convert" "$LIB_ROOT/neoprint-convert"

# ---------------------------------------------------------------------------
# 9. Narrow sudo rules
#
# Exactly the commands each account needs, and nothing else.
# ---------------------------------------------------------------------------
log "installing the sudoers rules"

cat >/etc/sudoers.d/neoprint <<CONF
# NeoPrint (plan section 7.12): only these commands, no password.

# The agent.
$SERVICE_USER ALL=(root) NOPASSWD: /usr/bin/systemctl reboot
$SERVICE_USER ALL=(root) NOPASSWD: /usr/bin/systemctl restart neoprint-kiosk
$SERVICE_USER ALL=(root) NOPASSWD: /usr/bin/systemctl restart neoprint-agent
# Updates (T-321): the UI is stopped and started around a version switch; the
# provisioning scripts only through the root-owned helper, which checks a
# package's signature itself before anything of it runs as root.
$SERVICE_USER ALL=(root) NOPASSWD: /usr/bin/systemctl stop neoprint-kiosk.service
$SERVICE_USER ALL=(root) NOPASSWD: /usr/bin/systemctl start neoprint-kiosk.service
$SERVICE_USER ALL=(root) NOPASSWD: $LIB_ROOT/neoprint-reprovision
$SERVICE_USER ALL=(root) NOPASSWD: /usr/sbin/lpadmin
$SERVICE_USER ALL=(root) NOPASSWD: /usr/sbin/cupsenable

# The UI: service access from the admin menu, a reboot, and the converter sandbox.
$KIOSK_USER ALL=(root) NOPASSWD: /usr/bin/systemctl start neoprint-maintenance.service
$KIOSK_USER ALL=(root) NOPASSWD: /usr/bin/systemctl reboot
$KIOSK_USER ALL=($CONVERT_USER) NOPASSWD: $LIB_ROOT/neoprint-convert
CONF
chmod 440 /etc/sudoers.d/neoprint
visudo -c -f /etc/sudoers.d/neoprint >/dev/null

# ---------------------------------------------------------------------------
# 10. Firewall
#
# Nothing reaches a kiosk from outside. Out: DNS, time, the backend and
# updates (HTTP/HTTPS), and the printers on the local network (IPP, raw,
# LPD, SNMP, mDNS). The converter account: nothing at all, loopback included.
# ---------------------------------------------------------------------------
log "configuring the firewall"

ufw --force reset >/dev/null
ufw default deny incoming >/dev/null
ufw default deny outgoing >/dev/null

ufw allow out 53 >/dev/null
ufw allow out 123/udp >/dev/null
ufw allow out 80/tcp >/dev/null
ufw allow out 443/tcp >/dev/null

for network in $LOCAL_NETWORKS; do
    ufw allow out to "$network" port 631 proto tcp >/dev/null
    ufw allow out to "$network" port 9100 proto tcp >/dev/null
    ufw allow out to "$network" port 515 proto tcp >/dev/null
    ufw allow out to "$network" port 161 proto udp >/dev/null
done

ufw allow out to 224.0.0.251 port 5353 proto udp >/dev/null

# ufw has no owner match of its own: the rule goes into its before-rules,
# ahead of the loopback exception, so the converters cannot reach even
# localhost. ufw reset above restored the defaults, so this adds it once.
for rules in /etc/ufw/before.rules /etc/ufw/before6.rules; do
    if [ -f "$rules" ] && ! grep -q "neoprint-convert" "$rules"; then
        awk -v user="$CONVERT_USER" '
            !done && /^-A ufw[6]?-before-output/ {
                chain = ($0 ~ /^-A ufw6-/) ? "ufw6-before-output" : "ufw-before-output"
                print "# NeoPrint: the converter account has no network (docs/TASKS.md T-075)"
                print "-A " chain " -m owner --uid-owner " user " -j REJECT"
                done = 1
            }
            { print }
        ' "$rules" >"$rules.neoprint" && mv "$rules.neoprint" "$rules"
    fi
done

ufw --force enable >/dev/null

# ---------------------------------------------------------------------------
# 11. Time
# ---------------------------------------------------------------------------
log "setting the time zone to Asia/Baku"
timedatectl set-timezone Asia/Baku || ln -sf /usr/share/zoneinfo/Asia/Baku /etc/localtime
systemctl enable --now chrony >/dev/null 2>&1 || true

# ---------------------------------------------------------------------------
# 12. NeoPrint
#
# Three components, each in versions/<version> with "current" pointing at one
# (docs/KIOSK_UPDATES.md section 2): the UI (app), the agent and these scripts.
# The data directories are outside them, so an update never touches identity
# or spool (plan section 7.12). After this, new versions come from the backend
# through the agent; --offline-package is the first installation:
#
#   --offline-package DIR   DIR/app and DIR/agent are the published UI and agent,
#                           DIR/VERSION their version.
# ---------------------------------------------------------------------------
switch_current() { # component version
    ln -sfn "versions/$2" "$INSTALL_ROOT/$1/current.next"
    mv -Tf "$INSTALL_ROOT/$1/current.next" "$INSTALL_ROOT/$1/current"
}

install_version() { # component version source-dir
    local target="$INSTALL_ROOT/$1/versions/$2"
    rm -rf "$target.partial"
    install -d -m 755 "$target.partial"
    cp -a "$3/." "$target.partial/"
    printf '%s' "$2" >"$target.partial/.neoprint-complete"
    rm -rf "$target"
    mv "$target.partial" "$target"
    if [ "$1" = provisioning ]; then
        chown -R root:root "$target"
    else
        chown -R "$SERVICE_USER:$SERVICE_USER" "$target"
    fi
    chmod -R go-w "$target"
    switch_current "$1" "$2"
    log "NeoPrint $1 $2 is current"
}

if [ "$REPAIR" -eq 0 ] && [ -n "$OFFLINE_PACKAGE" ]; then
    log "installing NeoPrint from $OFFLINE_PACKAGE"

    if [ ! -f "$OFFLINE_PACKAGE/app/NeoPrint.Kiosk" ] || [ ! -f "$OFFLINE_PACKAGE/agent/NeoPrint.Kiosk.Agent" ]; then
        log "ERROR: the package has no app/NeoPrint.Kiosk or agent/NeoPrint.Kiosk.Agent; see docs/KIOSK_SETUP.md"
        exit 1
    fi

    version="$(cat "$OFFLINE_PACKAGE/VERSION" 2>/dev/null || date -u +%Y%m%d%H%M%S)"
    install_version app "$version" "$OFFLINE_PACKAGE/app"
    install_version agent "$version" "$OFFLINE_PACKAGE/agent"
    chmod 755 "$INSTALL_ROOT/app/current/NeoPrint.Kiosk" "$INSTALL_ROOT/agent/current/NeoPrint.Kiosk.Agent"
elif [ ! -e "$INSTALL_ROOT/app/current" ]; then
    log "NOTE: no --offline-package and nothing installed yet; install a first version with --offline-package"
fi

# These scripts themselves: what --repair and a new provisioning version run.
if [ "$script_dir" != "$(readlink -f "$INSTALL_ROOT/provisioning/current" 2>/dev/null || true)" ]; then
    install_version provisioning "$PROVISIONING_VERSION" "$script_dir"
fi

# The guard outside the agent package: three failed starts of a new agent and
# the previous one is put back (T-321).
install -o root -g root -m 755 "$script_dir/neoprint-agent-guard" "$LIB_ROOT/neoprint-agent-guard"

# The provisioning helper and the root-owned copy of the agent it runs, which
# checks a provisioning package with its own keys (T-321). Refreshed only when
# a technician runs this script - never by a repair the agent asked for, so
# the agent cannot put its own binary in root's hands.
install -o root -g root -m 755 "$script_dir/neoprint-reprovision" "$LIB_ROOT/neoprint-reprovision"
if [ "$REPAIR" -eq 0 ] && [ -x "$INSTALL_ROOT/agent/current/NeoPrint.Kiosk.Agent" ]; then
    rm -rf "$LIB_ROOT/agent.new"
    cp -a "$(readlink -f "$INSTALL_ROOT/agent/current")/." "$LIB_ROOT/agent.new/"
    chown -R root:root "$LIB_ROOT/agent.new"
    chmod -R go-w "$LIB_ROOT/agent.new"
    rm -rf "$LIB_ROOT/agent"
    mv "$LIB_ROOT/agent.new" "$LIB_ROOT/agent"
fi

log "installing the systemd units"

for unit in neoprint-agent.service neoprint-kiosk.service neoprint-maintenance.service; do
    install -m 644 "$script_dir/systemd/$unit" "/etc/systemd/system/$unit"
done

# The environment's settings: not the kiosk's identity, so a drop-in rather
# than part of the package.
install -d -m 755 /etc/systemd/system/neoprint-kiosk.service.d
{
    echo "[Service]"
    echo "Environment=NEOPRINT_KIOSK_Kiosk__ConverterUser=$CONVERT_USER"
    if [ -n "$API_URL" ]; then
        echo "Environment=NEOPRINT_KIOSK_Kiosk__ApiBaseUrl=$API_URL"
    fi
} >/etc/systemd/system/neoprint-kiosk.service.d/10-environment.conf

# The earlier name of that drop-in.
rm -f /etc/systemd/system/neoprint-kiosk.service.d/10-api-url.conf

# tty1 belongs to the kiosk; the console login is for service access only.
systemctl disable getty@tty1.service >/dev/null 2>&1 || true

systemctl daemon-reload
systemctl enable neoprint-agent.service >/dev/null 2>&1 || true
systemctl enable neoprint-kiosk.service >/dev/null 2>&1 || true

printf '%s' "$PROVISIONING_VERSION" >"$DATA_ROOT/state/provisioning-version"
chown "$KIOSK_USER:$SERVICE_USER" "$DATA_ROOT/state/provisioning-version"
chmod 640 "$DATA_ROOT/state/provisioning-version"

log "provisioning finished; a reboot is required to enter kiosk mode"
log "log written to $LOG_FILE"
