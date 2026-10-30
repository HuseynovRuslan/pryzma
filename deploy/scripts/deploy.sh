#!/usr/bin/env bash
# Deploys one release on the VPS. Usage (as deploy, from anywhere):
#   /opt/meydan/current/deploy/scripts/deploy.sh /tmp/meydan-<id>.tgz
# Order: unpack → safety backup (when a database exists) → build images → start db → MIGRATE (explicit, one-shot)
# → start api + web → wait for /health/ready → switch `current`. A release that never becomes healthy leaves the
# previous containers... replaced: the rollback is deploy.sh with the previous release (deploy/README.md).
#
# Application rollback is not a database downgrade. EF migrations are forward-only; a release whose migration
# renamed or dropped something cannot be undone by starting the old code — restore the safety dump instead.
set -euo pipefail

archive="${1:?usage: deploy.sh <release.tgz>}"
base=/opt/meydan
env_file="$base/.env"
[ -f "$env_file" ] || { echo "missing $env_file — run deploy/scripts/init-env.sh first" >&2; exit 1; }
[ -d /var/lib/meydan/uploads ] || { echo "missing /var/lib/meydan/uploads (see deploy/README.md, 'Server preparation')" >&2; exit 1; }

stamp="$(date -u +%Y%m%dT%H%M%SZ)"
release_dir="$base/releases/$stamp"
# Unpack beside the final name and rename into place: a release directory either does not exist or is complete,
# never half-written — and the rename is one step on the same filesystem.
staging="$release_dir.unpacking"
rm -rf "$staging"
mkdir -p "$staging"
trap 'rm -rf "$staging"' EXIT
tar --extract --gzip --file="$archive" -C "$staging"
mv "$staging" "$release_dir"
trap - EXIT
# shellcheck disable=SC1091
. "$release_dir/RELEASE"
echo "→ release $MEYDAN_RELEASE unpacked to $release_dir"

compose() {
  IMAGE_TAG="$MEYDAN_RELEASE" MEYDAN_COMMIT="$MEYDAN_COMMIT" \
    docker compose --env-file "$env_file" -f "$release_dir/deploy/compose.yml" "$@"
}

if docker volume inspect meydan_pgdata >/dev/null 2>&1; then
  echo "→ safety backup before anything changes"
  sudo bash "$release_dir/deploy/scripts/backup.sh" >/dev/null
fi

echo "→ building images (tag $MEYDAN_RELEASE)"
compose build --quiet migrate web

echo "→ database (started if absent, never recreated by a deploy: its init mount path changes with every release)"
compose up -d --no-recreate db
for _ in $(seq 1 30); do
  compose exec -T db pg_isready -U postgres >/dev/null 2>&1 && break
  sleep 2
done

echo "→ migrating (explicit step; the API refuses to start on a schema behind its migrations)"
compose run --rm --no-deps migrate

echo "→ starting api + web"
compose up -d --no-deps api web

echo "→ waiting for /health/ready"
port="$(grep -E '^APP_HTTP_PORT=' "$env_file" | cut -d= -f2)"
port="${port:-8040}"
public_host="$(grep -E '^App__PublicUrl=' "$env_file" | cut -d= -f2 | sed -E 's#^https?://##; s#/.*$##')"
public_host="${public_host:-prizma.qrlog.az}"
healthy=""
for _ in $(seq 1 40); do
  # Through nginx on the loopback port, with the public host name: the API's host filtering (AllowedHosts)
  # accepts the site's name and the container names only, not 127.0.0.1.
  if curl -fsS --max-time 5 -H "Host: $public_host" "http://127.0.0.1:$port/health/ready" >/dev/null 2>&1; then healthy=1; break; fi
  sleep 3
done
if [ -z "$healthy" ]; then
  echo "NOT READY after two minutes. Logs:  docker logs meydan-api-1 --tail 100" >&2
  echo "Roll back with the previous release: $(readlink -f "$base/current" 2>/dev/null || echo '(none yet)')" >&2
  exit 1
fi

ln -sfn "$release_dir" "$base/current"
sed -i "s/^IMAGE_TAG=.*/IMAGE_TAG=$MEYDAN_RELEASE/" "$env_file"
# Keep the last five releases on disk (images are kept by tag for rollback; prune by hand when needed).
# The served /assets directory is a volume, not part of a release: the previous builds' hashed files stay there
# for ASSET_RETENTION_DAYS so tabs opened before this deploy can still load their chunks (frontend/nginx-assets.sh).
ls -1dt "$base"/releases/*/ | tail -n +6 | xargs -r rm -rf
echo "DONE — $MEYDAN_RELEASE is current; /health/ready answers"
