#!/bin/bash
# ---------------------------------------------------------------------------
# Prepare a provisioned machine to be cloned (plan section 7.12, T-327).
#
# Removes everything that identifies this particular machine, so the image can
# be copied onto many kiosks and each one registers as its own device on first
# boot. Getting this wrong is how two kiosks end up sharing an identity, so the
# script verifies afterwards that nothing was left behind.
#
#   sudo ./prepare-image.sh           # clean, then verify
#   sudo ./prepare-image.sh --check   # verify only: says what is left, changes nothing
#
# Left out of the image: the device key and the spool key, the enrollment
# token, the cached configuration and the spool, the logs and the journal, the
# machine id, the SSH host keys, shell histories and cloud-init's record of
# this machine. A Wi-Fi password in the network configuration stops the
# check: it would go to every clone, and is removed by hand (docs/KIOSK_SETUP.md).
#
# Each clone makes its own machine id and SSH host keys on its first boot
# (neoprint-firstboot) and its own device key when the kiosk starts; it gets
# its enrollment token afterwards with neoprint-enroll.
#
# Run this LAST, immediately before capturing the image. The machine must not
# be booted again before capture: booting generates a new identity.
# ---------------------------------------------------------------------------
set -euo pipefail

DATA_ROOT="${NEOPRINT_DATA_ROOT:-/var/lib/neoprint}"
LOG_ROOT="${NEOPRINT_LOG_ROOT:-/var/log/neoprint}"
CHECK_ONLY=0

case "${1:-}" in
    --check) CHECK_ONLY=1 ;;
    "") ;;
    *) echo "usage: prepare-image.sh [--check]" >&2; exit 64 ;;
esac

if [ "$(id -u)" -ne 0 ]; then
    echo "This script must run as root." >&2
    exit 1
fi

log() { printf '[prepare-image] %s\n' "$*"; }

if [ "$CHECK_ONLY" -eq 0 ]; then
    log "removing the device identity"
    rm -f "$DATA_ROOT/device/key.pem"
    rm -f "$DATA_ROOT/device/spool.key"
    rm -f "$DATA_ROOT/device/enrollment-token" "$DATA_ROOT/device/enrollment-token.new"
    rm -f "$DATA_ROOT/device/"*.json

    # The work folder itself stays: provisioning gave it the setgid group the
    # converter account needs (docs/TASKS.md T-075); only what is in it goes.
    log "removing the cached configuration and the spool"
    if [ -d "$DATA_ROOT/spool" ]; then
        find "$DATA_ROOT/spool" -mindepth 1 -maxdepth 1 ! -name work -exec rm -rf {} +
        rm -rf "$DATA_ROOT/spool/work/"*
    fi
    if [ -d "$DATA_ROOT/state" ]; then
        find "$DATA_ROOT/state" -mindepth 1 -maxdepth 1 ! -name provisioning-version -exec rm -rf {} +
    fi
    # What the agent and the UI wrote about updates belongs to this machine too.
    for folder in updates agent; do
        [ -d "$DATA_ROOT/$folder" ] && find "$DATA_ROOT/$folder" -mindepth 1 -maxdepth 1 ! -name incoming -exec rm -rf {} +
    done

    log "removing the logs"
    rm -f "$LOG_ROOT/"*.log "$LOG_ROOT/"*.log.gz
    journalctl --rotate >/dev/null 2>&1 || true
    journalctl --vacuum-time=1s >/dev/null 2>&1 || true

    # A cloned machine-id would make every kiosk look like the same machine to the
    # fingerprint check, and would confuse systemd's own journal.
    log "clearing the machine id so each clone generates its own"
    if [ -e /etc/machine-id ]; then
        : >/etc/machine-id
    fi
    rm -f /var/lib/dbus/machine-id

    log "removing SSH host keys, so clones do not share them"
    rm -f /etc/ssh/ssh_host_*

    log "clearing shell histories"
    rm -f /root/.bash_history /home/*/.bash_history

    if command -v cloud-init >/dev/null 2>&1; then
        log "clearing cloud-init's record of this machine"
        cloud-init clean --logs >/dev/null 2>&1 || true
    fi
fi

# -----------------------------------------------------------------------------
# Verification: the whole point of this script is that nothing identifying is
# left, so it is checked rather than assumed.
# -----------------------------------------------------------------------------
log "verifying"

failed=0

left() {
    echo "  STILL PRESENT: $1" >&2
    failed=1
}

check_absent() {
    if [ -e "$1" ] && [ -s "$1" ]; then
        left "$1"
    fi
}

check_absent "$DATA_ROOT/device/key.pem"
check_absent "$DATA_ROOT/device/spool.key"
check_absent "$DATA_ROOT/device/enrollment-token"
check_absent /etc/machine-id
check_absent /var/lib/dbus/machine-id

for key in /etc/ssh/ssh_host_*; do
    [ -e "$key" ] && left "$key"
done

for history in /root/.bash_history /home/*/.bash_history; do
    [ -s "$history" ] && left "$history"
done

for logfile in "$LOG_ROOT/"*.log; do
    [ -s "$logfile" ] && left "$logfile"
done

if [ -d "$DATA_ROOT/spool" ] && [ -n "$(find "$DATA_ROOT/spool" -type f -print -quit)" ]; then
    left "files in $DATA_ROOT/spool"
fi

# A password in the network configuration would be on every clone.
for config in /etc/netplan/*.yaml /etc/NetworkManager/system-connections/*; do
    if [ -f "$config" ] && grep -Eqi '^[[:space:]]*(password|psk)[[:space:]]*[:=]' "$config"; then
        left "a Wi-Fi password in $config - remove the Wi-Fi network before capturing (docs/KIOSK_SETUP.md)"
    fi
done

if [ "$failed" -ne 0 ]; then
    echo "prepare-image did NOT finish cleanly. Do not capture this image." >&2
    exit 1
fi

log "clean: this machine can be captured as a golden image"
log "each clone will generate its own identity on first boot and register separately"
