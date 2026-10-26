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
#                       [--channel pilot|stable] [--offline-package PATH | --download]
#   --download: the newest published UI and agent from the backend
#               (GET api/releases/latest), each checked by the agent on the
#               setup stick (--verifier, default ../verifier/NeoPrint.Kiosk.Agent)
#               with the release keys built into it before anything is installed
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
#
# A step that fails says which step it was and what to do (docs/KIOSK_SETUP.md
# has the same steps in Azerbaijani); the system files it had changed in this
# run are put back as they were, and since every step can run again, the cure
# is to remove the cause and start the script once more (T-327).
# ---------------------------------------------------------------------------
set -Eeuo pipefail

PROVISIONING_VERSION="0.5.0"

API_URL=""
ENROLLMENT_TOKEN=""
CHANNEL="stable"
OFFLINE_PACKAGE=""
DOWNLOAD=0
VERIFIER=""
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
        --download)          DOWNLOAD=1; shift ;;
        --verifier)          VERIFIER="${2:?--verifier needs a value}"; shift 2 ;;
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
VERIFIER="${VERIFIER:-$script_dir/../verifier/NeoPrint.Kiosk.Agent}"

if [ "$DOWNLOAD" -eq 1 ] && [ -n "$OFFLINE_PACKAGE" ]; then
    echo "--download and --offline-package are two ways to get NeoPrint: choose one" >&2
    exit 2
fi
if [ "$DOWNLOAD" -eq 1 ] && [ -z "$API_URL" ]; then
    echo "--download needs --api-url: the backend it downloads from" >&2
    exit 2
fi

# --dry-run (the container kiosk's ReapplyProvisioning, T-321): the scripts are
# all here and readable, and nothing on the machine is changed.
if [ "$DRY_RUN" -eq 1 ]; then
    for file in systemd/neoprint-agent.service systemd/neoprint-kiosk.service systemd/neoprint-firstboot.service neoprint-agent-guard neoprint-reprovision neoprint-convert neoprint-firstboot neoprint-enroll prepare-image.sh; do
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

# ---------------------------------------------------------------------------
# Steps, and what to do when one fails
# ---------------------------------------------------------------------------
STEPS=13
STEP=0
STEP_NAME="starting"
BACKUP_DIR="$(mktemp -d /var/tmp/neoprint-provision.XXXXXX)"
KEPT=()
UFW_WAS_ACTIVE=0
if command -v ufw >/dev/null 2>&1 && ufw status 2>/dev/null | grep -q "Status: active"; then
    UFW_WAS_ACTIVE=1
fi

step() { # number name
    STEP="$1"
    STEP_NAME="$2"
    log "step $STEP/$STEPS: $STEP_NAME"
}

# A system file this run is about to change: kept, to be put back if a step fails.
keep() {
    local file="$1"
    local copy
    copy="$BACKUP_DIR/$(printf '%s' "$file" | tr '/' '_')"
    [ -e "$copy" ] || [ -e "$copy.absent" ] && return 0
    if [ -e "$file" ]; then
        cp -a "$file" "$copy"
    else
        : >"$copy.absent"
    fi
    KEPT+=("$file")
}

# Whatever to do, said for each step (the same as docs/KIOSK_SETUP.md, "Quraşdırma xətaları").
hint() {
    case "$1" in
        1)  echo "the packages could not be installed: check the network cable or Wi-Fi and the date and time, then run 'sudo apt-get update' to see what the package server says." ;;
        2)  echo "an account could not be made: the line above names it; another user with that name is the usual cause." ;;
        3)  echo "a folder could not be made: the disk may be full or read-only - see 'df -h'." ;;
        4)  echo "the enrollment token could not be written: the disk may be full; the kiosk can also be given it later with 'sudo neoprint-enroll'." ;;
        5|6) echo "a system file could not be written: the line above names it." ;;
        7|8) echo "a file is missing from the setup stick: copy the whole provisioning folder onto the stick again." ;;
        9)  echo "the sudo rules did not pass their check and were not installed: send the lines above to the NeoPrint team." ;;
        10) echo "the firewall could not be set up: the rules from before were put back; see 'sudo ufw status verbose'." ;;
        11) echo "the time zone could not be set: the kiosk works; set it later with 'sudo timedatectl set-timezone Asia/Baku'." ;;
        12) echo "NeoPrint could not be installed: the package is incomplete, could not be downloaded, or did not pass its signature check - check the network, or prepare the stick again (docs/KIOSK_SETUP.md)." ;;
        13) echo "the services could not be set up: see 'systemctl status neoprint-agent neoprint-kiosk'." ;;
        *)  echo "see the lines above." ;;
    esac
}

