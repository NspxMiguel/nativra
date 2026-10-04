namespace Nativra.X86.Loader
{
    // Services the console does not offer to a 32-bit game, answered the way
    // Windows answers when they are unavailable, so a game that probes for them
    // takes its fallback instead of stopping at an unserved import.
    public sealed partial class GuestKernel
    {
        private void InstallAbsent(GuestImports i)
        {
            // A fallback for runs without a packaged dinput8.dll (such as the Mac runner).
            // DirectInput8Create(hinst, version, riid, ppvOut, punkOuter).
            i.Register("dinput8.dll", "DirectInput8Create", CallConv.Stdcall, 5, c =>
            {
                const uint DierrNotInitialized = 0x80070015;
                if (c.Arg(3) != 0) memory.Write32(c.Arg(3), 0);
                return DierrNotInitialized;
            });

            // WinHTTP: no session can be opened, as on a machine without the
            // service; every later call sees an invalid handle.
            const string w = "winhttp.dll";
            const uint ErrorWinHttpInternal = 12004;
            uint Fail() { process.LastError = ErrorWinHttpInternal; return 0; }
            i.Register(w, "WinHttpOpen", CallConv.Stdcall, 5, c => Fail());
            i.Register(w, "WinHttpConnect", CallConv.Stdcall, 4, c => Fail());
            i.Register(w, "WinHttpOpenRequest", CallConv.Stdcall, 7, c => Fail());
            i.Register(w, "WinHttpAddRequestHeaders", CallConv.Stdcall, 4, c => Fail());
            i.Register(w, "WinHttpSendRequest", CallConv.Stdcall, 7, c => Fail());
            i.Register(w, "WinHttpReceiveResponse", CallConv.Stdcall, 2, c => Fail());
            i.Register(w, "WinHttpQueryHeaders", CallConv.Stdcall, 6, c => Fail());
            i.Register(w, "WinHttpQueryDataAvailable", CallConv.Stdcall, 2, c => Fail());
            i.Register(w, "WinHttpReadData", CallConv.Stdcall, 4, c => Fail());
            i.Register(w, "WinHttpSetTimeouts", CallConv.Stdcall, 5, c => Fail());
            i.Register(w, "WinHttpSetStatusCallback", CallConv.Stdcall, 4, c => 0xFFFFFFFF);   // WINHTTP_INVALID_STATUS_CALLBACK
            i.Register(w, "WinHttpCloseHandle", CallConv.Stdcall, 1, c => 1);
        }
    }
}
