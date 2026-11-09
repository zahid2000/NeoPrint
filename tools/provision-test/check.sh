#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# The kiosk provisioning script, whole, on Ubuntu 24.04 with systemd (T-327):
# the closest thing to a kiosk without a virtual machine. Only Docker is
# needed (and the .NET SDK, to publish the kiosk); run from Git Bash, WSL or Linux:
#
#   bash tools/provision-test/check.sh
#
# 1. A machine boots (tools/provision-test/Dockerfile) and a setup stick is
#    put in it: the provisioning scripts and the published kiosk and agent.
# 2. provision.sh runs from the stick: all thirteen steps, the units enabled,
#    the folders and their owners, the token, the sudo rules, the firewall.
# 3. It runs again: nothing fails and nothing it wrote changes (idempotent).
# 4. A stick with a file missing: the step says which it is and what to do,
#    and the system files it had changed are put back.
# 4b. --download (only where the staging release key is, docs/RELEASE_SIGNING.md):
#    the newest UI and agent from a stand-in backend, each checked by the agent
#    on the stick before it is installed; a package changed on the way is refused.
# 5. prepare-image.sh cleans the machine and its --check agrees; two clones
#    of the image boot with a machine id and SSH host keys each of their own.
#
# UBUNTU=26.04 (default 24.04): that release. DESKTOP=1: the machine is made
# like a desktop install first - a graphical login (a stand-in display manager
# aliased display-manager.service), graphical.target as the default and, on
# 26.04, sudo-rs as its sudo - and provision.sh must switch the login off (T-333).
#
# What a container cannot show - cage on a screen, the flash drive, a reboot
# into kiosk mode - is checked on the real device (M19b, T-331).
# ---------------------------------------------------------------------------
set -euo pipefail

root="$(cd "$(dirname "$0")/../.." && pwd)"
image="neoprint-provision-test"
machine="neoprint-provision-machine"
version="${VERSION:-0.9.0-m19a}"
ubuntu="${UBUNTU:-24.04}"
desktop="${DESKTOP:-0}"
sudo_package=sudo
if [ "$desktop" = 1 ] && [ "$ubuntu" != 24.04 ]; then sudo_package=sudo-rs; fi
work="$(mktemp -d)"
failures=0

cleanup() {
    docker rm -f "$machine" "$machine-clone-1" "$machine-clone-2" >/dev/null 2>&1 || true
    docker rmi -f "$image-golden" >/dev/null 2>&1 || true
    rm -rf "$work"
}
trap cleanup EXIT

check() { # condition-result text
    if [ "$1" = ok ]; then echo "ok     $2"; else echo "WRONG  $2"; failures=$((failures + 1)); fi
}
is() { [ "$1" = "$2" ] && echo ok || echo no; }
win() { if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else printf '%s' "$1"; fi; }
on() { local name="$1"; shift; MSYS_NO_PATHCONV=1 docker exec "$name" bash -c "$*"; }

boot() { # name image
    MSYS_NO_PATHCONV=1 docker run -d --name "$1" --privileged --cgroupns=host \
        -v /sys/fs/cgroup:/sys/fs/cgroup:rw --tmpfs /run --tmpfs /run/lock "$2" >/dev/null
    for _ in $(seq 1 30); do
        state="$(MSYS_NO_PATHCONV=1 docker exec "$1" systemctl is-system-running 2>/dev/null || true)"
        case "$state" in running|degraded) return 0 ;; esac
        sleep 1
    done
    echo "the machine $1 did not boot ($state)" >&2
    return 1
}

# systemd 258 and later (Ubuntu 26.04) refuse to run on cgroup v1, which Docker
# Desktop on an older WSL 2 still has; GitHub's runners have v2.
if [ "$ubuntu" != 24.04 ] && [ "$(docker info --format '{{.CgroupVersion}}' 2>/dev/null)" = 1 ]; then
    echo "Ubuntu $ubuntu's systemd does not run on this Docker's cgroup v1: run it on a cgroup v2 host (the CI workflow does), or UBUNTU=24.04" >&2
    exit 2
