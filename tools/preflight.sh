#!/bin/bash
# A cheap check for the one mistake that costs a whole build: two constants or
# two fields with the same name in the same file. The compiler catches it in
# five minutes on the runner; this catches it in a second, here.
set -u

# Every shell script in the project has to parse. A quote left open inside a
# nested substitution stops the whole file, and the failure arrives as a
# syntax error in a line that looks innocent — which cost a full cycle.
for script in scripts-*.sh tools/*.sh; do
  [ -f "$script" ] || continue
  bash -n "$script" || bad=1
done
bad=0
for file in uwp/Kiosk/Native/*.cs uwp/Kiosk/*.cs; do
  [ -f "$file" ] || continue
  repeated=$(grep -oE '^[[:space:]]*(private|public|internal|protected)[[:space:]]+(static[[:space:]]+)?(readonly[[:space:]]+)?(const[[:space:]]+)?[A-Za-z_<>,\[\]]+[[:space:]]+([A-Za-z_][A-Za-z0-9_]*)[[:space:]]*[=;]' "$file" \
    | sed -E 's/.*[[:space:]]([A-Za-z_][A-Za-z0-9_]*)[[:space:]]*[=;]$/\1/' \
    | sort | uniq -d)
  if [ -n "$repeated" ]; then
    echo "$file declares these twice:"
    echo "$repeated" | sed 's/^/  /'
    bad=1
  fi
done
exit $bad
