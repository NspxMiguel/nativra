using System;
using Kiosk.Native;
using Xunit;

namespace Nativra.X86.Tests
{
    public sealed class ImportTracingTests
    {
        [Theory]
        [InlineData(0u)]
        [InlineData(0x01u)]
        [InlineData(0x02u)]
        [InlineData(0x04u)]
        [InlineData(0x08u)]
        [InlineData(0x120u)]
        public void DataAndUnclassifiedExportsKeepTheirObjectAddress(uint protection)
        {
            var data = new IntPtr(0x12345678);
            var result = ImportTracing.Resolve(data, true,
                _ => throw new InvalidOperationException("A data export cannot be wrapped"), _ => protection);
            Assert.Equal(data, result);
        }

        [Theory]
        [InlineData(0x10u)]
        [InlineData(0x20u)]
        [InlineData(0x40u)]
        [InlineData(0x80u)]
        public void ExecutableExportsUseTheFunctionThunk(uint protection)
        {
            var code = new IntPtr(0x12345678);
            var thunk = new IntPtr(0x23456789);
            Assert.Equal(thunk, ImportTracing.Resolve(code, true, p =>
            {
                Assert.Equal(code, p);
                return thunk;
            }, _ => protection));
        }

        [Fact]
        public void DisabledTracingAndNullExportsDoNotQueryOrWrap()
        {
            uint Query(IntPtr _) => throw new InvalidOperationException("Unexpected query");
            IntPtr Wrap(IntPtr _) => throw new InvalidOperationException("Unexpected wrapper");
            var code = new IntPtr(0x12345678);
            Assert.Equal(code, ImportTracing.Resolve(code, false, Wrap, Query));
            Assert.Equal(IntPtr.Zero, ImportTracing.Resolve(IntPtr.Zero, true, Wrap, Query));
        }
    }
}
