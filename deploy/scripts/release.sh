#!/usr/bin/env bash
# Packs a release for the VPS: every file git tracks or would track (untracked but not ignored) — no build output,
# no node_modules, no secrets — plus a RELEASE stamp. Run from anywhere inside the repository:
#   deploy/scripts/release.sh [out-dir]      → <out-dir>/meydan-<release-id>.tgz
# The release id is the commit (short hash), with "-wip-<time>" appended while the tree has uncommitted changes,
# so a server always knows exactly what it is running.
set -euo pipefail

root="$(git rev-parse --show-toplevel)"
out="${1:-$root/design-review/releases}"
cd "$root"

id="$(git rev-parse --short HEAD)"
if [ -n "$(git status --porcelain --untracked-files=normal)" ]; then
  id="$id-wip-$(date -u +%Y%m%dT%H%M%SZ)"
fi

mkdir -p "$out"
stamp="$(mktemp -d)"
printf 'MEYDAN_RELEASE=%s\nMEYDAN_COMMIT=%s\nMEYDAN_PACKED_AT=%s\n' "$id" "$(git rev-parse HEAD)" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" > "$stamp/RELEASE"

archive="$out/meydan-$id.tgz"
# --exclude-standard applies .gitignore; the extra filters keep an accidentally un-ignored artifact out too.
git ls-files -co --exclude-standard \
  | grep -vE '^(design-review/|frontend/design/|frontend/dist/|frontend/node_modules/|.*/bin/|.*/obj/|.*\.tsbuildinfo$)' \
  | tar --create --gzip --force-local --file="$archive" --files-from=- -C "$stamp" RELEASE
rm -rf "$stamp"
echo "$archive"
