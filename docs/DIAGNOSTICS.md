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