fi

echo "== the machine: Ubuntu $ubuntu$([ "$desktop" = 1 ] && echo ", made like a desktop install")"
MSYS_NO_PATHCONV=1 docker build -q --build-arg UBUNTU="$ubuntu" --build-arg SUDO="$sudo_package" -t "$image" "$(win "$root/tools/provision-test")" >/dev/null
docker rm -f "$machine" >/dev/null 2>&1 || true
boot "$machine" "$image"
check "$(is "$(on "$machine" '. /etc/os-release; echo "$VERSION_ID"')" "$ubuntu")" "the machine is Ubuntu $ubuntu"
if [ "$desktop" = 1 ]; then
    # A desktop's graphical login, as GDM is: a unit aliased display-manager.service, graphical.target the default.
    on "$machine" 'printf "[Unit]\nDescription=stand-in display manager\n[Service]\nExecStart=/bin/sleep infinity\n[Install]\nAlias=display-manager.service\nWantedBy=graphical.target\n" >/etc/systemd/system/gdm3.service && systemctl daemon-reload && systemctl enable gdm3.service && systemctl set-default graphical.target' >/dev/null 2>&1
    check "$(is "$(on "$machine" 'systemctl get-default; readlink -f /etc/systemd/system/display-manager.service')" "graphical.target
/etc/systemd/system/gdm3.service")" "it boots to a graphical login, as a desktop install does"
fi

echo "== the setup stick"
mkdir -p "$work/stick/package"
# The agent self-contained, as a release's is: the machine has no .NET, and
# neoprint-service-pin runs it (T-334).
for project in NeoPrint.Kiosk NeoPrint.Kiosk.Agent; do
    out="$work/stick/package/app"
    contained=false
    if [ "$project" = NeoPrint.Kiosk.Agent ]; then out="$work/stick/package/agent"; contained=true; fi
    dotnet publish "$(win "$root/desktop/src/$project/$project.csproj")" -c Release -r linux-x64 --self-contained "$contained" \
        -p:EnableWindowsTargeting=true -p:ReleaseKeySet=staging -p:Version="$version" -o "$(win "$out")" -v q >/dev/null
done
printf '%s' "$version" >"$work/stick/package/VERSION"
cp -R "$root/desktop/provisioning/linux" "$work/stick/provisioning"
# A stick on which one file did not arrive (step 4).
cp -R "$work/stick" "$work/broken"
rm "$work/broken/provisioning/systemd/neoprint-firstboot.service"
MSYS_NO_PATHCONV=1 docker cp "$(win "$work/stick")" "$machine:/media/stick"
MSYS_NO_PATHCONV=1 docker cp "$(win "$work/broken")" "$machine:/media/broken"
token="$(od -An -N32 -tx1 /dev/urandom | tr -d ' \n')"

provision() { # stick
    on "$machine" "bash /media/$1/provisioning/provision.sh --offline-package /media/$1/package --api-url http://neoprint.invalid --channel pilot --enrollment-token $token"
}

if [ "$desktop" = 1 ]; then
    echo "== a desktop's Wi-Fi that is its user's alone"
    # A stand-in nmcli: NetworkManager running, one Wi-Fi connection, the user's
    # alone or everyone's as /etc/nmcli-stub says (the container has no Wi-Fi).
    on "$machine" 'cat >/usr/local/bin/nmcli <<"STUB"
#!/bin/sh
mode="$(cat /etc/nmcli-stub)"
case "$*" in
    "-t -f RUNNING general") echo running ;;
    "-t -f UUID,TYPE connection show --active") echo "5b3c0f4e-0000-4000-8000-000000000001:802-11-wireless" ;;
    "-g connection.id "*) echo "Ofis WiFi" ;;
    "-g connection.permissions "*) [ "$mode" = user ] && echo "user:user" || echo "" ;;
    "-g 802-11-wireless-security.psk-flags "*) [ "$mode" = user ] && echo "1 (agent-owned)" || echo "0 (none)" ;;
