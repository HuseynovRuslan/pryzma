#!/usr/bin/env bash
# MEYDAN restore into a NEW, EMPTY database and a NEW, EMPTY storage directory — never over live data.
# Afterwards point ConnectionStrings__Meydan / Uploads__StorageRoot at them, run `Meydan.Api migrate`
# (applies any newer migrations; a no-op for the same version) and check /health/ready.
#
# Usage: PGHOST=… PGUSER=… [PGPASSWORD=…] scripts/restore.sh <backup-dir> <stamp> <new-database> <new-storage-root>
#   <stamp> is the part of the file names after "meydan-db-" and before ".dump", e.g. 20260918T120000Z
set -euo pipefail

dir="${1:?usage: restore.sh <backup-dir> <stamp> <new-database> <new-storage-root>}"
stamp="${2:?stamp missing}"
database="${3:?new database name missing}"
storage_root="${4:?new storage root missing}"

db="$dir/meydan-db-$stamp.dump"
files="$dir/meydan-files-$stamp.tar.gz"
sums="$dir/meydan-$stamp.sha256"

echo "Verifying checksums …"
( cd "$dir" && sha256sum --check --strict "$(basename "$sums")" )

if [ -e "$storage_root" ] && [ -n "$(ls -A "$storage_root" 2>/dev/null)" ]; then
  echo "refusing to restore files into a non-empty directory: $storage_root" >&2; exit 1
fi
if psql --dbname=postgres --tuples-only --no-align \
     --command="SELECT 1 FROM pg_database WHERE datname = '$database'" | grep -q 1; then
  echo "refusing to restore into an existing database: $database (create a new one)" >&2; exit 1
fi

echo "Creating database $database …"
createdb --encoding=UTF8 --template=template0 "$database"
echo "Restoring database …"
pg_restore --no-owner --no-privileges --exit-on-error --dbname="$database" "$db"

echo "Restoring files into $storage_root …"
mkdir -p "$storage_root"
tar --extract --gzip --file="$files" --directory="$storage_root"

echo "Restore complete. Next: set ConnectionStrings__Meydan (Database=$database) and Uploads__StorageRoot=$storage_root,"
echo "run 'Meydan.Api migrate', start the API and check /health/ready."
