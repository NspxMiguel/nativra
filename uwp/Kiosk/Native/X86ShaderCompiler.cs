using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Nativra.X86.Loader;

namespace Kiosk.Native
{
    internal static class X86ShaderCompiler
    {
        [DllImport("d3dcompiler_47.dll", EntryPoint = "D3DCompile", CallingConvention = CallingConvention.StdCall)]
        private static extern int D3DCompile(IntPtr source, UIntPtr sourceLength, IntPtr sourceName,
            IntPtr macros, IntPtr include, IntPtr entry, IntPtr target, uint flags1, uint flags2,
            out IntPtr code, out IntPtr errors);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate IntPtr GetBufferPointerDelegate(IntPtr blob);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate UIntPtr GetBufferSizeDelegate(IntPtr blob);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint ReleaseDelegate(IntPtr blob);

        private static byte[] BlobBytes(IntPtr blob)
        {
            if (blob == IntPtr.Zero) return null;
            var table = Marshal.ReadIntPtr(blob);
            var pointer = Marshal.GetDelegateForFunctionPointer<GetBufferPointerDelegate>(
                Marshal.ReadIntPtr(table, 3 * IntPtr.Size))(blob);
            var size = Marshal.GetDelegateForFunctionPointer<GetBufferSizeDelegate>(
                Marshal.ReadIntPtr(table, 4 * IntPtr.Size))(blob).ToUInt64();
            if (size > int.MaxValue) throw new OverflowException("Shader blob is too large");
            var bytes = new byte[(int)size];
            if (bytes.Length != 0) Marshal.Copy(pointer, bytes, 0, bytes.Length);
            return bytes;
        }

        private static void Release(IntPtr blob)
        {
            if (blob == IntPtr.Zero) return;
            var table = Marshal.ReadIntPtr(blob);
            Marshal.GetDelegateForFunctionPointer<ReleaseDelegate>(
                Marshal.ReadIntPtr(table, 2 * IntPtr.Size))(blob);
        }

        private static IntPtr Ansi(string value, List<IntPtr> allocated)
        {
            if (value == null) return IntPtr.Zero;
            var bytes = Encoding.ASCII.GetBytes(value + "\0");
            var pointer = Marshal.AllocHGlobal(bytes.Length);
            allocated.Add(pointer);
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            return pointer;
        }

        public static ShaderCompileResult Compile(ShaderCompileRequest request)
        {
            var allocated = new List<IntPtr>();
            IntPtr code = IntPtr.Zero, errors = IntPtr.Zero;
            try
            {
                var source = Marshal.AllocHGlobal(request.Source.Length);
                allocated.Add(source);
                Marshal.Copy(request.Source, 0, source, request.Source.Length);
                IntPtr macros = IntPtr.Zero;
                if (request.Macros != null)
                {
                    macros = Marshal.AllocHGlobal((request.Macros.Length + 1) * 2 * IntPtr.Size);
                    allocated.Add(macros);
                    for (var i = 0; i < request.Macros.Length; i++)
                    {
                        Marshal.WriteIntPtr(macros, i * 2 * IntPtr.Size, Ansi(request.Macros[i].Name, allocated));
                        Marshal.WriteIntPtr(macros, (i * 2 + 1) * IntPtr.Size,
                            Ansi(request.Macros[i].Definition, allocated));
                    }
                    Marshal.WriteIntPtr(macros, request.Macros.Length * 2 * IntPtr.Size, IntPtr.Zero);
                    Marshal.WriteIntPtr(macros, (request.Macros.Length * 2 + 1) * IntPtr.Size, IntPtr.Zero);
                }
                var hr = D3DCompile(source, (UIntPtr)(uint)request.Source.Length,
                    Ansi(request.SourceName, allocated), macros, IntPtr.Zero,
                    Ansi(request.Entry, allocated), Ansi(request.Target, allocated),
                    request.Flags1, request.Flags2, out code, out errors);
                return new ShaderCompileResult
                {
                    HResult = unchecked((uint)hr),
                    Code = BlobBytes(code),
                    Errors = BlobBytes(errors)
                };
            }
            finally
            {
                Release(code);
                Release(errors);
                foreach (var pointer in allocated) Marshal.FreeHGlobal(pointer);
            }
        }
    }
}
