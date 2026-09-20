using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Kiosk.Native
{
    /// <summary>
    /// Stands in for the Windows functions this console does not hand out.
    ///
    /// A game imports far more than it calls: of the hundred and seventy-eight
    /// the loader could not answer, only some are reached on the way to a first
    /// frame. Guessing which is wasted effort, so every unanswered import gets
    /// a small piece of generated code that records its own name and returns
    /// zero. Running the game then says exactly which ones to write, in the
    /// order it needs them.
    ///
    /// Returning zero from anything is safe on x64 because the caller cleans
    /// the stack, so a stub does not need to know the signature it is standing
    /// in for.
    /// </summary>
    public sealed class Win32Shim
    {
        private const uint MEM_COMMIT = 0x1000;
        private const uint MEM_RESERVE = 0x2000;
        private const uint PAGE_READWRITE = 0x04;
        private const uint PAGE_EXECUTE_READ = 0x20;

        [DllImport("api-ms-win-core-memory-l1-1-3.dll", SetLastError = true)]
        private static extern IntPtr VirtualAllocFromApp(
            IntPtr address, UIntPtr size, uint allocationType, uint protect);

        [DllImport("api-ms-win-core-memory-l1-1-3.dll", SetLastError = true)]
        private static extern bool VirtualProtectFromApp(
            IntPtr address, UIntPtr size, uint newProtect, out uint oldProtect);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate long RecorderDelegate(long index);

        private const int ThunkSize = 96;
        private const int Capacity = 1200;

        private readonly List<string> names = new List<string>();
        private readonly HashSet<int> called = new HashSet<int>();
        private readonly RecorderDelegate recorder;
        private readonly IntPtr recorderPointer;

        private IntPtr page;
        private int used;

        /// <summary>Names of the stubs the program actually reached, in order.</summary>
        public List<string> Called { get; } = new List<string>();

        public Win32Shim()
        {
            // Held in a field so the garbage collector cannot take the delegate
            // while native code still holds its address.
            recorder = Record;
            recorderPointer = Marshal.GetFunctionPointerForDelegate(recorder);
        }

        private long Record(long index)
        {
            var slot = (int)index;
            if (slot >= 0 && slot < names.Count && called.Add(slot))
            {
                lock (Called) Called.Add(names[slot]);
            }
            return 0;
        }

        /// <summary>
        /// Emits: mov rcx, index ; mov rax, recorder ; jmp rax.
        /// A tail jump keeps the caller's return address, so the stub answers
        /// with whatever the recorder returns.
        /// </summary>
        public IntPtr StubFor(string name)
        {
            if (page == IntPtr.Zero)
            {
                page = VirtualAllocFromApp(
                    IntPtr.Zero, (UIntPtr)(ThunkSize * Capacity),
                    MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
                if (page == IntPtr.Zero) return IntPtr.Zero;
            }
            if (used >= Capacity) return IntPtr.Zero;

            var index = names.Count;
            names.Add(name);

            var at = page + used * ThunkSize;
            var code = new List<byte> { 0x48, 0xB9 };            // mov rcx, imm64
            code.AddRange(BitConverter.GetBytes((long)index));
            code.AddRange(new byte[] { 0x48, 0xB8 });            // mov rax, imm64
            code.AddRange(BitConverter.GetBytes(recorderPointer.ToInt64()));
            code.AddRange(new byte[] { 0xFF, 0xE0 });            // jmp rax
            Marshal.Copy(code.ToArray(), 0, at, code.Count);

            used++;
            return at;
        }

        /// <summary>
        /// Wraps a real function so the call is recorded and then made anyway.
        ///
        /// The four argument registers are put on the stack, the recorder is
        /// called with the index, they are put back, and the jump to the real
        /// address leaves the caller's return address in place — so the
        /// function returns straight to whoever called it, none the wiser.
        /// </summary>
        public IntPtr TraceFor(string name, IntPtr target)
        {
            if (page == IntPtr.Zero)
            {
                page = VirtualAllocFromApp(
                    IntPtr.Zero, (UIntPtr)(ThunkSize * Capacity),
                    MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
                if (page == IntPtr.Zero) return target;
            }
            if (used >= Capacity) return target;

            var index = names.Count;
            names.Add(name);

            var code = new List<byte>();
            code.AddRange(new byte[] { 0x48, 0x83, 0xEC, 0x48 });             // sub rsp, 0x48
            code.AddRange(new byte[] { 0x48, 0x89, 0x4C, 0x24, 0x20 });       // mov [rsp+20], rcx
            code.AddRange(new byte[] { 0x48, 0x89, 0x54, 0x24, 0x28 });       // mov [rsp+28], rdx
            code.AddRange(new byte[] { 0x4C, 0x89, 0x44, 0x24, 0x30 });       // mov [rsp+30], r8
            code.AddRange(new byte[] { 0x4C, 0x89, 0x4C, 0x24, 0x38 });       // mov [rsp+38], r9
            code.AddRange(new byte[] { 0x48, 0xB9 });                         // mov rcx, index
            code.AddRange(BitConverter.GetBytes((long)index));
            code.AddRange(new byte[] { 0x48, 0xB8 });                         // mov rax, recorder
            code.AddRange(BitConverter.GetBytes(recorderPointer.ToInt64()));
            code.AddRange(new byte[] { 0xFF, 0xD0 });                         // call rax
            code.AddRange(new byte[] { 0x48, 0x8B, 0x4C, 0x24, 0x20 });       // mov rcx, [rsp+20]
            code.AddRange(new byte[] { 0x48, 0x8B, 0x54, 0x24, 0x28 });       // mov rdx, [rsp+28]
            code.AddRange(new byte[] { 0x4C, 0x8B, 0x44, 0x24, 0x30 });       // mov r8, [rsp+30]
            code.AddRange(new byte[] { 0x4C, 0x8B, 0x4C, 0x24, 0x38 });       // mov r9, [rsp+38]
            code.AddRange(new byte[] { 0x48, 0x83, 0xC4, 0x48 });             // add rsp, 0x48
            code.AddRange(new byte[] { 0x48, 0xB8 });                         // mov rax, target
            code.AddRange(BitConverter.GetBytes(target.ToInt64()));
            code.AddRange(new byte[] { 0xFF, 0xE0 });                         // jmp rax

            var at = page + used * ThunkSize;
            Marshal.Copy(code.ToArray(), 0, at, code.Count);
            used++;
            return at;
        }

        /// <summary>Call once every stub exists: a page cannot be written and run.</summary>
        public void Seal()
        {
            if (page == IntPtr.Zero) return;
            VirtualProtectFromApp(
                page, (UIntPtr)(ThunkSize * Capacity), PAGE_EXECUTE_READ, out _);
        }
    }
}
