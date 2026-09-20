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
