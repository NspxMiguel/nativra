#!/bin/bash
# One turn of the loop: build on the runner, put it on the console, run the
# game, bring the report back. Everything here is measured, not assumed — the
# workflow is matched on the commit being built, because asking for the last
# run right after a push answers with the previous one.
set -euo pipefail
# The whole body is one block: bash parses it before running any of it, so
# editing this file while a cycle is in flight cannot change the running one.
{
cd "$(dirname "$0")"

MSG="${1:-wip}"
WAIT="${2:-30}"   # turns of 20 seconds

# Catch the mistakes that are cheap here and expensive on the runner.
if ! ./tools/xaml-check.sh || ! ./tools/resource-check.sh || ! ./tools/preflight.sh; then
  echo "preflight refused the tree; nothing was pushed"
  exit 1
fi

git add -A
git commit -q -m "$MSG" || true
git push -q
# The build belongs to the last commit that touched what the workflow builds;
# a commit to scripts or notes alone starts no run.
SHA="$(git log -1 --format=%H -- uwp .github/workflows/build-uwp.yml)"
echo "== build $SHA"

RUN=""
for _ in $(seq 1 40); do
  RUN="$(gh run list --workflow build-uwp.yml --limit 5 \
        --json databaseId,headSha,status,conclusion \
        -q ".[] | select(.headSha==\"$SHA\") | \"\(.databaseId) \(.status) \(.conclusion)\"" | head -1)"
  [ -n "$RUN" ] && break
  sleep 5
done
# A push that changes nothing starts no workflow, and a push can simply be
# beaten to the question. Either way the answer is to ask for the build
# rather than to give up on the turn — waiting for a run that will never
# exist is how a cycle burns a quarter of an hour doing nothing.
if [ -z "$RUN" ]; then
  echo "   no build for this commit yet; asking for one"
  gh workflow run build-uwp.yml >/dev/null 2>&1 || true
  for _ in $(seq 1 24); do
    sleep 5
    RUN="$(gh run list --workflow build-uwp.yml --limit 5 \
          --json databaseId,headSha,status,conclusion \
          -q ".[] | select(.headSha==\"$SHA\") | \"\(.databaseId) \(.status) \(.conclusion)\"" | head -1)"
    [ -n "$RUN" ] && break
  done
fi
[ -z "$RUN" ] && { echo "no run for $SHA"; exit 1; }
ID="${RUN%% *}"
gh run watch "$ID" --exit-status >/dev/null 2>&1 || { echo "BUILD FAILED $ID"; gh run view "$ID" --log-failed 2>&1 | tail -30; exit 1; }

rm -rf .cycle && mkdir -p .cycle
TAG="$(gh release list --limit 1 --json tagName -q '.[0].tagName')"
gh release download "$TAG" -D .cycle -p 'kiosk-uwp.zip' >/dev/null
( cd .cycle && unzip -qo kiosk-uwp.zip )
echo "== $TAG"

# Only the Kiosk tree: the solution also builds the JIT probe, and installing
# that instead is how the last attempt ended up with no app on the console.
FILES=$(find .cycle/Kiosk_*_Test -type f \
  \( -name '*.msixbundle' -o -name '*.appxbundle' -o -name '*.msix' \) \
  ! -path '*/arm64/*' ! -path '*/x86/*' | tr '\n' ' ')
DEPS=$(find .cycle/Kiosk_*_Test/Dependencies/x64 -type f -name '*.appx' | tr '\n' ' ')

# Installing over the app in place keeps its storage, and its storage is where
# the downloaded game lives. Uninstalling first threw the game away every turn
# and the console spent most of each turn fetching it again from Steam — which
# is most of the time a turn takes. FRESH=on forces the old behaviour when the
# state itself is what is suspect.
# The cycle closes the app when it is done, so an app still open means someone
# is using the console. Replacing it under them froze a game and threw away
# their Steam sign-in. FORCE=on overrides.
if [ "${FORCE:-off}" != "on" ] && bun src/xbdev.ts running kiosk >/dev/null 2>&1; then
  echo "Nativra is open on the console; not replacing it (FORCE=on to override)"
  exit 3
fi
# Steam rotates the sign-in on the console, so the copy kept here goes stale.
# Take the live one before the uninstall wipes it; the sync puts it back.
if bun src/xbdev.ts pull kiosk steam.json LocalState >/dev/null 2>&1 \
  && python3 -c 'import json,sys; d=json.load(open("steam.json")); sys.exit(0 if d else 1)' 2>/dev/null; then
  echo "   steam session taken from the console"
fi
bun src/xbdev.ts stop kiosk >/dev/null 2>&1 || true
# Measured, and it cost the app: installing over the top reports success and
# leaves nothing registered — the console lists no package afterwards and the
# launch fails. So replacing is the default again, and the faster path has to
# earn its way back with evidence rather than hope. KEEP=on tries it.
# Builds now carry a rising version, so installing is an update that keeps
# the storage (game and Steam sign-in). An install that reports success but
# leaves no package registered, or that is refused, falls back to replacing.
# FRESH=on replaces on purpose.
if [ "${FRESH:-off}" = "on" ]; then
  bun src/xbdev.ts uninstall kiosk >/dev/null 2>&1 || true
fi
if ! bun src/xbdev.ts install $FILES $DEPS || ! bun src/xbdev.ts apps 2>/dev/null | grep -q Nativra; then
  echo "   update refused or left nothing registered; replacing the package"
  bun src/xbdev.ts uninstall kiosk >/dev/null 2>&1 || true
  bun src/xbdev.ts install $FILES $DEPS
fi
# A freshly installed package has no local storage until it has run once, and
# every push into it fails until then — silently, if the output is thrown away.
# Reports from an earlier run look exactly like this one's; clear both copies
# so a stale file can never pass for a result.
rm -f native-probe.txt native-pulse.txt Player.log
bun src/xbdev.ts rm kiosk native-probe.txt native-pulse.txt --dir LocalState >/dev/null 2>&1 || true
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
# TRACE=on turns on the per-call recording, which costs a managed transition
# on every function the engine uses — fine while debugging, not while timing.
if [ "${TRACE:-off}" = "on" ]; then
  bun src/xbdev.ts push kiosk .markers/trace.txt LocalState
fi
# AUDIO=off refuses the audio class outright, which is how the picture gets
# looked at without the sound getting in the way of it.
if [ "${AUDIO:-on}" = "off" ]; then
  bun src/xbdev.ts push kiosk .markers/noaudio.txt LocalState
fi
# SMALL=on renders at 1280x720, which halves what every copied frame costs.
if [ "${SMALL:-off}" = "on" ]; then
  bun src/xbdev.ts push kiosk .markers/small.txt LocalState
fi
# PLAY=off loads everything but never starts the engine.
if [ "${PLAY:-on}" = "off" ]; then
  bun src/xbdev.ts push kiosk .markers/noplay.txt LocalState
fi
# CHAIN=off refuses the game a swap chain, to see who owns the screen.
# STEAM=on answers the game's Steamworks calls with the signed-in account.
if [ "${STEAM:-off}" = "on" ]; then
  bun src/xbdev.ts push kiosk .markers/steambridge.txt LocalState
fi
# DIRECT=on hands the game a real chain on a native panel instead of the mirror.
if [ "${DIRECT:-off}" = "on" ]; then
  bun src/xbdev.ts push kiosk .markers/direct.txt LocalState
fi
if [ "${CHAIN:-on}" = "off" ]; then
  bun src/xbdev.ts push kiosk .markers/nochain.txt LocalState
fi
# PEB=off leaves the process-wide image base alone.
if [ "${PEB:-on}" = "off" ]; then
  bun src/xbdev.ts push kiosk .markers/nopeb.txt LocalState
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
# The engine ignores -logFile and writes where it always writes: its own
# folder under the container. That log says in one line what days of call
# tracing only hinted at, so it is fetched every turn.
# The folder is named after the game, and this game's name has an apostrophe
# in it. Held in a variable rather than written inline: nested inside a command
# substitution that quote opened a string nobody closed, and the whole script
# stopped parsing.
GAME_NAME=${GAME:-}
if [ -z "$GAME_NAME" ]; then GAME_NAME=$(printf 'Seraph%ss Last Stand' "'"); fi
for MAKER in OddGiant "LEGO" .; do
  if bun src/xbdev.ts pull kiosk Player.log "AC/$MAKER/$GAME_NAME" >/dev/null 2>&1; then
    echo "== the game's own log =="
    tail -40 Player.log
    break
  fi
done
echo "== pulse =="
cat native-pulse.txt 2>/dev/null | head -40
# Leave the console free: an open app is how the next cycle knows he is on it.
bun src/xbdev.ts stop kiosk >/dev/null 2>&1 || true
# Measuring switches must not outlive the measurement: a trace left behind
# slows every game he opens afterwards.
bun src/xbdev.ts rm kiosk trace.txt direct.txt steambridge.txt --dir LocalState >/dev/null 2>&1 || true
exit
}
