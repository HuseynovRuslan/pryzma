#!/usr/bin/env bash
# Deploys main from GitHub, pulled by the VPS itself: GitHub's runners cannot reach this host's SSH port (the network
# in front of it drops them), so the server asks instead. Cron runs it every two minutes as deploy:
#   */2 * * * * /home/deploy/bin/meydan-pull-deploy >> /home/deploy/.meydan-pull-deploy/log 2>&1
# A new commit on main goes live once the Deploy workflow's checks ("frontend", "backend") have passed on it. A commit
# whose checks or deploy failed is left alone until the next push; QRLog's clock-in peaks are waited out. The copy cron
# runs lives outside any release, so a push cannot change these rules for itself:
#   install -m 755 deploy/scripts/pull-deploy.sh ~deploy/bin/meydan-pull-deploy
set -euo pipefail

repo=HuseynovRuslan/pryzma
state="$HOME/.meydan-pull-deploy"
mkdir -p "$state"
exec 9>"$state/lock"
flock -n 9 || exit 0
say() { echo "$(date '+%F %T') $*"; }

sha="$(git ls-remote "https://github.com/$repo.git" refs/heads/main 2>/dev/null | cut -f1)" || exit 0
[[ "$sha" =~ ^[0-9a-f]{40}$ ]] || exit 0
for done in deployed refused; do
  [ "$sha" = "$(cat "$state/$done" 2>/dev/null)" ] && exit 0
done

# What GitHub's checks said about this very commit.
checks="$(curl -fsS -H 'Accept: application/vnd.github+json' \
  "https://api.github.com/repos/$repo/commits/$sha/check-runs?per_page=50" 2>/dev/null)" || exit 0
verdict="$(jq -r '[.check_runs[] | select(.name == "frontend" or .name == "backend")] as $r
  | if ($r | length) < 2 or any($r[]; .status != "completed") then "pending"
    elif all($r[]; .conclusion == "success") then "passed" else "failed" end' <<<"$checks")"
case "$verdict" in
  pending) exit 0 ;;
  failed)
    say "${sha:0:7}: checks failed on GitHub, not deployed"
    echo "$sha" > "$state/refused"
    exit 0 ;;
esac

# QRLog shares this host; about 114 people clock in at these hours (server clock, Asia/Baku). Wait them out.
now=$((10#$(date +%H%M)))
if { [ "$now" -ge 730 ] && [ "$now" -lt 930 ]; } || { [ "$now" -ge 1700 ] && [ "$now" -lt 1900 ]; }; then
  exit 0
fi

short="${sha:0:7}"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
curl -fsSL "https://codeload.github.com/$repo/tar.gz/$sha" -o "$work/source.tgz"
mkdir "$work/release"
tar -xzf "$work/source.tgz" -C "$work/release" --strip-components=1
# What release.sh leaves out of a release.
rm -rf "$work/release/design-review" "$work/release/frontend/design"
printf 'MEYDAN_RELEASE=%s\nMEYDAN_COMMIT=%s\nMEYDAN_PACKED_AT=%s\n' \
  "$short" "$sha" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" > "$work/release/RELEASE"
archive="/tmp/meydan-$short.tgz"
tar -czf "$archive" -C "$work/release" .

say "$short: checks passed, deploying"
if bash /opt/meydan/current/deploy/scripts/deploy.sh "$archive"; then
  echo "$sha" > "$state/deployed"
  say "$short: live"
else
  echo "$sha" > "$state/refused"
  say "$short: deploy.sh failed; the previous containers may be half-replaced, see deploy/README.md (rollback)"
fi