esac
STUB
chmod 755 /usr/local/bin/nmcli && echo user >/etc/nmcli-stub'
    wifi="$(provision stick 2>&1)" && code=0 || code=$?
    echo "$wifi" | grep -E "ERROR|what to do" || true
    check "$([ "$code" -ne 0 ] && echo "$wifi" | grep -q 'the Wi-Fi network "Ofis WiFi" is this user.s alone' && echo ok)" "the script stops before anything (exit $code)"
    check "$(echo "$wifi" | grep -q 'Make available to other users' && echo ok)" "and says what to do"
    check "$(echo "$wifi" | grep -q 'step 1/13' || echo ok)" "no step ran"
    check "$(is "$(on "$machine" 'systemctl get-default')" graphical.target)" "the desktop's login is untouched"
    on "$machine" 'echo system >/etc/nmcli-stub'
fi

echo "== provision.sh, the first time (packages take a few minutes)"
first="$(provision stick 2>&1)" && code=0 || code=$?
echo "$first" | grep -E "step [0-9]+/13|ERROR|what to do" || true
check "$(is "$code" 0)" "provision.sh finished (exit $code)"
check "$(echo "$first" | grep -q "13 of 13 steps" && echo ok)" "all thirteen steps ran"
check "$(is "$(on "$machine" 'systemctl is-enabled neoprint-agent neoprint-kiosk neoprint-firstboot | sort -u')" enabled)" "the agent, the kiosk and the first-boot units are enabled"
check "$(is "$(on "$machine" 'stat -c %U:%a /var/lib/neoprint/device')" kiosk:700)" "the device folder is the kiosk account's alone"
check "$(is "$(on "$machine" 'stat -c %U:%G:%a /var/lib/neoprint/updates/downloads /var/lib/neoprint/updates/requests /var/lib/neoprint/updates/health | sort -u')" kiosk:neoprint:2770)" "the UI's update folders are the kiosk account's, the agent's by the group (T-368)"
check "$(is "$(on "$machine" 'stat -c %U:%a /var/lib/neoprint/device/enrollment-token')" kiosk:600)" "the enrollment token is the kiosk account's, read by nobody else"
check "$(is "$(on "$machine" 'readlink /opt/neoprint/app/current')" "versions/$version")" "the kiosk from the stick is the current version"
check "$(is "$(on "$machine" 'cat /var/lib/neoprint/state/provisioning-version')" 0.7.0)" "the provisioning version is written"
check "$(on "$machine" 'visudo -c >/dev/null && echo ok')" "the sudo rules pass their check"
check "$(on "$machine" 'test -x /usr/local/sbin/neoprint-enroll && test -x /usr/local/lib/neoprint/neoprint-firstboot && echo ok')" "neoprint-enroll and neoprint-firstboot are installed"
check "$(is "$(on "$machine" 'grep -c "uid-owner neoprint-convert" /etc/ufw/before.rules')" 1)" "the converters have no network (one rule)"
ufw="$(on "$machine" 'ufw status | head -1')"
check "$(is "$ufw" "Status: active")" "the firewall is on ($ufw)"
check "$(on "$machine" 'command -v cage >/dev/null && command -v Xwayland >/dev/null && echo ok')" "cage is there, and Xwayland for the kiosk UI (X11) under it"
check "$(on "$machine" 'for lib in libX11.so.6 libICE.so.6 libSM.so.6 libfontconfig.so.1 libXrandr.so.2 libXi.so.6 libXcursor.so.1 libicuuc.so; do ldconfig -p | grep -q "$lib" || exit 1; done; echo ok')" "and the libraries the kiosk UI and the agent load: X11, fontconfig, ICU"
check "$(is "$(on "$machine" 'systemctl get-default')" multi-user.target)" "the machine boots to multi-user.target, where the kiosk is"
echo "== the service PIN (T-334)"
pinned="$(on "$machine" 'printf "24681357\n24681357\n" | neoprint-service-pin' 2>&1)" && code=0 || code=$?
check "$(is "$code" 0)" "neoprint-service-pin sets it, asked twice (exit $code)"
check "$(is "$(on "$machine" 'stat -c %U:%G:%a /etc/neoprint/service-pin')" root:kiosk:640)" "root's, read by the kiosk account and nobody else"
check "$(on "$machine" 'grep -q "^pbkdf2.sha256.[0-9][0-9]*." /etc/neoprint/service-pin && ! grep -q 24681357 /etc/neoprint/service-pin && echo ok')" "kept only as its hash"
check "$(grep -q 24681357 <<<"$pinned" || echo ok)" "and the digits are printed nowhere"
mismatch="$(on "$machine" 'printf "24681357\n11111111\n1234\n1234\n12345678\n87654321\n" | neoprint-service-pin' 2>&1)" && code=0 || code=$?
check "$([ "$code" -ne 0 ] && on "$machine" 'grep -q "^pbkdf2" /etc/neoprint/service-pin && echo ok')" "three wrong tries set nothing and keep the PIN there was (exit $code)"
check "$(on "$machine" 'neoprint-service-pin 24681357 >/dev/null 2>&1 || echo ok')" "it is never an argument"

