using Kiosk.Native;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

Check(ControllerMode.Desktop, "The pre-alpha keeps its existing desktop default");
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
