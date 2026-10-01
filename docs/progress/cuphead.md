# Cuphead (Steam 268910, 64-bit)

First real test, build 391, after the Steam session was restored (see the
shelf-names and B-button fixes from the same night).

## Result: window opens, never reaches a first frame

Downloaded (1.0 GB) and installed to console storage through the in-app Shop
flow — that path itself works end to end, including the mid-download shelf
tile with a live percentage. Launching it:

- Creates a real window (`window=0x...` in `native-probe.txt`) and pumps a
  normal Win32 message loop (`TranslateMessage`/`DefWindowProcW` cycling in
  `native-pulse.txt`'s recent-calls ring — not spinning on anything unusual,
  not calling anything missing).
- Never gets past that: `native-pulse.txt` stays at `mirror=not started
  chain=not built` and `frames=0 at 0.0 a second work=none` for the whole
  observed run (several minutes). No exception, no crash-log entry, no named
  stop — it just never calls whatever creates the swap chain FrameMirror hooks.
- Confirmed twice, including once from a verified cold process (`xbdev stop`
  until `xbdev running Kiosk` actually reports `stopped`, then `launch`) — not
  a stale-process artefact from an earlier build.

This is a 64-bit Unity/D3D11 game, not a 32-bit one — a different code path
from WAVESHAPER/LEGO's x86 layer entirely. Worth checking first on the next
round: whether Cuphead requests a fullscreen-exclusive swap chain (Unity's
"Exclusive Fullscreen" mode) rather than the windowed/composition swap chain
GraphicsBridge's `FakeSwapChain` expects — that would explain a window and a
message loop existing with no swap chain ever reaching `Show()`.

## Update (30/09): GraphicsBridge's own trail now reaches the pulse

Static review (no console that session) found `GraphicsBridge.Notes` — a
60-line trail already written at 46 call sites across factory creation,
adapter/output enumeration, device creation and swap-chain creation — but it
was never surfaced into `native-pulse.txt`, only `AudioBridge.Notes` was
(commit 5352237 fixes that, same pattern, `"graphics " + note` lines).
Also confirmed by reading `FakeSwapChain.cs`/`GraphicsBridge.cs` directly:
`SetFullscreenState` is already stubbed to return `S_OK` harmlessly, and
`IDXGIFactory` through v7 plus `EnumOutputs`/a fake-output fallback are
implemented — so a hard QueryInterface/EnumOutputs gap looks less likely
than the fullscreen-exclusive theory above. Next Cuphead run on a build with
5352237+ will show the real `graphics ...` call trail directly instead of
needing another guess.

## Unrelated oddity, not chased down

While diagnosing this, `native-watch.txt`'s thread sampler kept showing
`game`-tagged threads symbolized into `Adobe AIR.dll`/`Brawlhalla.exe` — on a
freshly confirmed-cold process, before any game was even launched. Checked
with the other session running scripts against this console (NXbox/Eden): not
them, their scripts wait on the lock and hadn't touched it. Either a genuine
zombie process surviving `portal.terminate()`, or a stale symbol-cache
artefact in the stack sampler unrelated to what is actually executing. Not
resolved tonight — flagging so the next session does not mistake it for a
real Brawlhalla process fighting for the console.