if [ "$desktop" = 1 ]; then
    check "$(is "$(on "$machine" 'cat /etc/neoprint/desktop-login')" gdm3.service)" "the desktop's login is written down for the service menu"
    on "$machine" 'systemctl start neoprint-maintenance.service' >/dev/null 2>&1 || true
    check "$(is "$(on "$machine" 'systemctl is-active gdm3.service')" active)" "the service menu's way out brings the desktop's login back (T-334)"
    on "$machine" 'systemctl stop gdm3.service' >/dev/null 2>&1 || true
    check "$(on "$machine" 'test ! -e /etc/systemd/system/display-manager.service && [ "$(systemctl is-enabled gdm3.service)" = disabled ] && echo ok')" "the desktop's graphical login is switched off"
    check "$(echo "$first" | grep -q "a desktop install: its graphical login (gdm3.service, graphical.target) is switched off" && echo ok)" "and the script says so"
    # A here-string, not a pipe: grep -q stops at a match this early in the output and, with pipefail, the echo it cut short would fail the check.
    check "$(grep -q 'the Wi-Fi network "Ofis WiFi" comes up with nobody signed in' <<<"$first" && echo ok)" "an everyone's Wi-Fi is let through"
fi

echo "== provision.sh, a second time"
sums='md5sum /etc/sudoers.d/neoprint /etc/ufw/before.rules /etc/udisks2/mount_options.conf /etc/sysctl.d/99-neoprint-kiosk.conf /etc/systemd/system/neoprint-*.service /etc/polkit-1/rules.d/50-neoprint-udisks.rules'
before="$(on "$machine" "$sums")"
second="$(provision stick 2>&1)" && code=0 || code=$?
check "$(is "$code" 0)" "it finishes again (exit $code)"
check "$(is "$(on "$machine" "$sums")" "$before")" "and nothing it wrote has changed"
check "$(is "$(on "$machine" 'grep -c "uid-owner neoprint-convert" /etc/ufw/before.rules')" 1)" "the converter rule is still there once"

