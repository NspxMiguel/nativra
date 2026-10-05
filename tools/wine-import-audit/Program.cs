using System.Text.RegularExpressions;
using Nativra.X86.Cpu;
using Nativra.X86.Loader;

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: dotnet run --project tools/wine-import-audit -- <wine-checkout> <output.tsv>");
    return 2;
}
var modules = new[] { "kernel32", "kernelbase", "ntdll", "user32", "gdi32", "advapi32", "shell32",
    "ole32", "oleaut32", "winmm", "ws2_32", "setupapi", "cfgmgr32", "msvcrt", "mfplat", "wmvcore", "quartz" };
// Install the real guest registrations, including generated aliases. No guest
// binary is loaded and no game/session data is read. Host-only bridges and
// packaged native DLLs are deliberately outside this kernel inventory.
using var process = new GuestProcess(new GuestMemory(), useJit: false);
var kernel = new GuestKernel(process) { ExePath = @"C:\audit\audit.exe" };
kernel.Install();
var rows = new List<string> { "module\texport\twine_kind\tguest_registration" };
Console.WriteLine("Guest-kernel registrations only; presence does not prove behavior. Host bridges and packaged DLLs are excluded.");
foreach (var module in modules)
{
    var path = Path.Combine(args[0], "dlls", module, module + ".spec");
    if (!File.Exists(path)) throw new FileNotFoundException("Wine export specification is missing", path);
    var symbols = new SortedDictionary<string, string>(StringComparer.Ordinal);
    foreach (var line in File.ReadLines(path))
    {
        var fields = line.Split('#')[0].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 3 || fields[0] != "@" && !uint.TryParse(fields[0], out _)) continue;
        // These are not named 32-bit public exports.
        if (fields.Contains("-private") || fields.Contains("-noname") ||
            fields.Any(f => f.StartsWith("-arch=") && !f.Contains("i386")) ||
            fields.Contains("-x86_64") || fields.Contains("-arm64")) continue;
        var token = fields.Skip(2).FirstOrDefault(f => !f.StartsWith('-'));
        if (token == null) continue;
        var symbol = token.Split('(')[0];
        if (!Regex.IsMatch(symbol, @"^[A-Za-z_?@$][A-Za-z0-9_?@$]*$")) continue;
        symbols[symbol] = fields[1];
    }
    var registered = 0;
    foreach (var symbol in symbols)
    {
        var present = process.Imports.HasHandler(module + ".dll", symbol.Key);
        if (present) registered++;
        rows.Add($"{module}.dll\t{symbol.Key}\t{symbol.Value}\t{(present ? "registered" : "absent")}");
    }
    Console.WriteLine($"{module}: {registered}/{symbols.Count} named exports registered");
}
File.WriteAllLines(args[1], rows);
return 0;
