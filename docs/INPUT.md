# Input and setup (pre-alpha)

The home screen's **Setup** button configures the initial input mode, virtual
pointer speed (0.25×–2×), and the runtime diagnostic panel. These preferences
share `LocalState/settings.json` with the download destination. Back up that file
before uninstalling a development build. Normal package data is not preserved
by uninstalling the app.

## Two exclusive modes

- **PC mode** maps the controller to mouse and keyboard input. The right stick
  moves the visible pointer; A clicks/fires, X is right-click, the left stick
  and D-pad produce movement keys, and Y produces Space. XInput reports the
  controller as disconnected in this mode to avoid duplicate game actions.
- **Native controller mode** exposes connected UWP gamepads through the XInput
  bridge and stops synthesizing mouse/keyboard events from their buttons.
  The game must support that input path. A mode label is not proof that a game
  has detected the controller.

Hold **View + Menu** together for at least 0.35 seconds to switch. This is
Nativra's shortcut, not a claim about an official SteamOS binding. Holding it
does not repeatedly toggle. Both buttons must be released before another
switch. Their shortcut events are consumed rather than sent as Enter/Escape.
Individual View/Menu presses are briefly deferred to distinguish the chord.

Keyboard input already delivered to the host remains separate from gamepad
mapping. Full physical mouse/keyboard coverage, native gamepad hot-plug behavior,
multiple-controller shortcuts and per-game remapping are not yet validated.

## Validation

Run the platform-independent chord behavior checks on a machine with .NET 8:

```sh
dotnet run --project tools/controller-mode-tests/ControllerMode.Tests.csproj
bun test
```

The source-wiring tests do not replace Xbox testing. On the console, check:

1. Opening a game immediately displays loading feedback and ignores duplicate
   launch requests.
2. In PC mode, move the cursor onto an actual menu button, then press A.
3. Switch modes while a mapped action is held; the action must release.
4. Verify native input only in a game that actually polls the bridge.
5. Hide diagnostics in Setup; the virtual cursor and mode hint must remain.
6. Change pointer speed, restart the app and verify the saved setting.

Device Portal input tests exercise the host-event path. They do not certify
physical controller behavior by themselves. The owner's report that Seraph's
menu did not respond remains an open physical-controller validation item.

## Console checks on September 23, 2026

Build 200 displayed the loading acknowledgement, cursor and active-mode hint.
The remote View+Menu chord switched modes without opening the game's pause menu.
In PC mode, pointing at Seraph's Start button and clicking entered a match;
subsequent movement and firing were observed in console screenshots.

Build 203 made Setup reachable with D-pad Up from the shelf and A. Its controls
saved PC mode, 0.25× pointer speed and the diagnostic visibility setting.
In build 202, hiding diagnostics survived an app restart while keeping the
cursor and mode hint visible. Reinstallation into build 203 also restored the
backed-up preference file. These were Device Portal input tests, not physical
controller certification. Native XInput gameplay remains unverified.

Build 207 was tested through combat, an upgrade selection and the second wave.
Remote movement, jump, firing and the upgrade click responded. After defeat,
the Retry button did not respond even though frames and raw-input reads kept
advancing. Unity logged two `NullReferenceException` messages without call
stacks after Game Over. Cursor-warp requests remained at zero throughout this
test, so the SetCursorPos correction does not explain the Retry failure.
This remains an incomplete gameplay flow, not a fully playable certification.