echo "== a step that fails"
on "$machine" 'echo "# changed by hand" >>/etc/systemd/system/neoprint-agent.service'
broken="$(on "$machine" "bash /media/broken/provisioning/provision.sh --offline-package /media/broken/package --api-url http://neoprint.invalid" 2>&1)" && code=0 || code=$?
echo "$broken" | grep -E "ERROR|what to do|put back" || true
check "$([ "$code" -ne 0 ] && echo ok)" "the script stops (exit $code)"
check "$(echo "$broken" | grep -q "step 13/13 (services) failed" && echo ok)" "it says which step failed"
check "$(echo "$broken" | grep -q "what to do: the services could not be set up" && echo ok)" "and what to do"
check "$(on "$machine" 'tail -1 /etc/systemd/system/neoprint-agent.service' | grep -q "changed by hand" && echo ok)" "the unit it had replaced is put back as it was"
provision stick >/dev/null 2>&1

staging_keys="${NEOPRINT_STAGING_KEYS:-${USERPROFILE:-$HOME}/.neoprint/release-keys/staging}"
if [ -f "$staging_keys/current.private.pem" ]; then
    echo "== --download: the newest release from the backend, checked on the stick"
    dl="0.9.1-m19a"
    srv="$work/srv"
    rel="$work/rel"
    mkdir -p "$rel"
    dotnet build "$(win "$root/tools/release-sign")" -c Release -v q >/dev/null
    sign() { dotnet "$(win "$root/tools/release-sign/bin/Release/net8.0/release-sign.dll")" "$@"; }
    for component in app agent; do
        name="neoprint-kiosk-$component-$dl-linux-x64-staging.tar.gz"
        tar -czf "$rel/$name" -C "$work/stick/package/$component" .
        sign manifest --package "$(win "$rel/$name")" --component "$(tr '[:lower:]' '[:upper:]' <<<"${component:0:1}")${component:1}" \
            --platform linux-x64 --key-set staging --version "$dl" --min-agent-version 0.1.0 --commit c0ffee1 --out "$(win "$rel/$name.manifest.json")" >/dev/null
    done
    sign sign-staging --dir "$(win "$rel")" >/dev/null
    for component in app agent; do
        name="neoprint-kiosk-$component-$dl-linux-x64-staging.tar.gz"
        files="$srv/api/releases/$component/linux-x64/$dl"
        mkdir -p "$files" "$srv/api/releases/latest/$component"
        cp "$rel/$name" "$files/package"
        cp "$rel/$name.manifest.json" "$files/manifest"
        cp "$rel/$name.manifest.json.sig" "$files/signature"
        printf '{"component":"%s","platform":"linux-x64","version":"%s","channel":"pilot","keySet":"staging","packagePath":"api/releases/%s/linux-x64/%s/package"}' \
            "$component" "$dl" "$component" "$dl" >"$srv/api/releases/latest/$component/linux-x64"
    done
    # The checker the stick carries: an agent built with the keys it trusts,
    # self-contained as a release package is - the machine has no .NET.
    dotnet publish "$(win "$root/desktop/src/NeoPrint.Kiosk.Agent/NeoPrint.Kiosk.Agent.csproj")" -c Release -r linux-x64 --self-contained true         -p:EnableWindowsTargeting=true -p:ReleaseKeySet=staging -p:Version="$version" -o "$(win "$work/stick/verifier")" -v q >/dev/null
    MSYS_NO_PATHCONV=1 docker cp "$(win "$work/stick/verifier")" "$machine:/media/stick/verifier"
    MSYS_NO_PATHCONV=1 docker cp "$(win "$srv")" "$machine:/media/srv"
    on "$machine" 'cd /media/srv && nohup python3 -m http.server 8088 >/dev/null 2>&1 &'
    sleep 2
    downloaded="$(on "$machine" "bash /media/stick/provisioning/provision.sh --download --api-url http://127.0.0.1:8088 --channel pilot" 2>&1)" && code=0 || code=$?
    echo "$downloaded" | grep -E "signature checked|did NOT pass|ERROR|what to do" || true
    check "$(is "$code" 0)" "--download finished (exit $code)"
    check "$([ "$(echo "$downloaded" | grep -c "signature checked with the keys on the stick")" -eq 2 ] && echo ok)" "the UI and the agent were each checked on the stick"
    check "$(is "$(on "$machine" 'readlink /opt/neoprint/app/current; readlink /opt/neoprint/agent/current' | sort -u)" "versions/$dl")" "and both are installed as the current version"

    # A package changed on the way: refused, nothing installed.
    on "$machine" "printf x >> /media/srv/api/releases/agent/linux-x64/$dl/package"
    on "$machine" 'rm -rf /opt/neoprint/agent/versions/0.9.1-m19a; ln -sfn versions/'"$version"' /opt/neoprint/agent/current'
    tampered="$(on "$machine" "bash /media/stick/provisioning/provision.sh --download --api-url http://127.0.0.1:8088 --channel pilot" 2>&1)" && code=0 || code=$?
    echo "$tampered" | grep -E "did NOT pass|ERROR|what to do" || true
    check "$([ "$code" -ne 0 ] && echo "$tampered" | grep -q "agent $dl did NOT pass the signature check" && echo ok)" "a package changed on the way is refused (exit $code)"
    check "$(is "$(on "$machine" 'readlink /opt/neoprint/agent/current')" "versions/$version")" "and the agent it would have replaced stays"
    # The bracket keeps pkill from matching its own command line.
    on "$machine" 'pkill -f "http.serve[r] 8088" || true'
    provision stick >/dev/null 2>&1
