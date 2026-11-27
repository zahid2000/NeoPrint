#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# NeoPrint — GlitchTip's first setup (T-478, docs/MONITORING.md).
#
# Makes, if missing: the administrator, the "NeoPrint" organisation and one
# project per client (API, kiosk, mobile, admin panel); then writes the DSN of
# each into an env file Compose gives the API:
#
#   bash tools/monitoring/glitchtip-bootstrap.sh <env file> [compose args...]
#
#   <env file>      where the DSNs go (ErrorTracking__*Dsn=...)
#   compose args    how to reach the stack, e.g. -f docker-compose.yml -f ...
#
# The administrator's e-mail and password come from GLITCHTIP_ADMIN_EMAIL and
# GLITCHTIP_ADMIN_PASSWORD in the environment - never as arguments - and are
# used only the first time. Run again, it changes nothing and writes the same
# file: it is what deploy.sh runs on every deployment.
#
# The API reaches GlitchTip inside the stack (http://glitchtip:8000); the
# kiosk, the app and the panel through GLITCHTIP_DOMAIN, the address in the
# DSNs GlitchTip itself makes.
# ---------------------------------------------------------------------------
set -euo pipefail

out="${1:?usage: glitchtip-bootstrap.sh <env file> [compose args...]}"
shift

: "${GLITCHTIP_ADMIN_EMAIL:?set GLITCHTIP_ADMIN_EMAIL}"
: "${GLITCHTIP_ADMIN_PASSWORD:?set GLITCHTIP_ADMIN_PASSWORD}"
export GLITCHTIP_ADMIN_EMAIL GLITCHTIP_ADMIN_PASSWORD

DOCKER=(docker)
if ! docker info >/dev/null 2>&1 && sudo -n docker info >/dev/null 2>&1; then
    DOCKER=(sudo -n docker)
fi

compose() { "${DOCKER[@]}" compose "$@"; }

# Read by Django's shell from stdin. Prints "DSN <client> <dsn>" lines only.
read -r -d '' setup <<'PY' || true
import os

from apps.organizations_ext.constants import OrganizationUserRole
from apps.organizations_ext.models import Organization
from apps.projects.models import Project, ProjectKey
from apps.users.models import User

email = os.environ["GLITCHTIP_ADMIN_EMAIL"]
user = User.objects.filter(email=email).first()
if user is None:
    user = User.objects.create_superuser(email=email, password=os.environ["GLITCHTIP_ADMIN_PASSWORD"])

org = Organization.objects.filter(slug="neoprint").first()
if org is None:
    org = Organization.objects.create(name="NeoPrint", slug="neoprint")
if not org.users.filter(id=user.id).exists():
    org.add_user(user, OrganizationUserRole.OWNER)

for client, name, platform in [
    ("api", "NeoPrint API", "csharp-aspnetcore"),
    ("kiosk", "NeoPrint Kiosk", "csharp"),
    ("mobile", "NeoPrint Mobile", "flutter"),
    ("admin", "NeoPrint Admin", "javascript-angular"),
]:
    project = Project.objects.filter(organization=org, name=name).first()
    if project is None:
        project = Project.objects.create(organization=org, name=name, platform=platform)
    key = ProjectKey.objects.filter(project=project, is_active=True).order_by("created").first()
    if key is None:
        key = ProjectKey.objects.create(project=project)
    print("DSN", client, key.get_dsn())
PY

# GlitchTip migrates its database when it starts; until then the shell fails.
result=""
for _ in $(seq 1 60); do
    if result="$(printf '%s\n' "$setup" | compose "$@" exec -T \
        -e GLITCHTIP_ADMIN_EMAIL -e GLITCHTIP_ADMIN_PASSWORD \
        glitchtip ./manage.py shell 2>/dev/null)" && grep -q '^DSN admin ' <<<"$result"; then
        break
    fi
    result=""
    sleep 5
done

[ -n "$result" ] || { echo "[glitchtip] GlitchTip did not answer in time" >&2; exit 1; }

dsn() { sed -n "s/^DSN $1 //p" <<<"$result"; }

api="$(dsn api)"
# The API sends inside the stack: the same key and project, GlitchTip's own address.
api_internal="$(sed -E 's#^(https?)://([^@]+)@[^/]+(/.*)$#http://\2@glitchtip:8000\3#' <<<"$api")"

umask 077
tmp="$(mktemp "$out.XXXXXX")"
cat >"$tmp" <<EOF
# Written by tools/monitoring/glitchtip-bootstrap.sh (T-478). A DSN lets a
# client send errors to its GlitchTip project, nothing more.
ErrorTracking__ApiDsn=$api_internal
ErrorTracking__KioskDsn=$(dsn kiosk)
ErrorTracking__MobileDsn=$(dsn mobile)
ErrorTracking__AdminDsn=$(dsn admin)
EOF
mv "$tmp" "$out"
echo "[glitchtip] organisation, projects and DSNs ready ($out)"