on_error() {
    local code=$?
    trap - ERR
    log "ERROR: step $STEP/$STEPS ($STEP_NAME) failed (exit code $code)"
    log "what to do: $(hint "$STEP")"

    # Back as they were: the system files this run had changed.
    local file copy
    for file in "${KEPT[@]}"; do
        copy="$BACKUP_DIR/$(printf '%s' "$file" | tr '/' '_')"
        if [ -e "$copy.absent" ]; then
            rm -f "$file"
        elif [ -e "$copy" ]; then
            cp -a "$copy" "$file"
        fi
    done
    if [ "${#KEPT[@]}" -gt 0 ]; then
        log "put back as they were: ${KEPT[*]}"
        systemctl daemon-reload >/dev/null 2>&1 || true
    fi
    if [ "$STEP" -eq 10 ] && command -v ufw >/dev/null 2>&1; then
        if [ "$UFW_WAS_ACTIVE" -eq 1 ]; then ufw --force enable >/dev/null 2>&1 || true; else ufw --force disable >/dev/null 2>&1 || true; fi
    fi
    log "nothing else needs undoing: every step can run again - remove the cause and run this script once more"
    log "log written to $LOG_FILE"
    exit "$code"
}
trap on_error ERR
trap 'rm -rf "$BACKUP_DIR"' EXIT

log "NeoPrint kiosk provisioning $PROVISIONING_VERSION starting (channel: $CHANNEL, repair: $REPAIR)"

# ---------------------------------------------------------------------------
# 1. Dependencies
#
# No desktop environment and no notification daemon: nothing must be able to
# put a window on the screen except the kiosk itself.
# ---------------------------------------------------------------------------
step 1 "packages"

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
step 2 "accounts"

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
step 3 "directories"

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
step 4 "enrollment token"
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
step 5 "console lockdown"

keep /etc/systemd/logind.conf.d/neoprint-kiosk.conf
keep /etc/sysctl.d/99-neoprint-kiosk.conf
keep /etc/default/grub
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
step 6 "automatic updates"
keep /etc/apt/apt.conf.d/52neoprint-kiosk
keep /etc/apt/apt.conf.d/20auto-upgrades

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
step 7 "removable media"
keep /etc/polkit-1/rules.d/50-neoprint-udisks.rules
keep /etc/udisks2/mount_options.conf

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
step 8 "converter sandbox"

install -o root -g root -m 755 "$script_dir/neoprint-convert" "$LIB_ROOT/neoprint-convert"

# ---------------------------------------------------------------------------
# 9. Narrow sudo rules
#
# Exactly the commands each account needs, and nothing else.
# ---------------------------------------------------------------------------
step 9 "sudo rules"

# Written beside, checked, and only then put in place: a broken sudoers file
# would lock every account out of sudo.
sudoers_new="$BACKUP_DIR/sudoers.neoprint"
cat >"$sudoers_new" <<CONF
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
visudo -c -f "$sudoers_new" >/dev/null
keep /etc/sudoers.d/neoprint
install -o root -g root -m 440 "$sudoers_new" /etc/sudoers.d/neoprint

# ---------------------------------------------------------------------------
# 10. Firewall
#
# Nothing reaches a kiosk from outside. Out: DNS, time, the backend and
# updates (HTTP/HTTPS), and the printers on the local network (IPP, raw,
# LPD, SNMP, mDNS). The converter account: nothing at all, loopback included.
# ---------------------------------------------------------------------------
step 10 "firewall"
keep /etc/ufw/before.rules
keep /etc/ufw/before6.rules

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
step 11 "time"
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
step 12 "NeoPrint"

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

