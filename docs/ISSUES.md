# The work, partitioned

Drafts for the issues to open. Each one says what done looks like, because
"help wanted" without an acceptance test is just a wish.

## good first issue — no console needed

1. **Translate the interface into Spanish.** `uwp/Kiosk/Texts.cs` and
   `src/i18n.ts`. Done when every key has a value and nothing falls back.
2. **Translate into French, German, Japanese, Simplified Chinese.** One issue
   each, same shape.
3. **`winmm` — the 23 functions a game asks a multimedia timer for.**
   `timeGetTime`, `timeBeginPeriod`, `timeSetEvent` and the rest. Done when a
   game that imports winmm no longer has a single recording stub in its report.
4. **`imm32` — the 8 input-method functions.** They are all "no composition is
   happening"; getting them right is reading the documentation carefully.
5. **`version` — `GetFileVersionInfoW` and friends.** A game asks its own
   version. Answer from the file it was loaded from.
6. **A download queue.** Right now one download happens and the screen shows
   it. Done when several can be queued, paused and reordered, and the state
   survives the app closing.
7. **First-run setup.** Language, where games install, sign in. Done when a
   fresh install walks someone through it without a manual.
8. **Compatibility badges.** Each game shows whether it has been tested and on
   which console. Done when the badge comes from data, not a hand-edited list.

## needs a console

9. **Test a game and report it.** Any Steam game you own. Run it, attach the
   report, say what happened. This is the single most useful thing anyone can
   do, and it needs no code at all.
10. **Repackage gzdoom, openbor and raze with a URI protocol** so they open
    from the app instead of from Dev Home.
11. **Measure the frame rate honestly** once something renders: a counter that
    reads the swap chain's statistics rather than guessing.

## hard — and open on purpose

12. **Audio.** A game reaches for WASAPI through `IMMDeviceEnumerator`, which
    a packaged app does not have; the console has `ActivateAudioInterfaceAsync`
    instead. Same shape of problem as the swap chain, same kind of answer.
13. **The rest of the window system.** 114 functions from `USER32`, of which a
    handful matter and the rest are constants. The report says which.
14. **Cydia-style sources.** A repository format anyone can host, a reader in
    the app, and installing straight onto the console from it.
15. **Emulators as data, not packages.** A downloaded emulator should be a
    thing the app runs, not a new app on the console — the way a Minecraft
    world is not a new copy of Minecraft.
16. **A ProtonDB-shaped site.** Per-game reports, per-console badges, fed by
    what people send from issue 9.

## The display problem, measured

Worth writing down before anyone spends a night on it:

- The game runs. Six million calls, its own message loop turning, **60.3 frames
  a second** measured at `Present`. Direct3D 11 comes up at feature level 11_0
  with the console's adapter, and the swap chain is created through the bridge.
- **The composition panel on this console does not implement
  `ISwapChainPanelNative`, nor version 2 of it.** Both return `E_NOINTERFACE`,
  asked by hand on the raw pointer. `IInspectable` on the same pointer answers
  `S_OK`, which rules out the managed runtime being at fault: the object is the
  panel, and the panel does not offer the interface.
- **`CreateSwapChainForCoreWindow` is refused** with `DXGI_ERROR_INVALID_CALL`
  in every combination of scaling, swap effect and alpha mode, with and without
  the application releasing its own content first.

So the frames are copied back to ordinary memory and shown as a picture, which
is the one route that asks the platform for nothing. It costs a read back from
the card and two screen-sized copies per frame. A better answer almost
certainly exists and would be a very welcome contribution.

## Known gaps, written down so nobody rediscovers them

- **`D3D11CreateDeviceAndSwapChain`** is not bridged. Engines that use it hand
  a window handle to Direct3D in one call, and that call cannot be redirected
  the way the factory can. The fix is to split it: create the device with
  `D3D11CreateDevice`, then reach the factory through the device and make the
  chain the same way `GraphicsBridge` already does.
- **Direct3D 9 does not exist on this console.** Games older than about 2012
  link `d3d9.dll`, and there is nothing to forward to. Translating D3D9 onto
  D3D11 is a project in itself — it is what DXVK does on Linux — and it is the
  single biggest thing standing between this and an old catalogue.
- **`steam_api64.dll`.** Most Steam games load it and quit if it fails. The
  app already signs in as the player and speaks the Steam protocol, so the
  honest answer is to implement the interface against that real session rather
  than to fake ownership. Nothing here will ever pretend a game is owned.
