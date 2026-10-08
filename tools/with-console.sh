#!/usr/bin/env bash
# Run a command only while this session really holds the console lock; abort without touching the console otherwise.
set -e
bash "$(dirname "$0")/console-lock.sh" acquire "${LOCK_OWNER:-opus-x86}" || { echo "console busy: not running" >&2; exit 75; }
trap 'bash "$(dirname "$0")/console-lock.sh" release "${LOCK_OWNER:-opus-x86}"' EXIT
"$@"
