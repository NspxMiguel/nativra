#!/bin/bash
# A StaticResource that does not exist does not degrade: it throws while the
# page is loading, and the screen simply never opens. That is how the store
# stopped working, and it is a one-second check here.
cd "$(dirname "$0")/.."
python3 - <<'PY'
import re, glob, sys
keys = set(re.findall(r'x:Key="([^"]+)"', open("uwp/Kiosk/Tokens.xaml").read()))
bad = False
for page in glob.glob("uwp/Kiosk/*.xaml"):
    if page.endswith("Tokens.xaml"): continue
    text = open(page).read()
    local = set(re.findall(r'x:Key="([^"]+)"', text))
    missing = sorted(set(re.findall(r'\{StaticResource ([A-Za-z0-9_]+)\}', text)) - keys - local)
    if missing:
        print(page + " wants resources nothing defines: " + ", ".join(missing))
        bad = True
sys.exit(1 if bad else 0)
PY
