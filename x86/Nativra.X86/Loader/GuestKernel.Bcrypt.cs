using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace Nativra.X86.Loader
{
    public sealed partial class GuestKernel
    {
        private readonly HashSet<uint> randomProviders = new HashSet<uint>();
        private uint nextRandomProvider = 0xBC000000;
        private const uint StatusInvalidHandle = 0xC0000008;
        private const uint StatusInvalidParameter = 0xC000000D;
        private const uint StatusNotSupported = 0xC00000BB;

        private void InstallBcrypt(GuestImports imports)
        {
            const string module = "bcrypt.dll";
            imports.Register(module, "BCryptOpenAlgorithmProvider", CallConv.Stdcall, 4, c =>
            {
                if (c.Arg(0) == 0) return StatusInvalidParameter;
                memory.Write32(c.Arg(0), 0);
                if (c.Arg(1) == 0 || c.Arg(3) != 0) return StatusInvalidParameter;
                if (ReadText(c.Arg(1), true) != "RNG") return StatusNotSupported;
                if (c.Arg(2) != 0 && ReadText(c.Arg(2), true) != "Microsoft Primitive Provider") return StatusNotSupported;
                var handle = ++nextRandomProvider;
                randomProviders.Add(handle);
                memory.Write32(c.Arg(0), handle);
                return 0;
            });
            imports.Register(module, "BCryptCloseAlgorithmProvider", CallConv.Stdcall, 2, c =>
            {
                if (c.Arg(1) != 0) return StatusInvalidParameter;
                return randomProviders.Remove(c.Arg(0)) ? 0u : StatusInvalidHandle;
            });
            imports.Register(module, "BCryptGenRandom", CallConv.Stdcall, 4, c =>
                GenerateBcryptRandom(c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3)));
        }

        private uint GenerateBcryptRandom(uint provider, uint buffer, uint length, uint flags)
        {
            if (provider == 0 ? (flags & 2) == 0 : provider != 0x81 && !randomProviders.Contains(provider))
                return StatusInvalidHandle;
            if (buffer == 0 || (flags & ~3u) != 0) return StatusInvalidParameter;
            if (length == 0) return 0;
            if ((ulong)buffer + length > (1UL << 32)) return StatusInvalidParameter;
            // Bound host allocations independently of the guest's requested size.
            var bytes = new byte[Math.Min(length, 4096u)];
            try
            {
                for (uint offset = 0; offset < length;)
                {
                    random.GetBytes(bytes);
                    var count = (int)Math.Min((uint)bytes.Length, length - offset);
                    memory.WriteBytes(buffer + offset, bytes, 0, count);
                    offset += (uint)count;
                }
                return 0;
            }
            catch (CryptographicException)
            {
                return 0xC0000001; // STATUS_UNSUCCESSFUL
            }
        }
    }
}
