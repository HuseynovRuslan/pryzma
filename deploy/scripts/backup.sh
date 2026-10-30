#!/usr/bin/env bash
# Nightly (and pre-deploy) backup on the VPS, run as root from /etc/cron.d/meydan:
#   /opt/meydan/current/deploy/scripts/backup.sh
# Produces, in /var/lib/meydan/backups (root only):
#   meydan-db-<stamp>.dump        pg_dump custom format, taken inside the db container (no password on any command line)
#   meydan-files-<stamp>.tar.gz   /var/lib/meydan/uploads (originals, derivatives, covers; *.partial and probes excluded)
#   meydan-<stamp>.sha256         checksums, verified by restore-test.sh
# Same-disk only; copy the directory off the machine for a real disaster plan. Retention: 30 days (conservative; no
# rule was given). Secrets (/opt/meydan/.env) are NOT included — back that file up separately.
set -euo pipefail

base=/opt/meydan
target=/var/lib/meydan/backups
uploads=/var/lib/meydan/uploads
retention_days=30
compose() { docker compose --env-file "$base/.env" -f "$base/current/deploy/compose.yml" "$@"; }

mkdir -p "$target"
chmod 700 "$target"
umask 077
stamp="$(date -u +%Y%m%dT%H%M%SZ)"
db="$target/meydan-db-$stamp.dump"
files="$target/meydan-files-$stamp.tar.gz"
sums="$target/meydan-$stamp.sha256"

db_name="$(grep -E '^APP_DB_NAME=' "$base/.env" | cut -d= -f2)"
db_name="${db_name:-meydan}"

# pg_dump runs inside the container as the postgres OS user over the unix socket (trust), so no password is
# involved; the dump streams out over stdout.
compose exec -T db pg_dump --format=custom --compress=6 --no-owner --no-privileges -U postgres "$db_name" > "$db.partial"
mv "$db.partial" "$db"

tar --create --gzip --file="$files.partial" --directory="$uploads" \
    --exclude='*.partial' --exclude='.health-*' .
mv "$files.partial" "$files"

# Readable before it is trusted.
compose exec -T db pg_restore --list < "$db" > /dev/null
tar --list --gzip --file="$files" > /dev/null

(cd "$target" && sha256sum "$(basename "$db")" "$(basename "$files")" > "$sums")

find "$target" -maxdepth 1 -type f \( -name 'meydan-db-*.dump' -o -name 'meydan-files-*.tar.gz' -o -name 'meydan-*.sha256' \) \
     -mtime +"$retention_days" -delete
echo "$(date -u +%FT%TZ) backup ok: $(basename "$db") ($(du -h "$db" | cut -f1)), $(basename "$files") ($(du -h "$files" | cut -f1))"
