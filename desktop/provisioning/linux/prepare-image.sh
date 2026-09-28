#!/bin/bash
# ---------------------------------------------------------------------------
# Prepare a provisioned machine to be cloned (plan section 7.12).
#
# Removes everything that identifies this particular machine, so the image can
# be copied onto many kiosks and each one registers as its own device on first
# boot. Getting this wrong is how two kiosks end up sharing an identity, so the
# script verifies afterwards that nothing was left behind.
#
#   sudo ./prepare-image.sh
#
# Run this LAST, immediately before capturing the image. The machine must not
# be booted again before capture: booting generates a new device key.
# ---------------------------------------------------------------------------
set -euo pipefail

DATA_ROOT="/var/lib/neoprint"
LOG_ROOT="/var/log/neoprint"

if [ "$(id -u)" -ne 0 ]; then
    echo "This script must run as root." >&2
    exit 1
fi

log() { printf '[prepare-image] %s\n' "$*"; }

log "removing the device identity"
rm -f "$DATA_ROOT/device/key.pem"
rm -f "$DATA_ROOT/device/spool.key"
rm -f "$DATA_ROOT/device/enrollment-token"
rm -f "$DATA_ROOT/device/"*.json

# The work folder itself stays: provisioning gave it the setgid group the
# converter account needs (docs/TASKS.md T-075); only what is in it goes.
log "removing the cached configuration and the spool"
find "$DATA_ROOT/spool" -mindepth 1 -maxdepth 1 ! -name work -exec rm -rf {} +
rm -rf "$DATA_ROOT/spool/work/"*
find "$DATA_ROOT/state" -mindepth 1 -maxdepth 1 ! -name provisioning-version -exec rm -rf {} +

log "removing the logs"
rm -f "$LOG_ROOT/"*.log
journalctl --rotate >/dev/null 2>&1 || true
journalctl --vacuum-time=1s >/dev/null 2>&1 || true

# A cloned machine-id would make every kiosk look like the same machine to the
# fingerprint check, and would confuse systemd's own journal.
log "clearing the machine id so each clone generates its own"
: >/etc/machine-id
rm -f /var/lib/dbus/machine-id

log "removing SSH host keys, so clones do not share them"
rm -f /etc/ssh/ssh_host_*

log "clearing shell history"
rm -f /root/.bash_history /home/*/.bash_history

# -----------------------------------------------------------------------------
# Verification: the whole point of this script is that nothing identifying is
# left, so it is checked rather than assumed.
# -----------------------------------------------------------------------------
log "verifying"

failed=0

check_absent() {
    if [ -e "$1" ] && [ -s "$1" ]; then
        echo "  STILL PRESENT: $1" >&2
        failed=1
    fi
}

check_absent "$DATA_ROOT/device/key.pem"
check_absent "$DATA_ROOT/device/spool.key"
check_absent "$DATA_ROOT/device/enrollment-token"
check_absent /etc/machine-id
check_absent /var/lib/dbus/machine-id

if [ "$failed" -ne 0 ]; then
    echo "prepare-image did NOT finish cleanly. Do not capture this image." >&2
    exit 1
fi

log "clean: this machine can be captured as a golden image"
log "each clone will generate its own device key and register separately"
