# `uwp/Kiosk/` — every file directly in the app folder

Part of [../ARCHITECTURE.md](../ARCHITECTURE.md). This covers the files
directly under `uwp/Kiosk/` — the UI and app-level state. The translation
layer (`Native/`, `Steam/`) is a separate map:
[kiosk-native.md](kiosk-native.md).

## Entry point and navigation

`App.xaml.cs` → `OnLaunched()` → full-screen mode, core-window bounds set,
root `Frame` created → navigates to `MainPage`.

The dock (library/shop/emulators/friends/mods/downloads) lives on `MainPage`
and is visible everywhere; the other pages are reached from it and return to
it:

- **MainPage** (the hub) — dock buttons switch screens *inside* MainPage
  (library shelf, all-games grid, emulator shop, downloads) without leaving
  the page.
- **MainPage → SteamPage** — the "shop" dock button.
- **MainPage → GamePage** — clicking a tile (game detail, install/launch/play/uninstall).
- **GamePage / SteamPage → MainPage** — back button or Esc/B.

## Screens (XAML pages)

| File | What it does |
| --- | --- |
| `MainPage.xaml` / `.xaml.cs` | The home hub: dock navigation, the game shelf, the All-games grid, the Downloads and Emulator-shop screens |
| `GamePage.xaml` / `.xaml.cs` | Game detail: art, play time, download-location picker, install/launch/play/uninstall |
| `SteamPage.xaml` / `.xaml.cs` | Steam library browser (filters, search, collections/shelves) and QR sign-in |
| `MainPage.Setup.cs` | The Setup dialog: input mode (PC mouse vs. native controller), pointer sensitivity, diagnostics toggle |
| `MainPage.Diagnostics.cs` | The in-game overlay: frame rates, memory, GPU/CPU info, input counters, audio status |

## App-level state and settings

| File | What it does |
| --- | --- |
| `Settings.cs` | Persistent settings: download root, input mode, pointer sensitivity, diagnostics visibility |
| `Updater.cs` | Checks the project's GitHub releases and installs a newer build (`PackageManager`, not the Device Portal — see [DIAGNOSTICS.md](../DIAGNOSTICS.md)) |
| `Texts.cs` | The two-language (pt/en) string catalog every screen reads from |
| `Tile.cs` | One installable/launchable tile: title, subtitle, protocol, art, download progress |
| `EvenWrapPanel.cs` | Layout panel: wraps children into equal-width columns (the emulator/all-games grids) |
| `Tokens.xaml` | Design tokens — colors, radii, fonts, spacing — on a single 1920×1080 canvas that scales to the TV |

## Downloads, updates and storage

| File | What it does |
| --- | --- |
| `DownloadManager.cs` | Tracks a download across page navigation (it used to die when the page that started it closed) |
| `GameStorage.cs` | Every place a game can live (console storage, a USB drive), free space, and which one a download should default to |
| `EmulatorShop.cs` | Installs catalogue emulators from inside the app: downloads the release, unpacks a zip if needed, hands the package to `PackageManager` |

## Steam library (app-level, not the `Steam/` protocol layer)

| File | What it does |
| --- | --- |
| `SteamLibrary.cs` | Fetches and caches the signed-in account's game library |
| `SteamSession.cs` | Persists the Steam session (tokens, account) to local storage |
| `SteamAuth.cs` | The QR sign-in flow (challenge/response; no password ever typed or transmitted) |
| `SteamShelves.cs` | Reads the account's own Steam collections (synced in from the Mac as `shelf.json`) |
| `OwnedGame.cs` | One game in the library: AppID, name, play time, "tested" flag, download state |

## Platform bridge

| File | What it does |
| --- | --- |
| `ConsolePortal.cs` | The app's own client for the console's Device Portal (used for the parts that still work from in-process; see [DIAGNOSTICS.md](../DIAGNOSTICS.md) for what does not) |
| `MemoryProbe.cs` | On-demand (`memtest.txt`): commits 128 MiB blocks until refused, to measure the real memory ceiling versus the reported one |
| `DriveProbe.cs` | On-demand (`probe-drives.txt`): lists drive letters and USB devices with capacity |
