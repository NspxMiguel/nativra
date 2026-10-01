# WAVESHAPER (Steam 562260, 32-bit)

## Status after merging PR #4 (kernel32 rest, msvcrt, gdi32, user32, winmm,
## advapi32 — commit 7569270, build 391)

Expected this to move the stop from `kernel32.dll!InterlockedCompareExchange`
(build 348's original finding) forward — the PR's own description names the
Interlocked family as the first thing it adds. It did not move at all:

```
x86.imports.kernel32.dll=212 (169 served)
x86.imports.msvcrt.dll=85 (0 served)
x86.imports.gdi32.dll=29 (2 served)
x86.imports.user32.dll=68 (53 served)
x86.init=missing import kernel32.dll!InterlockedCompareExchange
```

Identical numbers, on both builds — confirmed against build 390 (pre-merge)
and build 391 (post-merge, confirmed installed via `xbdev apps` showing
`1.0.391.0`, confirmed a genuinely cold process via `xbdev stop` until
`xbdev running Kiosk` actually reported `stopped`, then `launch`).

## Ruled out

- **Build not really updated**: checked `xbdev apps` (1.0.391.0) and
  extracted `Kiosk.dll` from the installed `.msixbundle` directly — the
  string `InterlockedCompareExchange` is present in the binary (UTF-16LE,
  `strings` on macOS needs `-e l` which this system's BSD strings does not
  have; grep for the UTF-16 bytes in Python instead). The new code is in the
  shipped package.
- **Stale process**: confirmed via `xbdev stop` + polling `xbdev running`
  until it actually flips to `stopped`, not just calling `launch` again on an
  app that was still resident.
- **`InstallKernel32` not wired up**: it is — `GuestKernel.cs`'s `Install()`
  calls it unconditionally (`InstallRuntime`, `InstallFiles`, `InstallSeh`,
  `InstallThreads`, `InstallUser32`, `InstallKernel32`, `InstallMsvcrt`,
  `InstallGdi`, `InstallUser32More`, `InstallLibraries`, in that order, no
  guard).
- **Registration-after-binding bug**: `GuestImports.Register()`'s own doc
  comment says order does not matter, and reading it, that holds — it updates
  `handlers[key]` and, if a binding already exists in `byKey`, pushes the new
  handler onto that existing `GuestImport` live.
- **Duplicate `GuestImport` per key (two images importing the same
  symbol each getting their own, only one of which gets fixed up)**: also
  ruled out by reading `Bind()` — it checks `byKey` first and returns the
  existing sentinel rather than creating a second entry, so there is only ever
  one `GuestImport` per (module, function) key regardless of how many images
  import it.
- **`Install()` throwing partway through, before `InstallKernel32` runs**:
  the actual stop is `x86.init=missing import ...` from the run loop
  dispatching through an unbound sentinel — that only happens once the guest
  is already executing, i.e. after `Install()` returned normally. An
  exception inside `Install()` would show up differently (and everything
  after it in the call chain — `X86Direct3D9.Install`, `GuestSteam.Install`
  — would not have had a chance to run either, which is not what the rest of
  the probe report shows).

## Not yet checked

Everything above was static reading — no debugger, no log line added and
rebuilt. The next real step is instrumentation, not more reading: log
inside `GuestKernel.Kernel32.InstallKernel32` right before and after the
`Interlocked` registrations (does it even get called; does `handlers` really
contain the key afterward), and log inside `GuestImports.Bind` /
`GuestImports.Register` for this one key specifically, to see which side —
if either — never sees the other's write. Two remaining real candidates,
neither eliminated: (1) a second `GuestKernel`/`GuestImports` instance
somewhere getting `Install()`ed while the *other* one is what the running
process actually binds against (two objects, not one, so `Register` and
`Bind` talk to different dictionaries); (2) `Canonical()`'s module-name
folding producing a *different* string for this particular import's actual
declared module (an API set name, not literally `KERNEL32.dll`) than the
literal `"kernel32.dll"` constant `InstallKernel32` registers under — checked
that the diagnostic's displayed module name matches, but that display uses
`import.Module`, which is set from `Bind()`'s own already-canonicalized
`module` parameter, not from a second independent source, so it cannot
actually catch a mismatch between the two callers.

## Update (build 394): implemented calling SetUnhandledExceptionFilter, no change

Added real support for calling a program's own top-level unhandled-exception
filter before treating an exception as fatal (`GuestKernel.Seh.cs`'s
`CallUnhandledFilter`/`FilterReturned`, commit c14c08a) — this is a real,
independently useful fix (any program that installs one and expects
`EXCEPTION_CONTINUE_EXECUTION` to resume it now gets that), but it did not
change WAVESHAPER's crash at all: same `0xC0000005 at 0x0020F300`, same
`x86.eip=0x7EF00010`.

Added logging for every `SetUnhandledExceptionFilter` call and for the new
filter dispatch (commit be5704b) to find out why. Answer: **WAVESHAPER never
calls `SetUnhandledExceptionFilter` at all** — no log line appears before the
crash. So this specific exception is unhandled because there is genuinely no
handler anywhere in the chain, not because a filter declined to resume it.
The CEG (Steamworks' anti-tamper wrapper, `Steamworks_InitCEGLibrary` /
`Steamworks_SelfCheck()` in the log right before the crash) does something
else that raises this — most likely a plain, deliberate access violation
used as an integrity/anti-debug check with no SEH involved at all, or a
genuine bug in how the layer serves whatever CEG is probing.

Not chased further that night — this needs figuring out what CEG is
actually doing at 0x0020F300 (a stack address, not image code — `Detail` on
the raised exception came back null, meaning the exception record's own
fault address was 0, so this was likely a software `RaiseException` call,
not a hardware access violation the CPU itself trapped). Next step: log the
full exception record's fields (code, flags, all parameters) at the point
`EnterHandler` finds no handler, not just the ones already surfaced.

## Update (30/09): the logging is in, not yet read

Added that logging (`GuestKernel.Seh.cs`'s `EnterHandler`, commit b2e4c18):
on the no-handler/no-filter path, logs code, flags, eip, eax, the parameter
count and every parameter word of the exception record. CI build is green.
Not yet installed/run on console (console unavailable that session) — the
next step is exactly what it was before, just with the tool now in place:
launch WAVESHAPER on a build containing b2e4c18 or later and read the new
`unhandled exception code=... flags=... eip=... eax=... nparams=...
params=[...]` log line.
