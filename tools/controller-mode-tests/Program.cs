using Kiosk.Native;
using System.Runtime.InteropServices;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

Check(ControllerMode.Desktop, "The pre-alpha keeps its existing desktop default");
Check(KeyboardMessages.TransitionData(0x57, true) == 0x00110001L, "W down includes scan code and repeat count");
Check(KeyboardMessages.TransitionData(0x57, false) == 0xC0110001L, "W up includes previous and transition state");
Check(KeyboardMessages.TransitionData(0x25, true) == 0x014B0001L, "Left arrow has the extended-key bit");
Check(KeyboardMessages.TransitionData(0x25, false) == 0xC14B0001L, "Extended key-up preserves every flag");
Check(KeyboardMessages.TransitionData(0x0D, true) == 0x001C0001L, "Main Enter is not keypad Enter");
foreach (var key in new[] { 0x57, 0x41, 0x53, 0x44, 0x25, 0x26, 0x27, 0x28, 0x20, 0x0D, 0x1B, 0x08 })
{
    var press = KeyboardMessages.TransitionData(key, true);
    var release = KeyboardMessages.TransitionData(key, false);
    Check(KeyboardMessages.ScanCode(key) != 0, "Every controller-mapped key has a scan code");
    Check((press & 0xFFFF) == 1 && (release & 0xFFFF) == 1, "Each transition has repeat count one");
    Check((release ^ press) == 0xC0000000L, "Release changes only the two state bits");
}
Console.WriteLine("Keyboard transition behavioral checks passed.");
var forward = new PointerPosition(960, 540);
var backward = new PointerPosition(960, 540);
for (var sample = 0; sample < 100; sample++)
{
    forward.Move(0.1, 0.1, 1920, 1080);
    backward.Move(-0.1, -0.1, 1920, 1080);
}
Check(forward.X == 970 && forward.Y == 550, "Small positive motions must accumulate");
Check(backward.X == 950 && backward.Y == 530, "Small negative motions must be symmetric");
var coarse = new PointerPosition(960, 540);
coarse.Move(10, 10, 1920, 1080);
Check(coarse.X == forward.X && coarse.Y == forward.Y, "Sample frequency must not change distance");
forward.Move(10000, 10000, 1920, 1080);
Check(forward.X == 1919 && forward.Y == 1079, "Motion must stop at the screen edge");
forward.Move(-1, -1, 1920, 1080);
Check(forward.X == 1918 && forward.Y == 1078, "Leaving an edge must respond immediately");
forward.Move(double.NaN, 0, 1920, 1080);
Check(forward.X == 1918 && forward.Y == 1078, "Invalid motion must not poison future samples");
forward.Move(-10000, -10000, 1920, 1080);
forward.Move(1, 1, 1920, 1080);
Check(forward.X == 1 && forward.Y == 1, "Leaving the minimum edges must respond immediately");
Console.WriteLine("Subpixel pointer behavioral checks passed.");
forward.Move(0.3, 0.3, 1920, 1080);
forward.Set(960, 540, 1920, 1080);
forward.Move(0.3, 0.3, 1920, 1080);
Check(forward.X == 960 && forward.Y == 540, "A warp must discard previous subpixel residue");
forward.Set(int.MaxValue, int.MinValue, 1920, 1080);
Check(forward.X == 1919 && forward.Y == 0, "Warped positions must remain inside the virtual screen");
forward.Move(-1, 1, 1920, 1080);
Check(forward.X == 1918 && forward.Y == 1, "Relative motion must continue from the warped position");
Console.WriteLine("Cursor warp behavioral checks passed.");
ControllerMode.Update(true, false, 0.1);
Check(ControllerMode.SystemButtons == 0, "A possible chord must not leak Menu");
ControllerMode.Update(false, false, 0.01);
Check(ControllerMode.SystemButtons == 0x10, "A short single Menu tap must still work");
ControllerMode.Update(false, false, 0.05);
Check(ControllerMode.SystemButtons == 0x10, "A short tap must survive a game input poll");
ControllerMode.Update(false, false, 0.2);
ControllerMode.Update(false, false, 0.01);
Check(ControllerMode.SystemButtons == 0, "A delivered tap must be released");

ControllerMode.Update(true, true, 0.2);
Check(ControllerMode.Desktop, "A short chord must not change modes");
Check(ControllerMode.SystemButtons == 0, "The shortcut must not become Enter or Escape");
ControllerMode.Update(true, true, 0.2);
Check(!ControllerMode.Desktop && ControllerMode.Changes == 1, "Holding the chord selects native input");
ControllerMode.Update(true, true, 2);
Check(ControllerMode.Changes == 1, "Holding the shortcut must not toggle repeatedly");
ControllerMode.Update(false, true, 0.2);
Check(ControllerMode.SystemButtons == 0, "Releasing one half must not leak View");
ControllerMode.Update(false, false, 0.1);
ControllerMode.Update(true, true, 0.4);
Check(ControllerMode.Desktop && ControllerMode.Changes == 2, "A new chord switches back to desktop");
ControllerMode.Update(false, false, 0.1);
ControllerMode.Update(false, true, 0.4);
Check(ControllerMode.SystemButtons == 0x20, "A held individual View button remains usable");
ControllerMode.Update(false, false, 0.01);
Check(ControllerMode.SystemButtons == 0, "A held individual button must release immediately");
Console.WriteLine("Controller mode behavioral checks passed.");

var allocation = Marshal.AllocHGlobal(28);
try
{
    for (var offset = 0; offset < 28; offset++) Marshal.WriteByte(allocation, offset, 0xCD);
    var description = IntPtr.Add(allocation, 4);
    Check(DxgiDescriptions.WriteWindowedFullscreenDescription(description) == 0, "Description must succeed");
    var expected = new[] { 60, 1, 0, 0, 1 };
    for (var field = 0; field < expected.Length; field++)
        Check(Marshal.ReadInt32(description, field * 4) == expected[field], "Every description field must be initialized");
    Check(Marshal.ReadInt32(allocation) == unchecked((int)0xCDCDCDCD), "Leading guard must remain intact");
    Check(Marshal.ReadInt32(allocation, 24) == unchecked((int)0xCDCDCDCD), "Trailing guard must remain intact");
    Check(DxgiDescriptions.WriteWindowedFullscreenDescription(IntPtr.Zero) == unchecked((int)0x80070057),
        "A null destination must fail, not claim success");
}
finally { Marshal.FreeHGlobal(allocation); }
Console.WriteLine("DXGI description behavioral checks passed.");
