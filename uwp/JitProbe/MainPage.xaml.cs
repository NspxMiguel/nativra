using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.UI.Xaml.Controls;

namespace JitProbe
{
    /// <summary>
    /// Decides whether a translation layer (Wine/Box64-style) is even buildable
    /// on this console: it allocates memory, writes real x64 machine code into
    /// it, marks it executable and calls it. If the call returns 42, the dev
    /// mode sandbox permits runtime code generation. If it is refused, only
    /// interpreted or ahead-of-time engines can ever run here.
    /// </summary>
    public sealed partial class MainPage : Page
    {
        // The UWP-sanctioned allocator; PAGE_EXECUTE_* needs the codeGeneration
        // capability, which is exactly what this probe is testing for.
        [DllImport("api-ms-win-core-memory-l1-1-3.dll", SetLastError = true)]
        private static extern IntPtr VirtualAllocFromApp(
            IntPtr address, UIntPtr size, uint allocationType, uint protect);

        [DllImport("api-ms-win-core-memory-l1-1-3.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool VirtualProtectFromApp(
            IntPtr address, UIntPtr size, uint newProtect, out uint oldProtect);

        private const uint MEM_COMMIT = 0x1000;
        private const uint MEM_RESERVE = 0x2000;
        private const uint PAGE_READWRITE = 0x04;
        private const uint PAGE_EXECUTE_READ = 0x20;
        private const uint PAGE_EXECUTE_READWRITE = 0x40;

        // x64: mov eax, 42 ; ret  ->  returns 42 from a parameterless call.
        private static readonly byte[] Code = { 0xB8, 0x2A, 0x00, 0x00, 0x00, 0xC3 };

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int ReturnsInt();

        public MainPage()
        {
            InitializeComponent();
            Loaded += async (s, e) => await RunAsync();
        }

        private async Task RunAsync()
        {
            var log = new StringBuilder();
            var jitWorks = false;

            // Path A: allocate RW, then flip to executable (the W^X-friendly route
            // a real JIT uses).
            try
            {
                var size = (UIntPtr)4096;
                var mem = VirtualAllocFromApp(IntPtr.Zero, size, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
                log.AppendLine(mem == IntPtr.Zero
                    ? $"A1 alloc RW    : FAIL (err {Marshal.GetLastWin32Error()})"
                    : "A1 alloc RW    : ok");

                if (mem != IntPtr.Zero)
                {
                    Marshal.Copy(Code, 0, mem, Code.Length);
                    log.AppendLine("A2 write code  : ok");

                    var flipped = VirtualProtectFromApp(mem, size, PAGE_EXECUTE_READ, out _);
                    log.AppendLine(flipped
                        ? "A3 make exec   : ok"
                        : $"A3 make exec   : FAIL (err {Marshal.GetLastWin32Error()})  <- the JIT gate");

                    if (flipped)
                    {
                        var fn = Marshal.GetDelegateForFunctionPointer<ReturnsInt>(mem);
                        var result = fn();
                        log.AppendLine($"A4 call code   : returned {result}");
                        jitWorks = result == 42;
                    }
                }
            }
            catch (Exception ex)
            {
                log.AppendLine("A! exception   : " + ex.GetType().Name + " " + ex.Message);
            }

            log.AppendLine();

            // Path B: ask for RWX up front. If W^X is enforced this fails even
            // when Path A works — worth knowing for how a layer must be written.
            try
            {
                var size = (UIntPtr)4096;
                var mem = VirtualAllocFromApp(IntPtr.Zero, size, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
                log.AppendLine(mem == IntPtr.Zero
                    ? $"B1 alloc RWX   : FAIL (err {Marshal.GetLastWin32Error()})"
                    : "B1 alloc RWX   : ok");
                if (mem != IntPtr.Zero)
                {
                    Marshal.Copy(Code, 0, mem, Code.Length);
                    var fn = Marshal.GetDelegateForFunctionPointer<ReturnsInt>(mem);
                    var result = fn();
                    log.AppendLine($"B2 call code   : returned {result}");
                    if (result == 42) jitWorks = true;
                }
            }
            catch (Exception ex)
            {
                log.AppendLine("B! exception   : " + ex.GetType().Name + " " + ex.Message);
            }

            Verdict.Text = jitWorks
                ? "JIT WORKS — a translation layer is possible"
                : "JIT BLOCKED — only interpreted/AOT engines";
            Verdict.Foreground = new Windows.UI.Xaml.Media.SolidColorBrush(
                jitWorks ? Windows.UI.Color.FromArgb(255, 59, 224, 129)
                         : Windows.UI.Color.FromArgb(255, 235, 110, 125));
            Detail.Text = log.ToString();

            // Persist so the Mac can pull the verdict without reading the TV.
            try
            {
                var file = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                    "jit.txt", CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(file, Verdict.Text + "\n\n" + log);
            }
            catch { }
        }
    }
}
