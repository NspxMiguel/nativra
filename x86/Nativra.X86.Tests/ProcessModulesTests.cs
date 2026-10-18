using System;
using System.Runtime.InteropServices;
using Kiosk.Native;
using Xunit;

namespace Nativra.X86.Tests
{
    public sealed class ProcessModulesTests
    {
        [Fact]
        public void ShortBuffersReportFullSizeAndPreserveAdjacentMemory()
        {
            var buffer = Marshal.AllocHGlobal(IntPtr.Size * 2);
            var needed = Marshal.AllocHGlobal(4);
            try
            {
                var first = new IntPtr(0x1234);
                var second = new IntPtr(0x5678);
                var canary = new IntPtr(0x7654);
                Marshal.WriteIntPtr(buffer, IntPtr.Size, canary);
                var snapshot = new[] { IntPtr.Zero, first, first, second };
                Assert.Equal(1, ProcessModules.Copy(snapshot, IntPtr.Zero, 0, needed));
                Assert.Equal(2 * IntPtr.Size, Marshal.ReadInt32(needed));
                Assert.Equal(1, ProcessModules.Copy(snapshot, buffer, (uint)(IntPtr.Size + 1), needed));
                Assert.Equal(first, Marshal.ReadIntPtr(buffer));
                Assert.Equal(canary, Marshal.ReadIntPtr(buffer, IntPtr.Size));
                Assert.Equal(1, ProcessModules.Copy(snapshot, buffer, (uint)(2 * IntPtr.Size), needed));
                Assert.Equal(second, Marshal.ReadIntPtr(buffer, IntPtr.Size));
            }
            finally { Marshal.FreeHGlobal(buffer); Marshal.FreeHGlobal(needed); }
        }

        [Fact]
        public void InvalidOutputsAreRejectedWithoutWriting()
        {
            Assert.Equal(0, ProcessModules.Copy(new[] { new IntPtr(1) }, IntPtr.Zero, 8, new IntPtr(1)));
            Assert.Equal(0, ProcessModules.Copy(Array.Empty<IntPtr>(), IntPtr.Zero, 0, IntPtr.Zero));
        }
    }
}
