# Runtime diagnostics

The in-game overlay samples actual counters once per second. It displays the
hardware product model, Windows device family/version, original DXGI adapter
description and IDs, reported dedicated video memory, D3D feature level, texture
dimensions, Present rate, XAML update rate, cumulative mirror-stage timing,
application memory usage/limit, input counters and audio activation status.

The overlay is drawn by Nativra over the original game's frames. It is not a
mock screenshot or access to the game's private debug UI. NPC tasks, collision
volumes and engine internals cannot be inferred from these measurements.

## Interpretation

- Present and XAML rates are not physical display FPS or frame-pacing proof.
- DXGI memory fields are reported adapter properties, not an unlocked VRAM cap.
- The application memory limit is read separately from Windows MemoryManager.
- UWP gamepad enumeration, successful XInput readings and host key events describe
  different input paths. Zero successful readings does not establish that no
  input was delivered through another path.
- Audio activation is machine-observable; audible output requires an output
  capture or a person listening. The owner confirmed menu audio in build 194.
- GPU identity is captured before optional compatibility naming overrides.

## Unexplained app termination

The whole app has vanished mid-session — process gone, no `crash-log.txt`
entry (a caught unhandled exception, added build 377), no console crash dump
(a native access violation; check `/api/debug/dump/usermode/dumps` on the
Device Portal) — three separate times in one day (2026-09-27): once installing
an emulator from the shop, once running Brawlhalla under TRACE for over a
minute, once running LEGO Jurassic World after its guest thread went idle for
48 seconds. Each time the console fell back to displaying its own "Xbox
Network status" system screen, as if nothing else was in the foreground.

None of the three leaves a trace that points at Nativra's own code. The
pattern in common — an app that stops responding for somewhere around 60-90
seconds before it disappears — matches a platform watchdog killing an
unresponsive foreground app more than it matches a bug in any one of these
three unrelated code paths. Not confirmed (nothing in reach from inside the
app can query the Xbox shell's own watchdog policy), but worth knowing before
chasing a "crash" as a code bug: if a diagnostic run needs more than about a
minute, pull what you need well before that, and treat a run that goes idle
that long as expected to be killed, not a fault to explain in Nativra's own
handlers.

## Files written for remote reading

All in the package's `LocalState`, readable with `xbdev pull Nativra <file> LocalState`:

- `update-log.txt` — every over-the-air update step with its reason, one
  timestamped line each (from build 353).
- `portal-probe.txt` — whether the app reached the console's Device Portal.
- `recorder-note.txt` — the in-app recorder's state; recordings themselves go
  to `LocalState/recordings` (from build 355).
- `crash-log.txt` — every unhandled exception the app's own handler caught
  before the process ended, with type, HRESULT, message and stack trace (from
  build 377). A crash it did not catch (a native access violation) still
  leaves nothing here; check the console's own crash dumps instead
  (`/api/debug/dump/usermode/dumps` on the Device Portal).

## The Device Portal is out of the app's reach

Measured on September 26, 2026 (build 353): the app cannot connect to the
Device Portal of the console it runs on, not even for a GET
(`portal-probe.txt`: `reachable=False`, "a connection with the server could not
be established"). The portal listens on the console's own address, which UWP
network isolation treats as loopback. Every in-app update through the portal
had failed for this reason. Updates now go through `PackageManager` instead
(build 359), and anything else the app wants from the portal has to find
another route or run from a machine on the network.

## Private session evidence

```sh
XBDEV_HOST=192.168.68.132 bun tools/capture-session.ts 60 195
```

The duration is bounded to 2–300 seconds. The final argument is an operator's
installed-build label, not automatic binary verification. The collector reads
the existing Keychain credentials without printing or serializing them. It does
not launch, stop, install, authenticate Steam or modify the console.

Each private `.cycle/diagnostics/<timestamp>` directory contains system CPU,
memory, I/O, network and available GPU engine metrics at approximately two-second
intervals, interleaved with the app's existing pulse, plus a final screenshot,
native probe, pulse, Unity Player.log and system metadata. Individual unavailable
sources are recorded rather than treated as successful measurements. GPU engine
indexes are left as reported: no invented labels such as “3D” or “copy”. System
metrics include other console processes and must not be called game-only usage.

Process names and IDs are sampled every ten seconds using the Device Portal
[process-list API](https://learn.microsoft.com/en-us/windows/uwp/debug-test-perf/device-portal-api-core).
Usernames and other process fields are discarded. The final console-wide crash
index is stored separately from the app-filtered index; these are metadata, not
dump downloads. On a shared console, another title launching can interrupt a
test. A frozen pulse or return to Dev Home alone does not prove a Nativra crash.
Compare process samples and dump package identities before assigning a cause.

Directories use owner-only permissions and files use mode 0600. Logs and system
metadata can contain private information even though credentials are excluded.
Never publish these directories or raw dumps. Review and redact selected evidence
before sharing. Crash dumps remain a separate, targeted collection because they
can contain hundreds of megabytes of game and account memory.

The collector buffers samples in memory and writes on the Mac, not extra logs
to the console for each event. Avoid tracing hot imports such as TlsGetValue,
GetCurrentThreadId and locking primitives: instrumentation previously introduced
deadlocks and false performance regressions. Measure with and without additional
instrumentation before attributing a change to the compatibility layer.

API references: [DXGI adapter description](https://learn.microsoft.com/en-us/windows/win32/api/dxgi/ns-dxgi-dxgi_adapter_desc),
[application memory usage](https://learn.microsoft.com/en-us/uwp/api/windows.system.memorymanager.appmemoryusage),
[device-family version](https://learn.microsoft.com/en-us/uwp/api/windows.system.profile.analyticsversioninfo.devicefamilyversion).
