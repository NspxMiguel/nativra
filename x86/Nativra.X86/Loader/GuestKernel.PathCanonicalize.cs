using System;
using System.Collections.Generic;

namespace Nativra.X86.Loader
{
    public sealed partial class GuestKernel
    {
        private uint CanonicalizePath(uint destination, uint source, bool wide)
        {
            if (destination == 0 || source == 0)
            {
                if (destination != 0) WriteText(destination, "", wide);
                return Fail(ErrorInvalidParameter);
            }
            // Read before writing so an in-place call preserves its source.
            var path = ReadText(source, wide);
            if (path.Length >= 260) return Fail(206); // ERROR_FILENAME_EXCED_RANGE
            var prefix = "";
            var start = 0;
            var protectedParts = 0;
            if (path.StartsWith(@"\\", StringComparison.Ordinal))
            {
                prefix = @"\\";
                start = 2;
                protectedParts = 2; // UNC server and share cannot be removed.
            }
            else if (path.StartsWith(@"\", StringComparison.Ordinal))
            {
                prefix = @"\";
                start = 1;
            }
            else if (path.Length >= 2 && path[1] == ':')
            {
                prefix = path.Substring(0, 2);
                start = 2;
                if (path.Length > 2 && path[2] == '\\') { prefix += @"\"; start++; }
            }
            var segments = path.Substring(start).Split('\\');
            var parts = new List<string>();
            for (var index = 0; index < segments.Length; index++)
            {
                var segment = segments[index];
                // The legacy API treats only backslashes as separators, and
                // leaves a final dot alone. Forward slashes remain literal.
                if (segment == "." && index + 1 < segments.Length) continue;
                if (segment == ".." && (parts.Count != 0 || prefix.Length != 0))
                {
                    if (parts.Count > protectedParts) parts.RemoveAt(parts.Count - 1);
                    if (parts.Count == 0 && prefix.Length == 0) prefix = @"\";
                    continue;
                }
                parts.Add(segment);
            }
            var result = prefix + string.Join(@"\", parts);
            if (result.Length == 0) result = @"\";
            if (result.Length == 2 && result[1] == ':') result += @"\";
            WriteText(destination, result, wide);
            return 1;
        }
    }
}
