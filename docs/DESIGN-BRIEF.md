# Design brief: screens for an Xbox game hub app

You are being asked to design screens only. Everything about how this app
looks — color, typography, layout, spacing, iconography, motion, density,
mood — is entirely your decision. This brief intentionally contains none of
that. It only describes what the app is, who uses it, what each screen has to
let someone do, and what information has to be on it for that to be possible.
Where the brief is silent on how something looks, that silence is deliberate.

## 1. What this app is, and who uses it

This app runs on an Xbox Series X that has been switched into the console's
official Developer Mode. In that mode the console stops being a locked box
and becomes a general-purpose x86-64 PC that happens to run Windows and speak
DirectX — because that is what it always was under the hood. The app turns
that machine into a general gaming PC:

- It runs actual PC games **natively on the console's own processor** —
  downloaded by the console itself from the owner's own Steam account, and
  executed as native x86-64 code. This is not game streaming, not a remote
  desktop, and not emulation of the PC games themselves.
- It also ships a shelf of pre-configured console emulators (PlayStation,
  GameCube/Wii, Dreamcast, PSP, Xbox 360, and many more through a
  multi-system core), so classic console games run alongside the native PC
  games in the same place.
- It is one single, unified app. There is no separate launcher for Steam
  games and a different one for emulators — a person should never have to
  think about which "half" of the app a given game lives in.

**Who uses it, and the two facts that constrain every screen you design:**

One person, sitting on a sofa, roughly ten feet (three meters) from a
television, holding an Xbox controller. There is no desk, no mouse, no
keyboard, and the screen is far enough away that phone-app or desktop-app
assumptions about text size, click targets, and information density do not
transfer. Treat "ten feet, one controller" as a hard constraint on every
screen, not a style preference.

The owner's own description of the goal, in his words: he wants this to feel
like a dedicated games console operating system — plug in a drive and it
works — rather than a computer he has to configure. He explicitly compares
the bar he wants to **EmuDeck**: "you just add the games and that's it."
Nothing about signing in, downloading, installing an emulator, supplying a
BIOS file, or launching something already-installed should require him to
leave this app, open a settings menu he has to hunt for, or already know how
game emulation works.

## 2. Screens needed

For each screen below: its purpose, what the person is trying to accomplish,
what information has to be visible for them to accomplish it, and what they
can do from there. Nothing here is a layout instruction — treat it as a list
of ingredients a screen must contain, not a floor plan.

### 2.1 Library (home)

The screen the app opens to. It is the single place that mixes everything
installed and everything owned: native PC games run through the console's own
engine loader, console games run through an emulator, and Steam library
entries that are not yet installed. The person's goal here is almost always
"find the thing I want to play and start it," so browsing, filtering and
launching all have to be possible without leaving this screen.

Needs to show: every item (native PC game, emulator-run game, Steam game
whether installed or not), which of those is currently installed vs. just
owned, a way to search, a way to filter (the person's own Steam collections
and filters carry over here — he has already built collections like
"Favorites" and "Hidden" on Steam and expects them reflected, not rebuilt), a
way to mark something as a favorite or hide it, a way to hide an entire
collection, and a badge on each item showing whether it has been tested and
confirmed working on this kind of console (and on which Xbox model —
this project tracks Xbox One and Series X|S separately). Recently played
should be easy to get back to.

From here: launch an installed item directly; open a game's own detail page
(2.5) for anything not yet installed or to see more before playing; jump to
Steam sign-in if signed out; jump to the emulator shelf, settings, or the
downloads/intake screens.

### 2.2 First-run setup

Shown once, the first time the app runs on a fresh install. The goal is that
a person with a console and a hard drive reaches a working library with the
smallest number of decisions possible — this screen exists specifically so
nothing later has to double as an unexpected setup step.

Needs to cover: choosing a language, choosing where games install by default
(the console's internal storage vs. an external drive, if more than one
target exists), and an invitation to sign in to Steam (which can also be
skipped and done later from the library or settings). Nothing here should
require typing more than unavoidable — this app has no keyboard and no mouse
(see section 3).

From here: finishing lands on the library (2.1), empty and signed out if
Steam sign-in was skipped.

### 2.3 Sign in to Steam

