#!/bin/bash
# Opens the project to contributors. Run only once the game runs, and only
# after he has said the word: making a repository public publishes it, and
# that decision is his, not this script's.
set -euo pipefail
cd "$(dirname "$0")"
REPO="NspxMiguel/xboxdev"

if [ "${1:-}" != "--yes" ]; then
  echo "This makes $REPO public and opens issues. Re-run with --yes."
  exit 1
fi

gh repo edit "$REPO" --visibility public --accept-visibility-change-consequences
gh repo edit "$REPO" \
  --description "An Xbox Series X|S running PC games natively, in developer mode" \
  --add-topic xbox --add-topic uwp --add-topic steam --add-topic pc-gaming

label() { gh label create "$1" --repo "$REPO" --color "$2" --description "$3" --force; }
label "good first issue" "7057ff" "Self-contained, and no console needed"
label "needs a console"  "d93f0b" "Requires an Xbox in developer mode"
label "hard"             "b60205" "A subsystem, left open on purpose"
label "translation"      "0e8a16" "Adding or fixing a language"

open() { gh issue create --repo "$REPO" --title "$1" --label "$2" --body "$3"; }

open "Translate the interface into Spanish" "good first issue,translation" \
"Every string the player sees is a key in \`uwp/Kiosk/Texts.cs\` and \`src/i18n.ts\`.
Adding a language is adding a table; nothing else changes.

Done when every key has a Spanish value and nothing falls back to English."

open "Write the winmm functions a game asks for" "good first issue" \
"A game imports 23 functions from \`WINMM\`. The timing ones are written; the
rest are answered with constants. Some of them deserve real implementations.

Start from the report a run produces: it names every function reached that
nobody had written, in the order it was needed.

Done when a game that imports winmm has no recording stub left in its report."

open "Write the imm32 functions" "good first issue" \
"Eight input-method functions. On a console with no IME, every one of them
means \"no composition is in progress\" — but saying that correctly matters,
because a caller told nothing keeps asking.

Done when \`ImmGetContext\` and its seven neighbours answer per the documentation."

open "Report a game: does it run?" "needs a console" \
"The single most useful thing anyone can do, and it needs no code.

Run any Steam game you own through the app, attach \`native-probe.txt\` and
\`native-pulse.txt\`, and say what happened. Each report makes the compatibility
list longer, which is the point of the project."

open "Repackage gzdoom, openbor and raze with a URI protocol" "needs a console" \
"Six of the emulators open straight from the app because their packages
register a protocol. Three do not, and land on Dev Home instead.

Done when all nine open from inside the app."

open "Audio: the endpoint a packaged app cannot create" "hard" \
"A game finds its speakers through \`CoCreateInstance(MMDeviceEnumerator)\`,
which a packaged app is not allowed to create. The console has
\`ActivateAudioInterfaceAsync\` instead, and a real audio engine behind it.

There is a first attempt in \`uwp/Kiosk/Native/AudioBridge.cs\`: the discovery
is built by hand and the client underneath is the platform's own. What is
missing is the property store a game reads the device format from, and every
game that asks for something else.

Done when a game plays sound."

open "Cydia-style sources" "hard" \
"A repository format anyone can host, a reader in the app, and installing
straight onto the console from one.

Note the constraint measured on this console: the app cannot reach its own
Device Portal, so installing cannot go through it."

open "A site for compatibility reports" "hard" \
"Shaped like ProtonDB: per-game reports, per-console badges, fed by what people
attach to the reporting issue.

Done when the app can show a badge on a game that someone else tested."
