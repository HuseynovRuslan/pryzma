#!/usr/bin/env bash
# MEYDAN backup: PostgreSQL (pg_dump, custom format) + the file storage root (private originals, public-safe
# derivatives, covers). Produces three files in the target directory:
#   meydan-db-<stamp>.dump      database (schema + data + data-protection keys)
#   meydan-files-<stamp>.tar.gz uploaded/derived files (interrupted *.partial writes and health probes excluded)
#   meydan-<stamp>.sha256       checksums, verified by restore.sh
#
# Credentials never go on the command line or into the archive: libpq reads PGHOST/PGPORT/PGUSER/PGDATABASE and
# the password from PGPASSWORD or ~/.pgpass (chmod 600). Logs and secrets (environment, user-secrets) are not
# part of the backup — keep the deployment's secret store backed up separately.
#
# Usage: PGHOST=… PGUSER=… PGDATABASE=meydan [PGPASSWORD=…] scripts/backup.sh <storage-root> <target-dir>
set -euo pipefail

storage_root="${1:?usage: backup.sh <storage-root> <target-dir>}"
target="${2:?usage: backup.sh <storage-root> <target-dir>}"
: "${PGDATABASE:?PGDATABASE must name the database to back up}"

[ -d "$storage_root" ] || { echo "storage root not found: $storage_root" >&2; exit 1; }
mkdir -p "$target"
umask 077                                  # backups contain personal data: owner-only
stamp="$(date -u +%Y%m%dT%H%M%SZ)"
db="$target/meydan-db-$stamp.dump"
files="$target/meydan-files-$stamp.tar.gz"
sums="$target/meydan-$stamp.sha256"

echo "Backing up database $PGDATABASE …"
# --no-owner/--no-privileges: restorable into a fresh database owned by a different role.
pg_dump --format=custom --compress=6 --no-owner --no-privileges --file="$db.partial" "$PGDATABASE"
mv "$db.partial" "$db"

echo "Backing up files from $storage_root …"
tar --create --gzip --file="$files.partial" --directory="$storage_root" \
    --exclude='*.partial' --exclude='.health-*' .
mv "$files.partial" "$files"

( cd "$target" && sha256sum "$(basename "$db")" "$(basename "$files")" > "$(basename "$sums")" )
pg_restore --list "$db" > /dev/null         # the dump is readable
tar --list --gzip --file="$files" > /dev/null

echo "Backup complete:"
ls -l "$db" "$files" "$sums"
