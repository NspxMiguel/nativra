# Helping out

This is a console that was sold as a locked box, running PC games because
someone asked it nicely. Most of what is left to do is small and specific, and
a lot of it can be done without owning an Xbox at all.

## Where the work is

Three kinds, and the issues are labelled that way.

**`good first issue`** — self-contained, no console needed. A missing Win32
function to write, a language to translate the interface into, a screen that
does not lay out properly at 960x540. Each one comes with what it should
answer and how to tell that it worked.

**`needs a console`** — you have an Xbox in developer mode and can run the
cycle. These are measurement tasks: does this game start, which function does
it die in, what does the trace say. Every one of them makes the compatibility
list longer, which is the point of the whole project.

**`hard`** — a subsystem. Audio, the shader cache, the parts of the window
system a game touches when it is doing something unusual. These are left open
on purpose; they are interesting, and nobody should have to ask permission to
work on something interesting.

## The shape of the thing

The app is a UWP package. Inside it, a loader maps ordinary Windows binaries
into the process — mapping, relocations, imports, exception tables, thread
local storage — and then answers the calls those binaries make. Most of them
are answered by the console's own Windows, because an Xbox *is* Windows NT.
The rest are written by hand, and that is the list that needs people.

    uwp/Kiosk/Native/PeImage.cs        maps a binary and makes it runnable
    uwp/Kiosk/Native/SystemImports.cs  decides where each import points
    uwp/Kiosk/Native/WindowStubs.cs    the window system a console does not have
    uwp/Kiosk/Native/GraphicsBridge.cs swap chains, via the console's own window
    uwp/Kiosk/Native/PadBridge.cs      XInput over the console's gamepad
    src/                               the command line tool that drives all this

## Writing a missing function

Run a game. The report names every function it reached that nobody had written,
in the order it needed them. Pick one, find out what it is supposed to do, and
write it the way the others are written: a delegate, held in a field so the
collector cannot take it, installed into `Overrides`.

A function that returns a constant goes in `Answers` instead, which costs
nothing at runtime. A function that fills in memory has to be written properly —
a game lays itself out from what it is told, and a plausible lie about a
rectangle becomes a game rendering into the wrong half of the screen.

## Translating

Every string the player sees is a key in `uwp/Kiosk/Texts.cs` and
`src/i18n.ts`. Adding a language is adding a table. Nothing else has to change.

## Rules

Code, comments, commit messages and pull requests in English. The interface is
translated, the source is not.

No license bypass, no piracy, nothing that works by pretending to own a game.
Issues asking for it will be closed.
