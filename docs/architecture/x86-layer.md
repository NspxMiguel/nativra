# `x86/` — running 32-bit games on a 64-bit process

Part of [../ARCHITECTURE.md](../ARCHITECTURE.md). A 32-bit game cannot call a
64-bit Windows function directly, so this is a small computer: it decodes and
runs x86 machine code itself, and answers the Win32 calls that code makes —
the 32-bit counterpart to `uwp/Kiosk/Native/`, built from scratch because
none of that code runs unmodified here.

## How it fits together

When a 32-bit game instruction executes: the **JIT** (`Jit/JitEngine.cs`)
first tries to translate a block of straight-line code into native x64
(`BlockTranslator.cs`), with guest registers pinned to host registers so ALU
flags fall out unchanged — fast, and always a cache of the interpreter's
behaviour, never a second source of truth for it. On a control-flow edge or
an instruction the JIT does not translate, it hands off to the
**`Cpu/Interpreter.cs`**, which executes one instruction at a time against
the same `CpuState`/`GuestMemory`. Both paths call into `GuestImports` when
the guest calls an imported function; that looks the import up and invokes a
host handler — either **`Loader/GuestKernel*.cs`** for the Win32 surface this
layer implements itself, or a callback into the real app's `uwp/Kiosk/Native/`
bridges for graphics/audio/file I/O the 64-bit side already answers well.

## CPU (`x86/Nativra.X86/Cpu/`)

| File | What it does |
| --- | --- |
| `Decoder.cs` | Parses instruction bytes: opcode, prefixes, ModRM, immediates, length |
| `Interpreter.cs` | Executes one instruction at a time; computes flags eagerly; the reference behaviour the JIT is validated against |
| `Interpreter.TwoByte.cs` | The 0F-prefixed two-byte opcodes (SSE, system instructions) |
| `CpuState.cs` | The guest's register file (EAX–EDI, EIP, EFLAGS) and segment bases |
| `Bits.cs` | Bit-manipulation helpers shared by the decoder and interpreter |
| `GuestMemory.cs` | The 32-bit guest address space, backed by host pages; raises a guest fault on an unmapped access |
| `GuestException.cs` | The exception type for a guest memory fault |
| `HostPages.cs` | The backing store and page mapping under `GuestMemory` |
| `Fpu.X87.cs` | The x87 floating-point stack and its instructions |
| `Fpu.Sse.cs` | SSE registers and instructions |

## JIT (`x86/Nativra.X86/Jit/`)

| File | What it does |
| --- | --- |
| `JitEngine.cs` | Translates a block at the current EIP, runs the native code, falls back to the interpreter for what it could not translate |
| `BlockTranslator.cs` | Straight-line x86 → one self-contained x64 function |
| `X64Emit.cs` | The x64 code emitter: instruction encoding, register allocation |
| `CodeCache.cs` | Caches a compiled block by address for reuse |
| `JitContext.cs` | Runtime context: register maps, scratch registers, the context pointer |
| `JitFaults.cs` | Handles a fault that comes out of JIT-compiled code |

## Guest runtime (`x86/Nativra.X86/Loader/`)

| File | What it does |
| --- | --- |
| `GuestKernel.cs` | The baseline Win32 surface: heap, virtual memory, TLS, module handles, command line, timing, critical sections |
| `GuestKernel.Files.cs` | File I/O (`GetFileSize`, `ReadFile`, `WriteFile`, ...) |
| `GuestKernel.Runtime.cs` | `GetTickCount`, `Sleep`, `ExitProcess`, and similar |
| `GuestKernel.Threads.cs` | Thread management (`CreateThread`, `GetCurrentThread`) |
| `GuestKernel.Seh.cs` | Structured exception handling — try/catch/finally plumbing for the guest |
| `GuestKernel.User32.cs` | Window/UI stubs (`PostMessage`, `GetMessage`, ...) |
| `GuestProcess.cs` | The guest process container: command line, modules, entry point |
| `GuestProcess.Threads.cs` | Per-thread state inside a guest process |
| `GuestHeap.cs` | The guest heap (`malloc`/`free`) |
| `GuestFiles.cs` | The guest's file handle table |
| `GuestImports.cs` | The import table, calling conventions (stdcall/cdecl/thiscall), and the struct a host handler receives |
| `GuestCom.cs` | COM interface wrappers (`IUnknown`, `IDispatch`) for the guest |
| `Direct3D9Com.cs` | Direct3D 9 COM interfaces for a 32-bit game |
| `XAudio27Com.cs` | XAudio 2.7 COM interfaces for a 32-bit game |
| `Pe32Image.cs` | Parses a PE32 binary: sections, imports, relocations, TLS |

## Tests and the oracle

`x86/Nativra.X86.Tests/` (22 files) covers the decoder, interpreter, JIT
(including a lockstep test that runs interpreter and JIT side by side and
compares state), and every `GuestKernel`/`GuestProcess`/`GuestFiles`/COM
surface above — none of it needs a console.

`x86/harness/` is the oracle: `oracle.c` builds a small .exe that runs the
same instructions on a real x86-64 CPU, recording what actually happens
(`oracle-vectors.txt`) so the interpreter's behaviour can be checked against
real hardware, not just against itself.
