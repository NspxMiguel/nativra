using System;
using System.Runtime.InteropServices;

namespace Nativra.X86.Tests
{
    internal static class TestHost
    {
        /// <summary>The JIT emits x64 code; elsewhere (an arm64 Mac) the layer interprets.</summary>
        public static bool CanJit => IntPtr.Size == 8 && RuntimeInformation.ProcessArchitecture == Architecture.X64;
    }
}
