# An Ubuntu 24.04 machine with systemd as PID 1, for running the kiosk
# provisioning script whole (desktop/provisioning/linux/provision.sh, T-327):
# the closest thing to a kiosk without a virtual machine. Run privileged, with
# the host's cgroups - tools/provision-test/check.sh does it.
#
# openssh-server is here because a minimal server install has it: its host
# keys are what prepare-image.sh takes away and a clone makes again. python3
# serves a stand-in backend for the --download check.
FROM ubuntu:24.04

ENV container=docker DEBIAN_FRONTEND=noninteractive

RUN apt-get update \
    && apt-get install -y --no-install-recommends systemd systemd-sysv dbus sudo iptables kmod openssh-server ca-certificates curl python3 \
    && rm -rf /var/lib/apt/lists/* \
    # Nothing a container cannot do should hold the boot up.
    && systemctl mask systemd-udevd.service systemd-udevd-kernel.socket systemd-udevd-control.socket systemd-logind.service getty.target console-getty.service

STOPSIGNAL SIGRTMIN+3
ENTRYPOINT ["/lib/systemd/systemd"]
