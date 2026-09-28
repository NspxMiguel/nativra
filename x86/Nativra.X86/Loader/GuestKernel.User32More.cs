using System;
using System.Collections.Generic;
using Nativra.X86.Cpu;

namespace Nativra.X86.Loader
{
    // The rest of user32 a game touches: wsprintf, string resources and
    // bitmaps, rectangles, painting, text layout, window lookup and
    // enumeration, menus and dialogs as the console has them (no menus to
    // show; a modal dialog answers as if its OK button were pressed), the
    // clipboard, the caret, keyboard layouts and input injection.
    public sealed partial class GuestKernel
    {
        private uint nextUserHandle = 0x00E00010;
        private int caretBlink = 530;

        private uint NewUserHandle() { var h = nextUserHandle; nextUserHandle += 4; return h; }

        private void InstallUser32More(GuestImports i)
        {
            const string u = "user32.dll";

            i.Register(u, "wsprintfA", CallConv.Cdecl, 2, c => Emit(FormatPrintf(CString(c.Arg(1)), Args(c, 2), false), c.Arg(0), 1025, false, 1));
            i.Register(u, "wsprintfW", CallConv.Cdecl, 2, c => Emit(FormatPrintf(WString(c.Arg(1)), Args(c, 2), true), c.Arg(0), 1025, true, 1));
            i.Register(u, "wvsprintfA", CallConv.Stdcall, 3, c => Emit(FormatPrintf(CString(c.Arg(1)), VaList(c.Arg(2)), false), c.Arg(0), 1025, false, 1));
            i.Register(u, "wvsprintfW", CallConv.Stdcall, 3, c => Emit(FormatPrintf(WString(c.Arg(1)), VaList(c.Arg(2)), true), c.Arg(0), 1025, true, 1));

            foreach (var wide in new[] { false, true })
            {
                var w = wide;
                var s = wide ? "W" : "A";
                i.Register(u, "LoadString" + s, CallConv.Stdcall, 4, c => LoadString(c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3), w));
                i.Register(u, "LoadBitmap" + s, CallConv.Stdcall, 2, c => LoadBitmap(c.Arg(0), c.Arg(1), w));
                i.Register(u, "LoadAccelerators" + s, CallConv.Stdcall, 2, c => NewUserHandle());
                i.Register(u, "CreateAcceleratorTable" + s, CallConv.Stdcall, 2, c => NewUserHandle());
                i.Register(u, "CharNext" + s, CallConv.Stdcall, 1, c => memory.Read16(c.Arg(0)) == 0 && w || !w && memory.Read8(c.Arg(0)) == 0 ? c.Arg(0) : c.Arg(0) + (w ? 2u : 1u));
                i.Register(u, "CharPrev" + s, CallConv.Stdcall, 2, c => c.Arg(1) > c.Arg(0) ? c.Arg(1) - (w ? 2u : 1u) : c.Arg(0));
                i.Register(u, "CharToOem" + s, CallConv.Stdcall, 2, c => { if (c.Arg(0) != c.Arg(1)) WriteText(c.Arg(1), ReadText(c.Arg(0), w), false); return 1; });
                i.Register(u, "OemToChar" + s, CallConv.Stdcall, 2, c => { if (c.Arg(0) != c.Arg(1)) WriteText(c.Arg(1), ReadText(c.Arg(0), false), w); return 1; });
                i.Register(u, "CharToOemBuff" + s, CallConv.Stdcall, 3, c => { MoveMemory(c.Arg(1), c.Arg(0), c.Arg(2)); return 1; });
                i.Register(u, "OemToCharBuff" + s, CallConv.Stdcall, 3, c => { MoveMemory(c.Arg(1), c.Arg(0), c.Arg(2)); return 1; });
                i.Register(u, "IsCharAlpha" + s, CallConv.Stdcall, 1, c => char.IsLetter(CharArg(c.Arg(0), w)) ? 1u : 0u);
                i.Register(u, "IsCharAlphaNumeric" + s, CallConv.Stdcall, 1, c => char.IsLetterOrDigit(CharArg(c.Arg(0), w)) ? 1u : 0u);
                i.Register(u, "IsCharUpper" + s, CallConv.Stdcall, 1, c => char.IsUpper(CharArg(c.Arg(0), w)) ? 1u : 0u);
                i.Register(u, "IsCharLower" + s, CallConv.Stdcall, 1, c => char.IsLower(CharArg(c.Arg(0), w)) ? 1u : 0u);
                i.Register(u, "CharLowerBuff" + s, CallConv.Stdcall, 2, c => MapBuffer(c.Arg(0), c.Arg(1), w, false));
                i.Register(u, "CharUpperBuff" + s, CallConv.Stdcall, 2, c => MapBuffer(c.Arg(0), c.Arg(1), w, true));
                i.Register(u, "DrawText" + s, CallConv.Stdcall, 5, c => DrawText(c.Arg(0), c.Arg(1), (int)c.Arg(2), c.Arg(3), c.Arg(4), w));
                i.Register(u, "DrawTextEx" + s, CallConv.Stdcall, 6, c => DrawText(c.Arg(0), c.Arg(1), (int)c.Arg(2), c.Arg(3), c.Arg(4), w));
                i.Register(u, "TabbedTextOut" + s, CallConv.Stdcall, 8, c => 0);
                i.Register(u, "FindWindow" + s, CallConv.Stdcall, 2, c => FindWindow(0, 0, c.Arg(0), c.Arg(1), w));
                i.Register(u, "FindWindowEx" + s, CallConv.Stdcall, 4, c => FindWindow(c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3), w));
                i.Register(u, "GetWindowModuleFileName" + s, CallConv.Stdcall, 3, c => CopyTruncated(ExePath, c.Arg(1), c.Arg(2), w));
                i.Register(u, "AppendMenu" + s, CallConv.Stdcall, 4, c => 1);
                i.Register(u, "InsertMenu" + s, CallConv.Stdcall, 5, c => 1);
                i.Register(u, "InsertMenuItem" + s, CallConv.Stdcall, 4, c => 1);
                i.Register(u, "ModifyMenu" + s, CallConv.Stdcall, 5, c => 1);
                i.Register(u, "SetMenuItemInfo" + s, CallConv.Stdcall, 4, c => 1);
                i.Register(u, "GetMenuItemInfo" + s, CallConv.Stdcall, 4, c => 0);
                i.Register(u, "GetMenuString" + s, CallConv.Stdcall, 5, c => 0);
                i.Register(u, "LoadMenu" + s, CallConv.Stdcall, 2, c => NewUserHandle());
                i.Register(u, "LoadMenuIndirect" + s, CallConv.Stdcall, 1, c => NewUserHandle());
                i.Register(u, "DialogBoxParam" + s, CallConv.Stdcall, 5, c => ModalDialog());
                i.Register(u, "DialogBoxIndirectParam" + s, CallConv.Stdcall, 5, c => ModalDialog());
                i.Register(u, "CreateDialogParam" + s, CallConv.Stdcall, 5, c => { Say("x86: modeless dialog not shown"); return 0; });
                i.Register(u, "CreateDialogIndirectParam" + s, CallConv.Stdcall, 5, c => { Say("x86: modeless dialog not shown"); return 0; });
                i.Register(u, "DefDlgProc" + s, CallConv.Stdcall, 4, c => DefWindowProc(c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3)));
                i.Register(u, "SetDlgItemText" + s, CallConv.Stdcall, 3, c => 1);
                i.Register(u, "GetDlgItemText" + s, CallConv.Stdcall, 4, c => { if (c.Arg(3) > 0) WriteText(c.Arg(2), "", w); return 0; });
                i.Register(u, "SendDlgItemMessage" + s, CallConv.Stdcall, 5, c => 0);
                i.Register(u, "RegisterClipboardFormat" + s, CallConv.Stdcall, 1, c => RegisterMessage("clip:" + ReadText(c.Arg(0), w)));
                i.Register(u, "GetClipboardFormatName" + s, CallConv.Stdcall, 3, c => 0);
                i.Register(u, "VkKeyScan" + s, CallConv.Stdcall, 1, c => VkKeyScan(CharArg(c.Arg(0), w)));
                i.Register(u, "VkKeyScanEx" + s, CallConv.Stdcall, 2, c => VkKeyScan(CharArg(c.Arg(0), w)));
                i.Register(u, "GetKeyboardLayoutName" + s, CallConv.Stdcall, 1, c => { WriteText(c.Arg(0), "00000409", w); return 1; });
                i.Register(u, "LoadKeyboardLayout" + s, CallConv.Stdcall, 2, c => 0x04090409);
                i.Register(u, "GetUserObjectInformation" + s, CallConv.Stdcall, 5, c =>
                {
                    if (c.Arg(1) == 1 && c.Arg(3) >= 12) { memory.Write32(c.Arg(2), 0); memory.Write32(c.Arg(2) + 4, 0); memory.Write32(c.Arg(2) + 8, 1); }   // UOI_FLAGS: WSF_VISIBLE
                    if (c.Arg(4) != 0) memory.Write32(c.Arg(4), 12);
                    return 1;
                });
                i.Register(u, "LoadCursorFromFile" + s, CallConv.Stdcall, 1, c => FakeCursor);
                i.Register(u, "CallMsgFilter" + s, CallConv.Stdcall, 2, c => 0);
            }
            i.Register(u, "DestroyAcceleratorTable", CallConv.Stdcall, 1, c => 1);

            // Rectangles (RECT: left, top, right, bottom).
            i.Register(u, "SetRect", CallConv.Stdcall, 5, c => { WriteRect(c.Arg(0), (int)c.Arg(1), (int)c.Arg(2), (int)c.Arg(3), (int)c.Arg(4)); return 1; });
            i.Register(u, "SetRectEmpty", CallConv.Stdcall, 1, c => { WriteRect(c.Arg(0), 0, 0, 0, 0); return 1; });
            i.Register(u, "CopyRect", CallConv.Stdcall, 2, c => { MoveMemory(c.Arg(0), c.Arg(1), 16); return 1; });
            i.Register(u, "InflateRect", CallConv.Stdcall, 3, c =>
            {
                var r = ReadRect(c.Arg(0));
                WriteRect(c.Arg(0), r[0] - (int)c.Arg(1), r[1] - (int)c.Arg(2), r[2] + (int)c.Arg(1), r[3] + (int)c.Arg(2));
                return 1;
            });
            i.Register(u, "OffsetRect", CallConv.Stdcall, 3, c =>
            {
                var r = ReadRect(c.Arg(0));
                WriteRect(c.Arg(0), r[0] + (int)c.Arg(1), r[1] + (int)c.Arg(2), r[2] + (int)c.Arg(1), r[3] + (int)c.Arg(2));
                return 1;
            });
            i.Register(u, "IntersectRect", CallConv.Stdcall, 3, c =>
            {
                int[] a = ReadRect(c.Arg(1)), b = ReadRect(c.Arg(2));
                int l = Math.Max(a[0], b[0]), t = Math.Max(a[1], b[1]), r = Math.Min(a[2], b[2]), bo = Math.Min(a[3], b[3]);
                if (l >= r || t >= bo) { WriteRect(c.Arg(0), 0, 0, 0, 0); return 0; }
                WriteRect(c.Arg(0), l, t, r, bo);
                return 1;
            });
            i.Register(u, "UnionRect", CallConv.Stdcall, 3, c =>
            {
                int[] a = ReadRect(c.Arg(1)), b = ReadRect(c.Arg(2));
                bool ea = a[0] >= a[2] || a[1] >= a[3], eb = b[0] >= b[2] || b[1] >= b[3];
                if (ea && eb) { WriteRect(c.Arg(0), 0, 0, 0, 0); return 0; }
                if (ea) a = b; else if (eb) b = a;
                WriteRect(c.Arg(0), Math.Min(a[0], b[0]), Math.Min(a[1], b[1]), Math.Max(a[2], b[2]), Math.Max(a[3], b[3]));
                return 1;
            });
            i.Register(u, "SubtractRect", CallConv.Stdcall, 3, c => { MoveMemory(c.Arg(0), c.Arg(1), 16); return 1; });
            i.Register(u, "PtInRect", CallConv.Stdcall, 3, c =>
            {
                var r = ReadRect(c.Arg(0));
                int x = (int)c.Arg(1), y = (int)c.Arg(2);
                return x >= r[0] && x < r[2] && y >= r[1] && y < r[3] ? 1u : 0u;
            });
            i.Register(u, "IsRectEmpty", CallConv.Stdcall, 1, c => { var r = ReadRect(c.Arg(0)); return r[0] >= r[2] || r[1] >= r[3] ? 1u : 0u; });
            i.Register(u, "EqualRect", CallConv.Stdcall, 2, c =>
            {
                int[] a = ReadRect(c.Arg(0)), b = ReadRect(c.Arg(1));
                return a[0] == b[0] && a[1] == b[1] && a[2] == b[2] && a[3] == b[3] ? 1u : 0u;
            });

            // Painting.
            i.Register(u, "BeginPaint", CallConv.Stdcall, 2, c =>
            {
                memory.WriteBytes(c.Arg(1), new byte[64]);
                memory.Write32(c.Arg(1), FakeDc);
                if (windows.TryGetValue(c.Arg(0), out var win)) WriteRect(c.Arg(1) + 8, 0, 0, win.Width, win.Height);
                return FakeDc;
            });
            i.Register(u, "EndPaint", CallConv.Stdcall, 2, c => 1);
            i.Register(u, "GetUpdateRect", CallConv.Stdcall, 3, c => { if (c.Arg(1) != 0) WriteRect(c.Arg(1), 0, 0, 0, 0); return 0; });
            i.Register(u, "GetUpdateRgn", CallConv.Stdcall, 3, c => 1);   // NULLREGION
            i.Register(u, "InvalidateRgn", CallConv.Stdcall, 3, c => 1);
            i.Register(u, "ValidateRgn", CallConv.Stdcall, 2, c => 1);
            i.Register(u, "FillRect", CallConv.Stdcall, 3, c =>
            {
                var r = ReadRect(c.Arg(1));
                FillArea(c.Arg(0), r[0], r[1], r[2], r[3], c.Arg(2));
                return 1;
            });
            i.Register(u, "FrameRect", CallConv.Stdcall, 3, c => 1);
            i.Register(u, "InvertRect", CallConv.Stdcall, 2, c => 1);
            i.Register(u, "DrawFocusRect", CallConv.Stdcall, 2, c => 1);
            i.Register(u, "DrawEdge", CallConv.Stdcall, 4, c => 1);
            i.Register(u, "DrawFrameControl", CallConv.Stdcall, 4, c => 1);
            i.Register(u, "DrawIcon", CallConv.Stdcall, 4, c => 1);
            i.Register(u, "DrawIconEx", CallConv.Stdcall, 9, c => 1);
            i.Register(u, "ScrollWindow", CallConv.Stdcall, 5, c => 1);
            i.Register(u, "ScrollWindowEx", CallConv.Stdcall, 8, c => 1);
            i.Register(u, "ScrollDC", CallConv.Stdcall, 7, c => 1);
            i.Register(u, "GetScrollInfo", CallConv.Stdcall, 3, c => 0);
            i.Register(u, "SetScrollInfo", CallConv.Stdcall, 4, c => 0);
            i.Register(u, "GetScrollPos", CallConv.Stdcall, 2, c => 0);
            i.Register(u, "SetScrollPos", CallConv.Stdcall, 4, c => 0);
            i.Register(u, "SetScrollRange", CallConv.Stdcall, 5, c => 1);
            i.Register(u, "ShowScrollBar", CallConv.Stdcall, 3, c => 1);
            i.Register(u, "EnableScrollBar", CallConv.Stdcall, 3, c => 1);

            // Windows.
            i.Register(u, "EnumWindows", CallConv.Stdcall, 2, c => EnumWindows(c.Arg(0), c.Arg(1), w => w.Parent == 0));
            i.Register(u, "EnumChildWindows", CallConv.Stdcall, 3, c => EnumWindows(c.Arg(1), c.Arg(2), w => w.Parent == c.Arg(0)));
            i.Register(u, "EnumThreadWindows", CallConv.Stdcall, 3, c => EnumWindows(c.Arg(1), c.Arg(2), w => w.OwnerThread == c.Arg(0) && w.Parent == 0));
            i.Register(u, "EnumDesktopWindows", CallConv.Stdcall, 3, c => EnumWindows(c.Arg(1), c.Arg(2), w => w.Parent == 0));
            i.Register(u, "WindowFromPoint", CallConv.Stdcall, 2, c => InputWindow);
            i.Register(u, "WindowFromDC", CallConv.Stdcall, 1, c => InputWindow);
            i.Register(u, "ChildWindowFromPoint", CallConv.Stdcall, 3, c => c.Arg(0));
            i.Register(u, "ChildWindowFromPointEx", CallConv.Stdcall, 4, c => c.Arg(0));
            i.Register(u, "RealChildWindowFromPoint", CallConv.Stdcall, 3, c => c.Arg(0));
            i.Register(u, "GetWindowInfo", CallConv.Stdcall, 2, c =>
            {
                if (!windows.TryGetValue(c.Arg(0), out var win)) return 0;
                var p = c.Arg(1);
                memory.WriteBytes(p, new byte[60]);
                memory.Write32(p, 60);
                WriteRect(p + 4, win.X, win.Y, win.X + win.Width, win.Y + win.Height);
                WriteRect(p + 20, win.X, win.Y, win.X + win.Width, win.Y + win.Height);
                memory.Write32(p + 36, win.Style);
                memory.Write32(p + 40, win.ExStyle);
                memory.Write32(p + 44, c.Arg(0) == InputWindow ? 1u : 0u);   // WS_ACTIVECAPTION
                memory.Write16(p + 56, 0x0409);                                // creator version
                return 1;
            });
            i.Register(u, "SetParent", CallConv.Stdcall, 2, c =>
            {
                if (!windows.TryGetValue(c.Arg(0), out var win)) return 0;
                var old = win.Parent;
                win.Parent = c.Arg(1);
                return old;
            });
            i.Register(u, "GetLastActivePopup", CallConv.Stdcall, 1, c => c.Arg(0));
            i.Register(u, "ShowOwnedPopups", CallConv.Stdcall, 2, c => 1);
            i.Register(u, "IsHungAppWindow", CallConv.Stdcall, 1, c => 0);
            i.Register(u, "SetWindowRgn", CallConv.Stdcall, 3, c => 1);
            i.Register(u, "GetWindowRgn", CallConv.Stdcall, 2, c => 0);
            i.Register(u, "SetLayeredWindowAttributes", CallConv.Stdcall, 4, c => 1);
            i.Register(u, "UpdateLayeredWindow", CallConv.Stdcall, 9, c => 1);
            i.Register(u, "GetClassWord", CallConv.Stdcall, 2, c => 0);
            i.Register(u, "SetClassWord", CallConv.Stdcall, 3, c => 0);
            i.Register(u, "GetWindowWord", CallConv.Stdcall, 2, c => GetWindowLong(c.Arg(0), (int)c.Arg(1)) & 0xFFFF);
            i.Register(u, "SetWindowWord", CallConv.Stdcall, 3, c => SetWindowLong(c.Arg(0), (int)c.Arg(1), c.Arg(2)) & 0xFFFF);
            i.Register(u, "OpenIcon", CallConv.Stdcall, 1, c => 1);
            i.Register(u, "CloseWindow", CallConv.Stdcall, 1, c => 1);
            i.Register(u, "AnimateWindow", CallConv.Stdcall, 3, c => 1);
            i.Register(u, "PrintWindow", CallConv.Stdcall, 3, c => 1);
            i.Register(u, "GetTitleBarInfo", CallConv.Stdcall, 2, c => 0);
            i.Register(u, "GetGUIThreadInfo", CallConv.Stdcall, 2, c => 0);
            i.Register(u, "IsGUIThread", CallConv.Stdcall, 1, c => 1);
            i.Register(u, "InSendMessage", CallConv.Stdcall, 0, c => 0);
            i.Register(u, "InSendMessageEx", CallConv.Stdcall, 1, c => 0);
            i.Register(u, "ReplyMessage", CallConv.Stdcall, 1, c => 0);
            i.Register(u, "WaitForInputIdle", CallConv.Stdcall, 2, c => 0);
            i.Register(u, "AllowSetForegroundWindow", CallConv.Stdcall, 1, c => 1);
            i.Register(u, "LockSetForegroundWindow", CallConv.Stdcall, 1, c => 1);
            i.Register(u, "SetProcessDefaultLayout", CallConv.Stdcall, 1, c => 1);
            i.Register(u, "DisableProcessWindowsGhosting", CallConv.Stdcall, 0, c => 0);
            i.Register(u, "SwitchToThisWindow", CallConv.Stdcall, 2, c => 0);

            // Menus: bookkeeping handles only.
            i.Register(u, "CreateMenu", CallConv.Stdcall, 0, c => NewUserHandle());
            i.Register(u, "CreatePopupMenu", CallConv.Stdcall, 0, c => NewUserHandle());
            i.Register(u, "DestroyMenu", CallConv.Stdcall, 1, c => 1);
            i.Register(u, "SetMenu", CallConv.Stdcall, 2, c => { if (windows.TryGetValue(c.Arg(0), out var win)) win.Menu = c.Arg(1); return 1; });
            i.Register(u, "GetMenu", CallConv.Stdcall, 1, c => windows.TryGetValue(c.Arg(0), out var win) ? win.Menu : 0);
            i.Register(u, "GetSubMenu", CallConv.Stdcall, 2, c => 0);
            i.Register(u, "GetSystemMenu", CallConv.Stdcall, 2, c => 0);
            i.Register(u, "EnableMenuItem", CallConv.Stdcall, 3, c => 0);
            i.Register(u, "CheckMenuItem", CallConv.Stdcall, 3, c => 0);
            i.Register(u, "CheckMenuRadioItem", CallConv.Stdcall, 5, c => 1);
            i.Register(u, "DeleteMenu", CallConv.Stdcall, 3, c => 1);
            i.Register(u, "RemoveMenu", CallConv.Stdcall, 3, c => 1);
            i.Register(u, "DrawMenuBar", CallConv.Stdcall, 1, c => 1);
            i.Register(u, "GetMenuItemCount", CallConv.Stdcall, 1, c => 0);
            i.Register(u, "GetMenuItemID", CallConv.Stdcall, 2, c => 0xFFFFFFFF);
            i.Register(u, "GetMenuState", CallConv.Stdcall, 3, c => 0xFFFFFFFF);
            i.Register(u, "TrackPopupMenu", CallConv.Stdcall, 7, c => 0);
            i.Register(u, "TrackPopupMenuEx", CallConv.Stdcall, 6, c => 0);
            i.Register(u, "SetMenuDefaultItem", CallConv.Stdcall, 3, c => 1);

            // Dialog helpers.
            i.Register(u, "EndDialog", CallConv.Stdcall, 2, c => 1);
            i.Register(u, "GetDlgItem", CallConv.Stdcall, 2, c => 0);
            i.Register(u, "GetDlgCtrlID", CallConv.Stdcall, 1, c => 0);
            i.Register(u, "SetDlgItemInt", CallConv.Stdcall, 4, c => 1);
            i.Register(u, "GetDlgItemInt", CallConv.Stdcall, 4, c => { if (c.Arg(2) != 0) memory.Write32(c.Arg(2), 0); return 0; });
            i.Register(u, "CheckDlgButton", CallConv.Stdcall, 3, c => 1);
            i.Register(u, "IsDlgButtonChecked", CallConv.Stdcall, 2, c => 0);
            i.Register(u, "CheckRadioButton", CallConv.Stdcall, 4, c => 1);
            i.Register(u, "MapDialogRect", CallConv.Stdcall, 2, c => 1);
            i.Register(u, "GetDialogBaseUnits", CallConv.Stdcall, 0, c => (16u << 16) | 8);
            i.Register(u, "GetNextDlgTabItem", CallConv.Stdcall, 3, c => 0);

            // Clipboard, caret.
            i.Register(u, "CloseClipboard", CallConv.Stdcall, 0, c => 1);
            i.Register(u, "EmptyClipboard", CallConv.Stdcall, 0, c => 1);
            i.Register(u, "GetClipboardData", CallConv.Stdcall, 1, c => 0);
            i.Register(u, "SetClipboardData", CallConv.Stdcall, 2, c => c.Arg(1));
            i.Register(u, "CountClipboardFormats", CallConv.Stdcall, 0, c => 0);
            i.Register(u, "EnumClipboardFormats", CallConv.Stdcall, 1, c => 0);
            i.Register(u, "GetClipboardSequenceNumber", CallConv.Stdcall, 0, c => 1);
            i.Register(u, "GetClipboardOwner", CallConv.Stdcall, 0, c => 0);
            i.Register(u, "CreateCaret", CallConv.Stdcall, 4, c => 1);
            i.Register(u, "DestroyCaret", CallConv.Stdcall, 0, c => 1);
            i.Register(u, "ShowCaret", CallConv.Stdcall, 1, c => 1);
            i.Register(u, "HideCaret", CallConv.Stdcall, 1, c => 1);
            i.Register(u, "SetCaretPos", CallConv.Stdcall, 2, c => 1);
            i.Register(u, "GetCaretPos", CallConv.Stdcall, 1, c => { memory.Write64(c.Arg(0), 0); return 1; });
            i.Register(u, "GetCaretBlinkTime", CallConv.Stdcall, 0, c => (uint)caretBlink);
            i.Register(u, "SetCaretBlinkTime", CallConv.Stdcall, 1, c => { caretBlink = (int)c.Arg(0); return 1; });

            // Input.
            i.Register(u, "GetDoubleClickTime", CallConv.Stdcall, 0, c => 500);
            i.Register(u, "SetDoubleClickTime", CallConv.Stdcall, 1, c => 1);
            i.Register(u, "SwapMouseButton", CallConv.Stdcall, 1, c => 0);
            i.Register(u, "GetLastInputInfo", CallConv.Stdcall, 1, c => { memory.Write32(c.Arg(0) + 4, (uint)Milliseconds); return 1; });
            i.Register(u, "SendInput", CallConv.Stdcall, 3, c => c.Arg(0));
            i.Register(u, "keybd_event", CallConv.Stdcall, 4, c => 0);
            i.Register(u, "mouse_event", CallConv.Stdcall, 5, c => 0);
            i.Register(u, "BlockInput", CallConv.Stdcall, 1, c => 1);
            i.Register(u, "OemKeyScan", CallConv.Stdcall, 1, c => 0xFFFFFFFF);
            i.Register(u, "GetKBCodePage", CallConv.Stdcall, 0, c => 1252);
            i.Register(u, "ActivateKeyboardLayout", CallConv.Stdcall, 2, c => 0x04090409);
            i.Register(u, "RegisterHotKey", CallConv.Stdcall, 4, c => 1);
            i.Register(u, "UnregisterHotKey", CallConv.Stdcall, 2, c => 1);
            i.Register(u, "GetMouseMovePointsEx", CallConv.Stdcall, 5, c => 0xFFFFFFFF);
            i.Register(u, "GetCursorInfo", CallConv.Stdcall, 1, c =>
            {
                Input.CursorPosition(out var x, out var y);
                memory.Write32(c.Arg(0) + 4, cursorCount >= 0 ? 1u : 0u);   // CURSOR_SHOWING
                memory.Write32(c.Arg(0) + 8, cursor);
                memory.Write32(c.Arg(0) + 12, (uint)x);
                memory.Write32(c.Arg(0) + 16, (uint)y);
                return 1;
            });

            // Icons and cursors: handles only.
            i.Register(u, "CreateIconIndirect", CallConv.Stdcall, 1, c => NewUserHandle());
            i.Register(u, "CreateIcon", CallConv.Stdcall, 7, c => NewUserHandle());
            i.Register(u, "CreateCursor", CallConv.Stdcall, 7, c => NewUserHandle());
            i.Register(u, "CreateIconFromResource", CallConv.Stdcall, 4, c => NewUserHandle());
            i.Register(u, "CreateIconFromResourceEx", CallConv.Stdcall, 7, c => NewUserHandle());
            i.Register(u, "LookupIconIdFromDirectory", CallConv.Stdcall, 2, c => 1);
            i.Register(u, "LookupIconIdFromDirectoryEx", CallConv.Stdcall, 5, c => 1);
            i.Register(u, "CopyIcon", CallConv.Stdcall, 1, c => c.Arg(0));
            i.Register(u, "CopyImage", CallConv.Stdcall, 5, c => c.Arg(0));
            i.Register(u, "SetSystemCursor", CallConv.Stdcall, 2, c => 1);
            i.Register(u, "GetIconInfo", CallConv.Stdcall, 2, c =>
            {
                memory.WriteBytes(c.Arg(1), new byte[20]);
                memory.Write32(c.Arg(1), 1);
                memory.Write32(c.Arg(1) + 12, defaultBitmap);
                return 1;
            });

            // Desktops and window stations.
            i.Register(u, "GetProcessWindowStation", CallConv.Stdcall, 0, c => 0x00E00004);
            i.Register(u, "GetThreadDesktop", CallConv.Stdcall, 1, c => 0x00E00008);
            i.Register(u, "OpenInputDesktop", CallConv.Stdcall, 3, c => 0x00E00008);
            i.Register(u, "OpenDesktopA", CallConv.Stdcall, 4, c => 0x00E00008);
            i.Register(u, "OpenDesktopW", CallConv.Stdcall, 4, c => 0x00E00008);
            i.Register(u, "CloseDesktop", CallConv.Stdcall, 1, c => 1);
            i.Register(u, "SetThreadDesktop", CallConv.Stdcall, 1, c => 1);
            i.Register(u, "SwitchDesktop", CallConv.Stdcall, 1, c => 1);
            i.Register(u, "ExitWindowsEx", CallConv.Stdcall, 2, c => 0);
            i.Register(u, "LockWorkStation", CallConv.Stdcall, 0, c => 0);
        }

        private static char CharArg(uint value, bool wide) => wide ? (char)value : Ansi.Decode(new[] { (byte)value })[0];

        private int[] ReadRect(uint p) => new[] { (int)memory.Read32(p), (int)memory.Read32(p + 4), (int)memory.Read32(p + 8), (int)memory.Read32(p + 12) };

        private uint MapBuffer(uint text, uint count, bool wide, bool upper)
        {
            var s = wide ? memory.ReadUnicode(text, (int)count) : Ansi.Decode(memory.ReadBytes(text, (int)count));
            s = upper ? s.ToUpperInvariant() : s.ToLowerInvariant();
            if (wide) for (var n = 0; n < s.Length; n++) memory.Write16(text + (uint)n * 2, s[n]);
            else memory.WriteBytes(text, Ansi.Encode(s));
            return count;
        }

        private static uint VkKeyScan(char ch)
        {
            if (ch >= 'a' && ch <= 'z') return (uint)(ch - 32);
            if (ch >= 'A' && ch <= 'Z') return 0x100u | ch;   // with shift
            if (ch >= '0' && ch <= '9') return ch;
            switch (ch)
            {
                case ' ': return 0x20;
                case '\r': return 0x0D;
                case '\t': return 0x09;
                case '\b': return 0x08;
                default: return 0xFFFF;
            }
        }

        private uint ModalDialog()
        {
            // No dialogs on the console: the game carries on as if its OK button were pressed.
            Say("x86: modal dialog answered IDOK");
            return 1;
        }

        /// <summary>LoadString: RT_STRING blocks of sixteen length-prefixed UTF-16 strings.</summary>
        private uint LoadString(uint module, uint id, uint buffer, uint size, bool wide)
        {
            var entry = FindResource(module, 6, (id >> 4) + 1, true);
            if (entry == 0)
            {
                if (buffer != 0 && size > 0) WriteText(buffer, "", wide);
                return 0;
            }
            var at = ModuleOrMain(module) + memory.Read32(entry);
            for (var n = 0; n < (id & 15); n++) at += 2 + memory.Read16(at) * 2u;
            var length = memory.Read16(at);
            if (wide && size == 0)
            {
                // The W form with no buffer size hands back a pointer to the resource itself.
                memory.Write32(buffer, at + 2);
                return length;
            }
            if (buffer == 0 || size == 0) return 0;
            var text = memory.ReadUnicode(at + 2, length);
            return CopyTruncated(text, buffer, size, wide);
        }

        private uint LoadBitmap(uint module, uint name, bool wide)
        {
            if (module == 0) return defaultBitmap;   // an OBM_ system bitmap
            var entry = FindResource(module, 2, name, wide);
            if (entry == 0) return 0;
            var info = ModuleOrMain(module) + memory.Read32(entry);
            var bpp = memory.Read16(info + 14);
            var colors = memory.Read32(info + 32);
            if (colors == 0 && bpp <= 8) colors = 1u << bpp;
            var masks = memory.Read32(info + 16) == 3 ? 12u : 0u;   // BI_BITFIELDS
            var bits = info + memory.Read32(info) + masks + colors * 4;
            var handle = NewBitmap((int)memory.Read32(info + 4), Math.Abs((int)memory.Read32(info + 8)), 32, 0, false, null);
            if (gdiObjects.TryGetValue(handle, out var o) && o.Bitmap != null) CopyDibIntoBitmap(o.Bitmap, 0, 0, bits, info, 0, int.MaxValue);
            return handle;
        }

        private uint DrawText(uint hdc, uint textPtr, int count, uint rect, uint format, bool wide)
        {
            const uint CalcRect = 0x400, SingleLine = 0x20;
            var text = count < 0 ? ReadText(textPtr, wide) : ReadCount(textPtr, (uint)count, wide);
            FontCell(DcFont(hdc), out var height, out _, out _, out _, out _);
            var lines = (format & SingleLine) != 0 ? new[] { text } : text.Replace("\r\n", "\n").Split('\n');
            var width = 0;
            foreach (var line in lines) width = Math.Max(width, Extent(hdc, line));
            if ((format & CalcRect) != 0 && rect != 0)
            {
                var r = ReadRect(rect);
                WriteRect(rect, r[0], r[1], r[0] + width, r[1] + height * lines.Length);
            }
            return (uint)(height * lines.Length);
        }

        private uint FindWindow(uint parent, uint after, uint className, uint title, bool wide)
        {
            var wantedClass = className == 0 ? null : className < 0x10000 ? null : ReadText(className, wide);
            var wantedTitle = title == 0 ? null : ReadText(title, wide);
            var passed = after == 0;
            foreach (var win in windows.Values)
            {
                if (!passed) { passed = win.Handle == after; continue; }
                if (win.Parent != parent && !(parent == 0 && win.Parent == 0)) continue;
                if (className != 0 && className < 0x10000 && win.Class?.Atom != className) continue;
                if (wantedClass != null && !string.Equals(win.Class?.Name, wantedClass, StringComparison.OrdinalIgnoreCase)) continue;
                if (wantedTitle != null && win.Text != wantedTitle) continue;
                return win.Handle;
            }
            return 0;
        }

        private uint EnumWindows(uint callback, uint parameter, Func<Window, bool> which)
        {
            var list = new List<uint>();
            foreach (var win in windows.Values) if (which(win)) list.Add(win.Handle);
            foreach (var handle in list)
                if (CallGuest(callback, handle, parameter) == 0) break;
            return 1;
        }
    }
}
