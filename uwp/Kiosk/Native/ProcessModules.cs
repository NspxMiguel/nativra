using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Kiosk.Native
{
    public static class ProcessModules
    {
        // PSAPI reports the full byte requirement even when the caller's buffer is short.
        public static int Copy(IEnumerable<IntPtr> modules, IntPtr buffer, uint size, IntPtr needed)
        {
            if (needed == IntPtr.Zero || (buffer == IntPtr.Zero && size != 0)) return 0;
            var snapshot = new List<IntPtr>();
            var seen = new HashSet<IntPtr>();
            foreach (var module in modules)
                if (module != IntPtr.Zero && seen.Add(module)) snapshot.Add(module);
            Marshal.WriteInt32(needed, checked(snapshot.Count * IntPtr.Size));
            var capacity = size / (uint)IntPtr.Size;
            for (var index = 0; index < snapshot.Count && (uint)index < capacity; index++)
                Marshal.WriteIntPtr(buffer, index * IntPtr.Size, snapshot[index]);
            return 1;
        }
    }
}
