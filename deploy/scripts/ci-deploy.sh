#!/usr/bin/env bash
# The only thing the GitHub Actions key may do on the VPS (forced command in ~deploy/.ssh/authorized_keys):
#   ssh deploy@<vps> "deploy <commit>" < meydan-<commit>.tgz
# It takes one release from stdin and runs deploy.sh on it, streaming its output. A copy lives in ~deploy/bin, outside
# any release, so a push cannot change these rules for itself:
#   install -m 755 deploy/scripts/ci-deploy.sh ~deploy/bin/meydan-ci-deploy
set -euo pipefail

request="${SSH_ORIGINAL_COMMAND:-}"
id="${request#deploy }"
if [[ "$request" != deploy\ * || ! "$id" =~ ^[0-9a-f]{7,40}$ ]]; then
  echo "refused: this key only runs 'deploy <commit>'" >&2
  exit 2
fi

# QRLog shares this host; about 114 people clock in at these hours and their pay depends on the record (server clock,
# Asia/Baku). A push in the window fails here and can simply be re-run afterwards.
now=$((10#$(date +%H%M)))
if { [ "$now" -ge 730 ] && [ "$now" -lt 930 ]; } || { [ "$now" -ge 1700 ] && [ "$now" -lt 1900 ]; }; then
  echo "refused: QRLog clock-in peak ($(date +%H:%M)); re-run the deploy after 09:30 or 19:00" >&2
  exit 3
fi

exec 9>/tmp/meydan-ci-deploy.lock
flock -n 9 || { echo "refused: another deploy is running" >&2; exit 4; }

archive="/tmp/meydan-$id.tgz"
head -c 200000000 > "$archive"
gzip -t "$archive" || { echo "refused: the upload is not a complete release archive" >&2; rm -f "$archive"; exit 5; }

# Detached from this connection, so a dropped CI connection cannot cut a deploy halfway; the log is followed here.
log="/tmp/meydan-$id-ci-deploy.log"
status="/tmp/meydan-$id-ci-deploy.status"
rm -f "$status"
: > "$log"
setsid sh -c "/opt/meydan/current/deploy/scripts/deploy.sh '$archive' > '$log' 2>&1; echo \$? > '$status'" < /dev/null &
pid=$!
tail -n +1 -f --pid="$pid" "$log" 2>/dev/null || true
wait "$pid" 2>/dev/null || true
for _ in $(seq 1 600); do [ -s "$status" ] && break; sleep 1; done
code="$(cat "$status" 2>/dev/null || echo 1)"
echo "deploy.sh exit $code"
exit "$code"
