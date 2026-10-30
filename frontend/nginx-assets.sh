#!/bin/sh
# Keeps the previous releases' hashed assets alive for a while.
#
# The problem this solves: a browser tab that was opened before a deploy is still running the old bundle. When the
# person then navigates to a route whose chunk has not been loaded yet, the tab asks for a file name from the old
# build. If the new image is all that is left on disk, that request 404s and the page breaks for no reason the
# person can understand.
#
# So /usr/share/nginx/html/assets is a named volume that outlives any one image, and each container adds its own
# build to it on start instead of replacing what is there:
#
#   * a file already in the volume is left exactly as it is (the names are content hashes, so the same name means
#     the same bytes — there is nothing to overwrite);
#   * every file this build owns is then touched, so the current release never looks old enough to be pruned,
#     however long it stays deployed;
#   * anything older than the retention window belongs to a build nobody is running any more, and goes.
#
# index.html is deliberately NOT in the volume: it lives in the image, so each release serves its own, and nginx
# sends it with no-cache while the hashed assets are immutable.
set -eu

# Overridable so the merge can be exercised outside a container.
staged="${ASSETS_STAGED:-/usr/share/nginx/assets-build}"
live="${ASSETS_LIVE:-/usr/share/nginx/html/assets}"
days="${ASSET_RETENTION_DAYS:-30}"

[ -d "$staged" ] || exit 0
mkdir -p "$live"

cd "$staged"
find . -type f | while IFS= read -r file; do
    name="${file#./}"
    target="$live/$name"
    if [ ! -f "$target" ]; then
        mkdir -p "$(dirname "$target")"
        cp "$staged/$name" "$target"
    fi
    # This build is the current one: its files must never age out from under it.
    touch "$target"
done

removed=0
if [ "$days" -gt 0 ]; then
    removed="$(find "$live" -type f -mtime "+$days" -print -delete | wc -l | tr -d ' ')"
    find "$live" -mindepth 1 -type d -empty -delete 2>/dev/null || true
fi

echo "meydan: assets published from this build; ${removed} file(s) older than ${days}d removed"