else
    echo "== --download: skipped - no staging release key at $staging_keys (docs/RELEASE_SIGNING.md)"
fi

echo "== the golden image"
on "$machine" 'systemctl stop neoprint-kiosk neoprint-agent >/dev/null 2>&1 || true'
prepared="$(on "$machine" 'bash /media/stick/provisioning/prepare-image.sh' 2>&1)" && code=0 || code=$?
check "$(is "$code" 0)" "prepare-image.sh cleans the machine"
check "$(on "$machine" 'bash /media/stick/provisioning/prepare-image.sh --check >/dev/null 2>&1 && echo ok')" "and its --check finds nothing left"
check "$(on "$machine" 'test ! -s /etc/machine-id && test ! -e /var/lib/neoprint/device/enrollment-token && ! ls /etc/ssh/ssh_host_* >/dev/null 2>&1 && echo ok')" "no machine id, no token, no SSH host keys in the image"
check "$(on "$machine" 'test ! -e /etc/neoprint/service-pin && echo ok')" "and no service PIN: each clone is given its own"
on "$machine" 'echo "secret" > /root/.bash_history'
check "$(on "$machine" 'bash /media/stick/provisioning/prepare-image.sh --check >/dev/null 2>&1 || echo ok')" "--check stops on a history left behind"
on "$machine" 'rm -f /root/.bash_history'
docker stop "$machine" >/dev/null
docker commit "$machine" "$image-golden" >/dev/null

for clone in 1 2; do boot "$machine-clone-$clone" "$image-golden"; done
sleep 2
id1="$(on "$machine-clone-1" 'cat /etc/machine-id')"
id2="$(on "$machine-clone-2" 'cat /etc/machine-id')"
key1="$(on "$machine-clone-1" 'ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub 2>/dev/null | cut -d" " -f2')"
key2="$(on "$machine-clone-2" 'ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub 2>/dev/null | cut -d" " -f2')"
check "$([ -n "$id1" ] && [ -n "$id2" ] && [ "$id1" != "$id2" ] && echo ok)" "the two clones have a machine id each of their own"
check "$([ -n "$key1" ] && [ -n "$key2" ] && [ "$key1" != "$key2" ] && echo ok)" "and SSH host keys of their own"
check "$(is "$(on "$machine-clone-1" 'systemctl is-active neoprint-firstboot')" active)" "made by neoprint-firstboot on their first boot"

echo
if [ "$failures" -eq 0 ]; then echo "Every check passed."; else echo "$failures check(s) failed."; exit 1; fi
