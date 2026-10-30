#!/usr/bin/env bash
# Weekly restore drill (root, /etc/cron.d/meydan) — a backup nobody restores is a hope. Restores the newest dump
# into a THROWAWAY database inside the db container, counts a few tables, verifies the files archive and the
# checksums, then drops the throwaway database. The production database and the uploads directory are never touched.
set -euo pipefail

base=/opt/meydan
target=/var/lib/meydan/backups
compose() { docker compose --env-file "$base/.env" -f "$base/current/deploy/compose.yml" "$@"; }

dump="$(ls -1t "$target"/meydan-db-*.dump 2>/dev/null | head -1)"
[ -n "$dump" ] || { echo "no dump in $target" >&2; exit 1; }
stamp="$(basename "$dump" .dump)"; stamp="${stamp#meydan-db-}"
files="$target/meydan-files-$stamp.tar.gz"
sums="$target/meydan-$stamp.sha256"

(cd "$target" && sha256sum --check --quiet "$sums")
tar --list --gzip --file="$files" > /dev/null

scratch="meydan_restore_test_$$"
cleanup() { compose exec -T db dropdb -U postgres --if-exists "$scratch" >/dev/null 2>&1 || true; }
trap cleanup EXIT

compose exec -T db createdb -U postgres "$scratch"
compose exec -T db pg_restore -U postgres --dbname "$scratch" --no-owner --no-privileges --exit-on-error < "$dump"

count() { compose exec -T db psql -U postgres -d "$scratch" -Atc "select count(*) from \"$1\""; }
echo "$(date -u +%FT%TZ) restore ok from $(basename "$dump"): $(count AspNetUsers) users, $(count Submissions) submissions, $(count __EFMigrationsHistory) migrations; files archive and checksums verified"
