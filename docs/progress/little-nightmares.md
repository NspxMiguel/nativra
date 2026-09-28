# Little Nightmares (Steam 424840, 64-bit, Unreal Engine 4)

## Build 394: alive, never crashes, extremely slow startup, never reaches a frame

Downloads and installs fine (real Steam library, session live). Launches: real
window (`window=0x...`), the process genuinely runs (`exe.alive=True`, calls
counter climbing continuously, not frozen) — but it spends the whole observed
run (5+ minutes) inside engine start-up before ever pumping its first window
message (`exe.pumped=0` the entire time) or building a swap chain
(`mirror=not started chain=not built`, `frames=0`).

The call profile: `towupper` alone is >60% of every call made (6.2M of
9.8M at one sample), with `TryEnterCriticalSection`/`memcpy`/`iswspace` next —
classic case-insensitive string-table lookup traffic (UE4's config/asset
registry parsing does a lot of this). The growth rate decelerates over time
(≈7000 calls/s early, under 1000 calls/s by the 5-minute mark) rather than
staying flat — consistent with an O(n²)-shaped loop (a linear
case-insensitive search re-run against a table that keeps growing) rather
than a hang.

Not concluded either way tonight: this could be a legitimately slow but
real UE4 first-run cook step that finishes given enough wall-clock time, or
a real performance bug in the emulation's import-dispatch overhead compounding
across millions of small calls the game would run near-instantly on real
hardware. Worth a longer unattended run (20-30 minutes) next time, and/or
profiling whether the per-call dispatch overhead itself (not `towupper`'s own
logic, which is a single `char.ToUpperInvariant`) is what's actually slow.
