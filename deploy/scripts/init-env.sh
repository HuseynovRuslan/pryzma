#!/usr/bin/env bash
# One-time: writes /opt/meydan/.env with freshly generated secrets. Refuses to overwrite an existing file.
# Nothing is printed except the path. Usage (on the VPS, as deploy):
#   ADMIN_EMAIL=owner@example.com deploy/scripts/init-env.sh [/opt/meydan/.env]
set -euo pipefail

target="${1:-/opt/meydan/.env}"
: "${ADMIN_EMAIL:?ADMIN_EMAIL must be set (the first admin e-mail)}"
[ -e "$target" ] && { echo "refusing: $target already exists" >&2; exit 1; }

rand() { openssl rand -base64 "$1" | tr -d '/+=\n' | cut -c1-"$2"; }
# Identity password rules: >= 8 characters, at least one digit and one lower-case letter (guaranteed below).
admin_password="$(rand 24 20)a7"

umask 077
cat > "$target" <<EOF
# MEYDAN production secrets — generated $(date -u +%Y-%m-%dT%H:%M:%SZ) by deploy/scripts/init-env.sh. Never committed.
POSTGRES_PASSWORD=$(rand 36 40)
APP_DB_NAME=meydan
APP_DB_USER=meydan
APP_DB_PASSWORD=$(rand 36 40)

SHARED_PROXY_NETWORK=attendanceqr_default
APP_HTTP_PORT=8040

IMAGE_TAG=local
Jwt__SigningKey=$(rand 60 64)
App__PublicUrl=https://prizma.qrlog.az
AllowedHosts=prizma.qrlog.az;meydan.qrlog.az;localhost;meydan-api
ReverseProxy__KnownNetworks__0=172.16.0.0/12

Email__Mode=Disabled
Email__FromAddress=
Email__FromName=MEYDAN
Email__Smtp__Host=
Email__Smtp__Port=587
Email__Smtp__Security=StartTls
Email__Smtp__Username=
Email__Smtp__Password=

QrlogSso__Enabled=false
QrlogSso__VouchSecret=$(rand 48 48)
QrlogSso__AuthorizeUrl=https://app.qrlog.az/signin/meydan?code={code}&return={return}

# First admin: created by the first migrate run only. The API container never receives these two values.
# After the first sign-in, change the password on the site (or remove the line) — the seeder never updates it.
Seed__AdminEmail=$ADMIN_EMAIL
Seed__AdminPassword=$admin_password
EOF
chmod 600 "$target"
echo "written: $target"
