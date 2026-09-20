#!/bin/bash
# One turn of the loop: build on the runner, put it on the console, run the
# game, bring the report back. Everything here is measured, not assumed — the
# workflow is matched on the commit being built, because asking for the last
# run right after a push answers with the previous one.
set -euo pipefail
cd "$(dirname "$0")"

MSG="${1:-wip}"
WAIT="${2:-30}"   # turns of 20 seconds

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
# A freshly installed package has no local storage until it has run once, and
# every push into it fails until then — silently, if the output is thrown away.
bun src/xbdev.ts launch kiosk >/dev/null
sleep 12
bun src/xbdev.ts stop kiosk >/dev/null 2>&1 || true

bun src/xbdev.ts sync >/dev/null 2>&1 || true

# The app cannot read the developer share (UnauthorizedAccessException on every
# drive letter, measured), and its own storage goes with the uninstall. So the
# game is fetched again each turn, by the console, from his own Steam account.
# Which game this turn tests. The app downloads it on the console itself, so
# trying another one is a number, not a new build.
printf '%s\n' "${APPID:-1919460}" > .markers/autodownload.txt
bun src/xbdev.ts push kiosk .markers/autodownload.txt LocalState
# TRACE=off turns off the per-call recording, which costs a managed transition
# on every function the engine uses — fine while measuring, not while timing.
if [ "${TRACE:-on}" = "off" ]; then
  bun src/xbdev.ts push kiosk .markers/notrace.txt LocalState
fi
bun src/xbdev.ts launch kiosk >/dev/null

echo "== downloading and running"
for _ in $(seq 1 "$WAIT"); do
  sleep 20
  if bun src/xbdev.ts pull kiosk native-probe.txt LocalState >/dev/null 2>&1; then
    if grep -q "exe\.\|FAILED\|probe failed" native-probe.txt; then break; fi
    echo "   $(tail -1 native-probe.txt)"
  fi
done
bun src/xbdev.ts pull kiosk native-probe.txt LocalState >/dev/null 2>&1 || true
bun src/xbdev.ts pull kiosk native-pulse.txt LocalState >/dev/null 2>&1 || true
bun src/xbdev.ts shot /tmp/xbox-cycle.png >/dev/null 2>&1 || true
if bun src/xbdev.ts pull kiosk unity.log LocalState >/dev/null 2>&1; then
  echo "== unity =="
  head -60 unity.log
fi
echo "== pulse =="
cat native-pulse.txt 2>/dev/null | head -40
