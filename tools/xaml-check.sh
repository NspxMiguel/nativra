#!/bin/bash
# XAML is XML, and a tag left behind by an edit is a five-minute build failure
# on the runner and a one-second one here. This catches exactly that.
set -u
bad=0
for file in uwp/Kiosk/*.xaml; do
  python3 - "$file" <<'PY' || bad=1
import sys, xml.etree.ElementTree as ET
try:
    ET.parse(sys.argv[1])
except ET.ParseError as error:
    print(sys.argv[1] + ": " + str(error))
    sys.exit(1)
PY
done
exit $bad