The person's goal is to attach their own Steam account so their library,
purchases and social graph become available on the console. Sign-in happens
by QR code: the console displays a code, the person scans it with their phone
and approves the sign-in there, and the console's screen has to reflect the
wait and the outcome without the person ever typing a password or a
verification code on the console itself.

Needs to show: the QR code itself, a plain-language explanation of what to do
with it, and live status while waiting (waiting for scan, waiting for approval
on the phone, success, or failure/expired with a way to get a new code).

From here: on success, go to the library (now populated) or back to wherever
sign-in was triggered from; on failure or expiry, offer a fresh code without
extra steps.

### 2.4 Sign out

The person's goal is to detach the currently signed-in Steam account from the
console — for themselves, or to hand the console to someone else. This should
state plainly what leaves with the sign-out (the library view, friends list,
achievements tied to the account) and what stays (already-installed games and
emulator content are not deleted by signing out).

Needs to show: which account is currently signed in, and a clear confirmation
before completing the sign-out (this is a state change the person should not
trigger by accident with a stray controller press).

From here: back to a signed-out library, or straight into sign-in (2.3) for a
different account. The owner has also asked for multiple accounts, SteamOS-style,
as a later goal — it is worth this screen (and 2.3) being designed in a way
that does not have to be redesigned when a second account is added, even
though only one account needs to work today.

### 2.5 Steam library browser

Distinct from the mixed home library (2.1) in scope: this is specifically the
person's own Steam account's catalog — everything they own directly and
everything shared with them through Steam Family — before deciding what to
install. The goal is finding a specific game to install, or browsing for
something to try.

Needs to show: the full owned + family-shared catalog (not a partial or
sample list), the account's real collections exactly as the person organized
them on Steam, the account's existing filters, and search. Each entry should
indicate whether it is already installed on the console.

From here: open a game's detail page (2.6) for anything.

### 2.6 A game's own detail page

Shown before launching a specific game — the person's goal is either to start
playing something already installed, or to decide to install something that
is not. This is the single most information-dense screen games have, modeled
on the level of detail Steam itself gives a game (the owner sent screenshots
of Steam's own game page as the reference for completeness of information,
not for how it looks): artwork/branding for the game, an install or play
action depending on state, last time played, total time played, an
achievements summary (e.g. "9 of 33 unlocked" with a way to see the full list
in 2.8), and a menu of secondary actions.

Needs to show: install state (not installed / installing / installed),
playtime and last-played, achievement progress, and — when installing — where
the game will be installed, with a sensible default and the ability for the
person to choose a different install location (a second drive, a development
folder, wherever is available), the same way Steam itself asks.

From here: install (kicks off 2.7), play (launches the game directly),
uninstall, open the full achievements list (2.8), mark as favorite, hide,
manage which collections it belongs to, and — if the install came from a
non-Steam source (2.11–2.13) — whatever is relevant there (choosing a mod for
it, for instance, via 2.14).

### 2.7 Download and install progress

The person's goal is to know a download is actually happening and roughly
when it will be done, and — since more than one thing can be queued — to
manage more than one job. Downloads and installs happen on the console
itself, from Steam's own content servers for Steam games, or from the
relevant emulator/mod/intake source otherwise.

Needs to show: per-job progress (bytes done vs. total, current speed, time
remaining), the state of each job (queued, downloading, installing, done,
failed), and — for a job not yet started — the chosen install location if
that was not already fixed on the previous screen. Several jobs need to be
visible and manageable at once: pausing one, reordering the queue, and
canceling a job all have to be possible, and the queue's state has to survive
the app being closed and reopened.

From here: jump to a finished game's detail page or straight into play; retry
or dismiss a failed job.

### 2.8 Achievements

The person's goal is either bragging-rights browsing of what they have
already unlocked, or checking what is left to do in a specific game. This is
a per-game screen reached from the game's detail page (2.6).

Needs to show: every achievement for the game, which are unlocked and which
are not, and — for unlocked ones — when they were unlocked. Locked
achievements that Steam itself would keep hidden (spoiler achievements)
should stay hidden here too.

### 2.9 Friends list and inviting into a game

The person's goal is social: seeing who from their Steam friends list is
online right now, what they are playing, and pulling a specific friend into
whatever the person is currently playing.