# The newest published release of a component, from the backend - which is
# not trusted for it: the agent on the stick checks the signature with the
# keys built into it, and nothing that fails is installed (T-327).
download_release() { # component; sets DOWNLOADED_DIR and DOWNLOADED_VERSION
    local component="$1" work meta version path base verifier checked
    work="$BACKUP_DIR/download-$component"
    mkdir -p "$work/files"

    log "asking $API_URL for the newest $component for linux-x64 ($CHANNEL)"
    meta="$(curl -fsS --max-time 30 "$API_URL/api/releases/latest/$component/linux-x64?channel=$CHANNEL")" || {
        log "the backend has no published $component for linux-x64, or cannot be reached"
        return 1
    }
    version="$(printf '%s' "$meta" | sed -n 's/.*"version":"\([^"]*\)".*/\1/p')"
    path="$(printf '%s' "$meta" | sed -n 's/.*"packagePath":"\([^"]*\)".*/\1/p')"
    base="${path%/package}"
    if [ -z "$version" ] || [ -z "$path" ]; then
        log "the backend's answer about $component was not understood"
        return 1
    fi

    curl -fsS --max-time 30 -o "$work/manifest.json" "$API_URL/$base/manifest"
    curl -fsS --max-time 30 -o "$work/manifest.json.sig" "$API_URL/$base/signature"
    curl -fS --max-time 1800 --retry 3 -o "$work/package.tar.gz" "$API_URL/$base/package"

    # A stick may carry no execute bits (FAT): the checker runs from a copy.
    if [ ! -f "$VERIFIER" ]; then
        log "the checker is not on the stick: $VERIFIER"
        return 1
    fi
    verifier="$BACKUP_DIR/verifier"
    if [ ! -d "$verifier" ]; then
        cp -a "$(dirname "$VERIFIER")" "$verifier"
        chmod +x "$verifier/$(basename "$VERIFIER")"
    fi
    if ! checked="$("$verifier/$(basename "$VERIFIER")" verify-package "$work/package.tar.gz" "$work/manifest.json" "$work/manifest.json.sig" "$component")"; then
        log "$component $version did NOT pass the signature check; nothing of it is installed"
        return 1
    fi
    if [ "$checked" != "$version" ]; then
        log "the signed version ($checked) is not the one the backend named ($version); nothing of it is installed"
        return 1
    fi
    log "$component $version: signature checked with the keys on the stick"

    tar -xzf "$work/package.tar.gz" -C "$work/files"
    DOWNLOADED_DIR="$work/files"
    DOWNLOADED_VERSION="$version"
}

if [ "$REPAIR" -eq 0 ] && [ "$DOWNLOAD" -eq 1 ]; then
    download_release app
    app_dir="$DOWNLOADED_DIR"
    app_version="$DOWNLOADED_VERSION"
    download_release agent
    install_version app "$app_version" "$app_dir"
    install_version agent "$DOWNLOADED_VERSION" "$DOWNLOADED_DIR"
    chmod 755 "$INSTALL_ROOT/app/current/NeoPrint.Kiosk" "$INSTALL_ROOT/agent/current/NeoPrint.Kiosk.Agent"
elif [ "$REPAIR" -eq 0 ] && [ -n "$OFFLINE_PACKAGE" ]; then
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

step 13 "services"

# A golden image's clone makes its own identity on its first boot, and is
# given its enrollment token afterwards (T-327).
install -o root -g root -m 755 "$script_dir/neoprint-firstboot" "$LIB_ROOT/neoprint-firstboot"
install -o root -g root -m 755 "$script_dir/neoprint-enroll" "$LIB_ROOT/neoprint-enroll"
ln -sfn "$LIB_ROOT/neoprint-enroll" /usr/local/sbin/neoprint-enroll

for unit in neoprint-agent.service neoprint-kiosk.service neoprint-maintenance.service neoprint-firstboot.service; do
    keep "/etc/systemd/system/$unit"
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
systemctl enable neoprint-firstboot.service >/dev/null 2>&1 || true
systemctl enable neoprint-agent.service >/dev/null 2>&1 || true
systemctl enable neoprint-kiosk.service >/dev/null 2>&1 || true

printf '%s' "$PROVISIONING_VERSION" >"$DATA_ROOT/state/provisioning-version"
chown "$KIOSK_USER:$SERVICE_USER" "$DATA_ROOT/state/provisioning-version"
chmod 640 "$DATA_ROOT/state/provisioning-version"

trap - ERR
log "provisioning finished: $STEPS of $STEPS steps; a reboot is required to enter kiosk mode"
log "log written to $LOG_FILE"
