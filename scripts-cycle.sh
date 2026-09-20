#!/bin/bash
# One turn of the loop: build on the runner, put it on the console, run the
# game, bring the report back. Everything here is measured, not assumed — the
# workflow is matched on the commit being built, because asking for the last
# run right after a push answers with the previous one.
set -euo pipefail
cd "$(dirname "$0")"

MSG="${1:-wip}"
WAIT="${2:-40}"

git add -A
git commit -q -m "$MSG" || true
git push -q
SHA="$(git rev-parse HEAD)"
echo "== build $SHA"

RUN=""
for _ in $(seq 1 40); do
  RUN="$(gh run list --workflow build-uwp --limit 5 \
        --json databaseId,headSha,status,conclusion \
        -q ".[] | select(.headSha==\"$SHA\") | \"\(.databaseId) \(.status) \(.conclusion)\"" | head -1)"
  [ -n "$RUN" ] && break
  sleep 5
done
[ -z "$RUN" ] && { echo "no run for $SHA"; exit 1; }
ID="${RUN%% *}"
gh run watch "$ID" --exit-status >/dev/null 2>&1 || { echo "BUILD FAILED $ID"; gh run view "$ID" --log-failed 2>&1 | tail -30; exit 1; }

rm -rf .cycle && mkdir -p .cycle
TAG="$(gh release list --limit 1 --json tagName -q '.[0].tagName')"
gh release download "$TAG" -D .cycle -p 'kiosk-uwp.zip' >/dev/null
( cd .cycle && unzip -qo kiosk-uwp.zip )
echo "== $TAG"

bun src/xbdev.ts uninstall kiosk >/dev/null 2>&1 || true
# Only the Kiosk tree: the solution also builds the JIT probe, and installing
# that instead is how the last attempt ended up with no app on the console.
FILES=$(find .cycle/Kiosk_*_Test -type f \
  \( -name '*.msixbundle' -o -name '*.appxbundle' -o -name '*.msix' \) \
  ! -path '*/arm64/*' ! -path '*/x86/*' | tr '\n' ' ')
DEPS=$(find .cycle/Kiosk_*_Test/Dependencies/x64 -type f -name '*.appx' | tr '\n' ' ')
bun src/xbdev.ts install $FILES $DEPS
bun src/xbdev.ts sync >/dev/null 2>&1 || true
bun src/xbdev.ts markers || true
bun src/xbdev.ts launch kiosk >/dev/null
echo "== running ${WAIT}s"
sleep "$WAIT"
bun src/xbdev.ts pull kiosk native-probe.txt LocalState || true