Needs to show: the friends list with real-time online/offline/away status,
what each online friend is currently playing (when they are playing
something), and an invite action reachable both from this list and from
inside/around an active game session, so inviting someone does not require
backing all the way out to a menu.

From here: send a game invite to a specific friend; this covers the owner's
explicit ask for feature parity with Steam on "seeing who's online" and
"inviting a friend into your game."

### 2.10 The emulator shelf

The person's goal is browsing what console systems are available and getting
into a specific one's own game list. Each emulator here is pre-configured out
of the box — controller mapping and any required BIOS/key file are meant to
already be sorted out by the time a person reaches this screen, per the
plug-and-play goal in section 5.

Needs to show: every installed emulator/system, and for each one, whether it
is ready to play (fully configured) or missing something it needs (most
commonly a BIOS file — see 2.12) with a way to resolve that from right here.
Selecting a system needs to lead into that system's own list of games.

From here: open a specific system's game list; add games to a system (2.11);
resolve a missing requirement (2.12); reach the source/install management
screen (2.15) to add a system that is not installed yet.

### 2.11 Adding games to an emulator

The person's goal is simple in principle and has to stay simple in practice:
they have game files (ROMs, ISOs, or similar) on a drive, and they want the
app to find them and make them playable without the person manually sorting
files into folders per system. This directly answers the owner's plug-and-play
priority — "just add the games and that's it."

Needs to show: where the app is looking (or let the person point it at a
drive/folder once), what it found and recognized, and — for anything it
cannot confidently identify — a way to say which system it belongs to.

### 2.12 Supplying a BIOS or key file

The person's goal is getting a system from "installed but not ready" to
"ready to play" when that system needs a BIOS, firmware, or key file the app
cannot legally ship itself. This is not a settings screen buried in an
emulator's own configuration — the whole point is that the person never opens
an individual emulator's settings.

Needs to cover two paths, and the screen should make it obvious which one
applies to a given missing file:

1. **The person already has the file**, on a USB stick, an internal disk, or
   somewhere on the network — they point the app at it (or the drive is
   already attached) and the app figures out what the file is and files it
   into the right place for the right emulator on its own, by reading the
   file's contents rather than trusting a filename.
2. **The person needs to get the file off their own console/hardware.** For
   systems where that applies, this is a guided, step-by-step walkthrough —
   the person follows along and ends by handing the resulting file to the app
   the same way as path 1. (See `docs/BIOS.md` for the full, per-system
   breakdown of which systems need this at all — most of the catalog needs
   nothing here — and what each guided path involves.)

Needs to show, regardless of path: which specific file is missing, for which
system, in plain terms (not just an internal filename), and confirmation once
a supplied file was recognized and accepted — or a clear explanation when a
supplied file was not the right one.

### 2.13 Adding a download by magnet link, .torrent file, or Telegram

The person's goal is handing the app a game they already have a source for —
a magnet link, a `.torrent` file, or something from Telegram — and having it
end up filed and playable, the same as anything installed any other way. (See
`docs/INTAKE.md` for the underlying job queue this screen is the front end
of.)

Needs to show: a way to hand over each of the three input types, and then the
job in the same download/progress view as everything else (2.7) once it
starts moving.

### 2.14 Choosing which console a download is for

