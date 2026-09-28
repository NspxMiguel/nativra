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
