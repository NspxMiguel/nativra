# Hades: new deadlock tonight (build 391-394), reproducible

Previously reliable (build 382: reached menu at ~60fps, no issues). Tonight,
twice in a row, it gets well into a real run — window created, exe.pumped
climbs past 11,000-12,000 messages, exe.calls past 750,000 — then goes
completely flat (identical exe.calls across a 60+ second re-check) or the
whole Nativra process silently drops to the Xbox Home screen (no
crash-log.txt entry, so not a caught .NET exception).

`native-watch.txt`'s stack sample while stuck: the `main` thread (tid 4200
in one sample) is inside `windows.storage.onecore.dll` / `combase.dll` /
`RPCRT4.dll` frames, called from `~Kiosk.dll+0x737436` (host code) via
`~SharedLibrary.dll+0x7E370B`, itself reached from `EngineWin64s.dll` / SDL2
frames. This is the same *shape* of bug LEGO's investigation found in
`UsbFiles.Folder()` (a synchronous wait on a WinRT Storage async call that
can hang) — but not that specific method: grepped `CrtFiles.cs` and
`UserFolders.cs` (the other WinRT-storage-touching files) for the same
`GetAwaiter().GetResult()`/`.AsTask().Wait()` pattern and found none; every
call there is already properly awaited.

Not resolved tonight — need to actually symbolicate `Kiosk.dll+0x737436` (or
add a marker log right before whatever storage call this is) to find the
real call site. Worth checking first: does this coincide with a Steamworks
cloud-save or achievements file access (Hades uses Steam Cloud), since that
path is less exercised than plain local file access and was flagged in
earlier project history as still using the remote-storage stub answering
"honestly empty" rather than doing real I/O — if something downstream of
that stub still tries a real WinRT file open, that would explain a hang
appearing only once actual gameplay-adjacent code runs, not at boot.

No code change tonight is implicated: `git diff --stat` between the commit
before PR #4's merge and the merge itself touches only
`uwp/Kiosk/Native/X86*.cs` and `Kiosk.csproj` (32-bit-only launch glue) —
nothing Hades' 64-bit path goes through. My own SEH-filter change is
similarly confined to `x86/Nativra.X86/Loader/`. This looks like a
pre-existing intermittent bug this session happened to hit twice in a row,
not a regression from tonight's work — but that is an inference, not
verified by a clean run on an older build tonight.

## Confirmed deterministic (3 runs)

Reproduced two more times: `exe.calls=754089`/`exe.pumped=11373`,
`exe.calls=755227`/`exe.pumped=12240`, `exe.calls=755407`/`exe.pumped=12235`
— same stall point within noise, every time, on a genuinely cold process
each run. This is not resource pressure or a slow console tonight: it is a
deterministic deadlock at a specific point in Hades' own startup sequence,
right around 755,000 host calls and 12,000 pumped window messages.

**This is the single best lead of the night to hand to a fresh session.**
The stack (see above) has the main thread inside `windows.storage.onecore.dll`
via `combase.dll`/`RPCRT4.dll`, called from `~Kiosk.dll+0x737436`, reached
through a chain that repeatedly crosses `SDL2.dll` and `EngineWin64s.dll`.
Being 100% reproducible at the same call count makes this tractable: the
next session should add a call-count-based breakpoint-equivalent (log the
last N host function names once `exe.calls` crosses ~750,000, the same
"recent calls" ring already used elsewhere in the codebase) rather than
guess, since the exact same stopping point every run means the same log
line will appear at the same call count next time too.

## Fixed, verified: the FileWatch.Attributes deadlock is real and gone

Root cause found by loading `Kiosk.pdb` (from the CI build's `.appxsym`
artifact) into radare2 and resolving the crash stack's raw addresses
directly to source: both non-generic frames landed in
`Kiosk.Native.FileWatch.Attributes` / `GetFileAttributesExFromAppW`.
`GetFileAttributesExFromAppW` is a direct P/Invoke into the app-container
storage broker with no timeout — the same class of bug as `UsbFiles.Folder`
before it. Fixed the same way (build 395, commit fc3fa7b): run it on its own
task, 2s bound, timeout answers "not found" instead of blocking forever.

Verified on console: the exact `exe.calls≈755000`/`exe.pumped≈12000` point
that killed every previous run went past cleanly — `exe.calls` reached
755,934 and kept climbing (+395,606 in one interval, not flat). The original
deadlock is confirmed fixed.

**New finding: it still doesn't reach a frame.** Shortly after passing that
point, the whole Nativra process dropped to the Xbox Home screen again — no
crash-log.txt entry (not a caught .NET exception), and native-probe.txt
stopped updating at the same 755,934/12,969 reading (the probe writer died
with the process, so that reading is the last one before whatever happened,
not necessarily the exact failure point — could be moments later). This is
a different, not-yet-diagnosed problem, likely the same class of bug
(another unbounded broker call somewhere else in the startup path) given how
cleanly the first one was found. Next session: reproduce again, and if it
recurs at a consistent call count like the first one did, the same
PDB-address-resolution technique above will find it directly — that
workflow is now proven and fast to repeat:
1. `gh release download kiosk-build-<N> -D <dir>` (grabs `kiosk-uwp.zip`, which
   contains `Kiosk_<ver>_Test/Kiosk_<ver>_x64.appxsym`)
2. Unzip the `.appxsym` (it's a zip containing `Kiosk.pdb`)
3. Extract `Kiosk.dll` from the matching `.msixbundle`
4. `brew install radare2`
5. Pull `native-watch.txt` from the console while it's hung, take the
   `Kiosk.dll+0x...` offsets, add the image base (`rabin2 -H Kiosk.dll` shows
   it, e.g. `0x180000000`), and resolve with
   `r2 -q -c "idp Kiosk.pdb; fd <base+offset>" Kiosk.dll`

## Neighbours checked: the same bug is probably widespread

Grepped for every other `*FromAppW` broker P/Invoke (the family
`GetFileAttributesExFromAppW` belongs to) across `Native/`:
`CreateFileFromAppW` and `CreateDirectoryFromAppW` in `CrtFiles.cs`;
`FindFirstFileExFromAppW`, `MoveFileFromAppW`, `DeleteFileFromAppW`,
`CopyFileFromAppW`, `CreateFileFromAppW` (again, a second copy) in
`FileWatch.cs`; `CreateFileFromAppW`, `FindFirstFileExFromAppW`,
`CreateDirectoryFromAppW`, `DeleteFileFromAppW`, `RemoveDirectoryFromAppW`
in `X86Files.cs` — roughly 15 call sites total, every one a direct,
unbounded P/Invoke into the same app-container storage broker that just
hung on `GetFileAttributesExFromAppW`.

**Not fixed tonight, deliberately.** `CreateFileFromAppW` specifically is
the single hottest call in the file layer (every file open) — wrapping it
in the same `Task.Run` + 2s-wait pattern unconditionally would add real
per-call overhead to the common, already-working case, and that trade-off
needs an actual timing comparison on console, not a guess made to close out
a context-constrained session. The narrower, safer version of this fix is
probably: only the ones that are not already latency-sensitive (directory
creation, delete, move, rename — not the per-file open path) can get the
bounded-wait treatment cheaply; `CreateFileFromAppW` itself may need a
different approach (a shorter bound, or accepting that a hang there is
rarer because it is usually opening a file that is already known to exist).
Flagged as the clear next step, not attempted blind.