A specific, necessary follow-up to 2.13 (and to 2.12's own file recognition):
when a downloaded file's format does not make it obvious which system it
belongs to, the person is asked directly rather than the app guessing wrong
and filing it somewhere that will not run it. When the app does have a guess,
it should be offered as the obvious first choice rather than making the
person hunt for it in a long list.

Needs to show: what was downloaded (enough detail to recognize it — a name,
a size), the app's best guess if it has one, and the full list of supported
systems to choose from otherwise.

### 2.15 Mod hub

The person's goal is finding and installing mods for something they already
have installed — this applies to both native PC games and emulator-run games,
and the owner has asked for both to be covered by the same hub rather than two
separate ones.

Needs to show: mods available for a specific installed game/system, which of
those are already installed, and enough about each mod (what it changes, who
made it) for the person to decide whether they want it.

From here: install, remove, and — where a mod has options — configure it.

### 2.16 App and emulator sources (installing, updating, icons)

The person's goal is managing what is available in the app at all — this is
where new emulators, or new versions of ones already installed, come from,
and it is explicitly meant to feel like an independent app store/source
system (the owner's own reference point is Cydia): third parties can publish
their own sources, the person adds a source, and everything in it becomes
installable from here without ever leaving the app.

This screen is also where the icon and general appearance of an installed
item within the app can be changed or resized, and it is the mechanism behind
a specific requirement: **everything has to open from within this app, no
matter what**, replacing the case today where some installed items only open
from the console's own separate developer home screen.

Needs to show: the list of configured sources, what each one offers, what is
installed vs. available vs. has an update pending, and per-item actions
(install, update, remove, change how it is presented in this app).

### 2.17 Settings

The catch-all for account- and app-level configuration that is not tied to a
single game or a single moment in a flow: language, the default location new
downloads install to (with per-download override already covered in 2.6/2.7),
and a way to reach help — the owner wants a visible path from inside the app
to the project's own GitHub repository, framed as an invitation to
contribute, not a hidden "about" screen. A future multiple-accounts feature
(SteamOS-style) belongs here once it exists.

## 3. Interaction facts — design constraints, not preferences

- **The only input device is an Xbox controller.** D-pad and analog sticks for
  navigation, the four face buttons (A/B/X/Y), the bumpers (LB/RB) and
  triggers (LT/RT) for actions and shortcuts. Every single thing on every
  screen — including anything that looks like a text field — has to be
  reachable and operable this way.
- **There is no mouse and no touchscreen anywhere in this product.**
- **There is no physical or hardware keyboard.** Whenever text entry is
  genuinely unavoidable, the only input method is an on-screen keyboard driven
  by the same controller.
- The viewing distance is roughly ten feet, on a television, not a monitor or
  a phone held in the hand. Whatever this implies for how much can be read
  from that distance is your call to make as the designer — it is stated here
  as a fact about the product, not a request for a particular text size.

## 4. States every screen has to handle

For each screen above, design for all of the following where they apply, not
only the "everything is working and populated" case:

- **Empty.** Nothing installed yet, no search results, no friends online, no
  achievements unlocked yet, a queue with nothing in it, a mod hub with
  nothing installed for this game yet.
- **Loading.** Data that has been requested but has not arrived yet — the
  Steam library on first load, a game's achievement list, artwork that has
  not finished fetching.
- **Error.** A request failed, a file the person supplied was not what was
  expected, a download failed partway through, a source could not be reached.
  The owner has specifically asked that an error be shown full-screen rather
  than as a small inline message — treat that as a requirement for how
  errors are presented, not merely mentioned somewhere on the page.
- **Offline.** The console has no network connection. Screens that depend on
  live data (the Steam library, friends' online status, sources, downloads)
  need an honest state for this rather than silently showing stale or blank
  data.
- **Signed out.** Anything that depends on a Steam account (the library
  beyond what is already installed, friends, achievements tied to the
  account) needs a state that makes it obvious why it is unavailable and
  offers the way to sign in (2.3), rather than just appearing broken or empty
  for no visible reason.

## 5. What success looks like

In the project owner's own words, translated from his notes: he wants this to
be **maximally plug-and-play and as intuitive as possible** — "like EmuDeck:
you just add the games and that's it." He does not want to have to configure
a controller, hunt for a BIOS file's correct folder, or open an individual
emulator's own settings screen for anything this app's own screens are
supposed to cover.

He has also set an explicit bar of feature parity with Steam itself:
achievements, seeing which friends are online, inviting a friend into your
own game, and signing out — "everything Steam can do has to be possible in
our app too." The screens in section 2 are what makes that bar reachable;
none of them exist to look impressive, they exist because leaving one out
would mean something a person can do on Steam has no equivalent here.

## For the designer

Everything about how these screens actually look is your decision to make: no
color, palette, typography, iconography, layout, spacing, density, or motion
direction has been specified anywhere in this brief, and that is intentional.
Design the screens listed in section 2, honoring the content each one needs
to show and the actions each one needs to offer, the input constraints in
section 3, and the states in section 4 — and make every visual and
art-direction choice yourself.
