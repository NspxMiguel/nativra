#!/bin/bash
# One session at a time on the console. Two apps brought to the front by two
# sessions suspend each other and every measurement taken then is wrong.
# usage: console-lock.sh acquire <owner> | release <owner> | show
LOCK=/tmp/xbox-console.lock
STALE=2700
now=$(date +%s)
case "$1" in
  acquire)
    if [ -f "$LOCK" ]; then
      read -r who since < "$LOCK"
      if [ "$who" != "$2" ] && [ $((now - ${since:-0})) -lt $STALE ]; then
        echo "console busy: $who since $(( (now - since) / 60 )) min"
        exit 4
      fi
    fi
    echo "$2 $now" > "$LOCK"
    ;;
  release)
    [ -f "$LOCK" ] && read -r who _ < "$LOCK" && [ "$who" = "$2" ] && rm -f "$LOCK"
    exit 0
    ;;
  *)
    cat "$LOCK" 2>/dev/null || echo free
    ;;
esac
