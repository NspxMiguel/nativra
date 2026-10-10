#!/usr/bin/env bash
# Run a command only while this session really holds the console lock.
set -e
owner=${LOCK_OWNER:-opus-x86}
lock=/tmp/xbox-console.lock
nested=false
if [ -f "$lock" ]; then
  read -r who since <"$lock"
  if [ "$who" = "$owner" ]; then
    age=$(($(date +%s) - ${since:-0}))
    if [ "$age" -ge 900 ]; then
      echo "console lease reached 15 minutes: not running" >&2
      exit 75
    fi
    nested=true
  fi
fi
if [ "$nested" = false ]; then
  bash "$(dirname "$0")/console-lock.sh" acquire "$owner" || {
    echo "console busy: not running" >&2
    exit 75
  }
  trap 'bash "$(dirname "$0")/console-lock.sh" release "$owner"' EXIT
fi
"$@"
