using System;
using System.Collections.Generic;
using System.IO;

namespace Nativra.X86.Loader
{
    // Where a DLL named without a host-known location is looked for, in the
    // guest's own files, the way Windows searches: the folder of the DLL being
    // loaded (what LOAD_WITH_ALTERED_SEARCH_PATH asks for, and what a Source
    // engine's bin\ needs), the program's folder, the current directory and
    // PATH. A miss is remembered until PATH or the current directory change,
    // since every import of a system DLL asks again.
    public sealed partial class GuestKernel
    {
        private readonly Stack<string> loadingFolders = new Stack<string>();
        private readonly HashSet<string> searchMissed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private void InstallSearch()
        {
            process.ModuleSearch = SearchModule;
        }

        private byte[] SearchModule(string key)
        {
            if (Files == null || searchMissed.Contains(key)) return null;
            foreach (var folder in SearchFolders())
            {
                var bytes = ReadGuestFile(folder.TrimEnd('\\') + "\\" + key);
                if (bytes != null) return bytes;
            }
            searchMissed.Add(key);
            return null;
        }

        private IEnumerable<string> SearchFolders()
        {
            if (loadingFolders.Count > 0) yield return loadingFolders.Peek();
            if (!string.IsNullOrEmpty(ExePath)) yield return Folder(ExePath);
            yield return CurrentDirectory;
            environment.TryGetValue("PATH", out var path);
            foreach (var entry in (path ?? "").Split(';'))
                if (entry.Trim().Length > 0) yield return FullPath(entry.Trim());
        }

        /// <summary>A guest file's bytes, or null when it is not there (or not readable).</summary>
        private byte[] ReadGuestFile(string path)
        {
            try
            {
                using (var stream = Files.Open(path, FileMode.Open, FileAccess.Read))
                using (var copy = new MemoryStream())
                {
                    stream.CopyTo(copy);
                    return copy.ToArray();
                }
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        /// <summary>A DLL named with a folder ("hl2mp\bin\server.dll"): that very file.</summary>
        private Pe32Image LoadFromPath(string raw, string key)
        {
            if (Files == null || raw.IndexOfAny(new[] { '\\', '/' }) < 0) return null;
            var path = FullPath(raw);
            var file = path.Substring(path.LastIndexOf('\\') + 1);
            if (file.IndexOf('.') < 0) path += ".dll";
            var bytes = ReadGuestFile(path);
            if (bytes == null) return null;
            // Its own imports are looked for beside it first.
            loadingFolders.Push(Folder(path));
            try
            {
                return process.LoadImage(key, bytes);
            }
            finally
            {
                loadingFolders.Pop();
            }
        }

        private void SearchChanged() => searchMissed.Clear();
    }
}
