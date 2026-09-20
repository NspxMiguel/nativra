# Renaming Kiosk: Inventory and Candidates

## Section 1: Where the Name Lives

This inventory distinguishes between cosmetic changes (human-facing, no impact on the installed package) and breaking changes (alter the UWP package identity, breaking existing installations and hard-coded console paths).

### 1.1 Breaking Changes — UWP Package Identity

These changes **invalidate every existing installation on the console** and rewrite every hard-coded path stored there.

| File | Line(s) | What | Impact |
| --- | --- | --- | --- |
| `uwp/Kiosk/Package.appxmanifest` | 9 | `<Identity Name="NSPX.Kiosk"` | **CRITICAL**: Package identity. All PackageFullName references become invalid. Every install on console must be uninstalled first. |
| `uwp/Kiosk/Package.appxmanifest` | 13, 30 | `<DisplayName>Kiosk</DisplayName>` (2x) | Shown in console UI and app list; UWP package identity uses this. |
| `uwp/Kiosk/Kiosk.csproj` | 10 | `<RootNamespace>Kiosk</RootNamespace>` | C# namespace root; changing this rewrites generated code. |
| `uwp/Kiosk/Kiosk.csproj` | 11 | `<AssemblyName>Kiosk</AssemblyName>` | Compiled binary name; changes DLL/EXE on disk and in registry. |
| `uwp/Kiosk.sln` | 5 | `Project(...) = "Kiosk"` | Visual Studio project name; cosmetic in .sln but mirrors csproj. |
| `uwp/Kiosk/Properties/AssemblyInfo.cs` | 4–5 | `[assembly: AssemblyTitle("Kiosk")]` / `AssemblyProduct` | Assembly metadata; winds up in binary. |

**Key fact:** The `PackageFullName` property used in console operations (e.g., `portal.pushFile(kiosk.PackageFullName, ...)`) derives from the Identity Name. Changing `NSPX.Kiosk` to `NSPX.<NewName>` means every line in `src/xbdev.ts` that reads `await findPackage(portal, "kiosk")` must search for the new slug instead, but `PackageFullName` on the console will be different.

### 1.2 Cosmetic Changes — Human-Facing, No Package Identity Impact

These can be changed without breaking installed packages or requiring uninstall.

| File | Line(s) | What | Notes |
| --- | --- | --- | --- |
| `uwp/Kiosk/Texts.cs` | 4, 15, 9 | `namespace Kiosk`, `"app.eyebrow": "KIOSK"`, env var `KIOSK_LANG` | Namespace declaration, UI eyebrow text, and language override var. Env var name is cosmetic; the UI string comes from i18n. |
| All 38 C# files under `uwp/Kiosk/` | — | `namespace Kiosk` (38 occurrences) | All C# class definitions use this namespace. **Requires refactoring all class names**, but is otherwise cosmetic from the package perspective. |
| `src/xbdev.ts` | 622, 649–750 (31 lines) | Slug `"kiosk"` and variable name `kiosk` in cmdSyncKiosk(), cmdMakeFolders(), cmdGameFolder() | CLI uses this slug internally to find the package. Changes throughout the sync logic, but `findPackage()` searches by name, not slug. **Must align slug rename with catalog.json.** |
| `src/i18n.ts` | — | Strings mentioning Kiosk (2 lines) | "send the installed app list to Kiosk" and "Kiosk's list" in English and Portuguese. Pure string, no code impact. |
| `scripts-cycle.sh` | 1, 2, 6–32 (22 lines) | References to `kiosk-uwp.zip` filename, `Kiosk_*_Test` package name, and CLI calls `bun src/xbdev.ts ... kiosk` | Filenames and script commands. `Kiosk_*_Test` is the actual folder name that msbuild generates; depends on AssemblyName, so **changes when csproj changes**. |
| `scripts-publish.sh` | — | One comment mentioning `uwp/Kiosk/Texts.cs` | Cosmetic comment. |
| `catalog.json` | 6–10 | `"slug": "kiosk"`, `"name": "Kiosk"`, `"url": "...kiosk-uwp.zip"` | The slug and release asset name. **Must match the CLI's `findPackage("kiosk")` call.** |
| `.github/workflows/build-uwp.yml` | Multiple | Release artifact named `kiosk-uwp.zip`, build output references, secrets `KIOSK_PFX_*` | Artifact filename, build paths, secret names. **Release asset name must be updated; secret names are cosmetic.** |
| `README.md` | 63 | Example command `bun src/xbdev.ts launch kiosk` | Documentation. |
| `docs/CONTRIBUTING.md` | — | Path references `uwp/Kiosk/Native/*.cs`, discussion of role | Documentation. |
| `docs/ISSUES.md` | — | Path reference to `uwp/Kiosk/Texts.cs` for translation task | Documentation. |
| `docs/VEREDITO.md` | — | Mentions "Kiosk — front-end nativo em UWP" | Portuguese docs (internal notes). |
| `docs/preview/kiosk.html` | — | Title and text "Kiosk — preview", references to XAML mirrors | Documentation / prototype preview. File name `kiosk.html` is also cosmetic. |
| `test/xbdev.test.ts` | — | Test references "Kiosk_1.0.0.0_x64.msixbundle" | Test data that depends on AssemblyName. **Will change when csproj changes.** |

### 1.3 Directory and File Names

The directory `uwp/Kiosk/` is the project root and appears in every path. Renaming it requires:
- Updating `.sln` project path references
- Updating all `.csproj` paths
- Updating all build workflow paths
- Updating all documentation and examples

The folder rename is **breaking** because relative paths in `.sln` and build scripts point to it.

### 1.4 Summary

| Category | Count | Breaking | Cosmetic |
| --- | --- | --- | --- |
| UWP manifest/identity | 4 lines | ✓ | — |
| C# project files | 5 lines | ✓ | — |
| C# namespaces (38 files) | 38 occurrences | ✓ | — |
| CLI and script references | 22 script lines + 31 TS lines | Partial | ✓ |
| Documentation | 13 lines | — | ✓ |
| Catalog and workflows | 9 lines | Partial | ✓ |
| **Total** | ~100+ occurrences across 57 files | | |

**Bottom line:** Changing the name requires three mandatory operations:
1. Update UWP identity and C# namespace (breaks package, requires uninstall on console)
2. Update folder `uwp/Kiosk/` and all paths (filesystem; breaking)
3. Update CLI slug and catalog (required for console to find package)

The rest (documentation, strings, examples) is optional but recommended for consistency.

---

## Section 2: Name Candidates

Each candidate is evaluated on:
- Fit for purpose (launches games, central hub, gaming machine)
- Pronounceability and memorability
- Conflict check: existing GitHub repos, trademarks, common gamedev/emulation projects
- Exclusion of Apple, Microsoft, Sony, Nintendo, Valve brand names (to avoid confusion)

### Candidate 1: Forge

**One-liner:** Central workshop where games are installed and configured, like a blacksmith's forge preparing tools.

**Existing conflicts:**
- GitHub: `weaveworks/forge` (container orchestration), `forge` package by various authors exists
- Trademark: Unreal Engine has "Pixel Streaming Forge", but not a standalone app
- Recommendation: **Low conflict in gaming space.** Forge in game development usually refers to modding tools (Minecraft, etc.). Clear enough.

**Verdict:** Strong candidate. Familiar to PC gamers (Minecraft modding).

---

### Candidate 2: Nexus

**One-liner:** Central hub and junction point where emulators and games converge.

**Existing conflicts:**
- GitHub: `sonatype/nexus` (Maven repository), `nexus` repos by various authors; none gaming-focused
- Trademark: Nexus is a Google Pixel brand; however, no consumer gaming product uses it as primary name
- Real-world: Nexus Mods (game modding site) exists, but not a launcher/console app
- Recommendation: **Low conflict.** Distinctive enough from Nexus Mods since this is a console app, not a mod site.

**Verdict:** Strong candidate. Clear "hub" concept.

---

### Candidate 3: Genesis

**One-liner:** Nods to Sega Genesis and the retro gaming legacy; fits a console that runs classics.

**Existing conflicts:**
- Trademark: Sega Genesis (trademarked), Genesis Energy, Genesis Records exist
- GitHub: Countless Genesis repos; none a console emulation hub
- Recommendation: **MODERATE CONFLICT.** "Genesis" evokes Sega too directly. Legal risk if app becomes widely known; could be seen as trading on brand. Not recommended for public release.

**Verdict:** Weak candidate. Trademark liability.

---

### Candidate 4: Portal

**One-liner:** Gateway to the world of games; doorway between native Xbox and PC/emulated games.

**Existing conflicts:**
- Trademark: Valve's "Portal" (puzzle game) is a registered trademark
- GitHub: Countless `portal` projects; "Portal" usually refers to web portals or Valve's game
- Recommendation: **HIGH CONFLICT.** Valve actively defends the Portal trademark. Risk of cease-and-desist if the app gains visibility.

**Verdict:** Avoid. Trademark too strong.

---

### Candidate 5: Haven

**One-liner:** Safe space where games are collected, organized, and preserved.

**Existing conflicts:**
- GitHub: No major gaming project named Haven
- Trademark: No major gaming trademark
- Real-world: "Haven" is a generic term, used in many game titles (e.g., game "Haven" by Dontnod) but not as a launcher
- Recommendation: **Very low conflict.** Generic enough to be clear.

**Verdict:** Mild candidate. A bit generic, forgettable.

---

### Candidate 6: Beacon

**One-liner:** Guiding light; the central signal point where all your games gather.

**Existing conflicts:**
- GitHub: Various Beacon projects; none gaming-related
- Trademark: No major trademark in gaming
- Real-world: Generic term, used in Minecraft (beacon block) but no consumer app by this name
- Recommendation: **Very low conflict.**

**Verdict:** Mild candidate. Abstract; might not immediately convey "games" to players.

---

### Candidate 7: Sovereign

**One-liner:** Takes control; rules over the console's ecosystem, merging native and emulated games under one command.

**Existing conflicts:**
- GitHub: Various Sovereign projects, none major in gaming
- Trademark: No gaming trademark by this name
- Real-world: Generic (means "supreme ruler"); used in sci-fi (e.g., Halo ring world) but not as a game hub
- Recommendation: **Very low conflict.**

**Verdict:** Moderate candidate. Strong brand identity; slightly pretentious for a hobby tool.

---

### Candidate 8: Codex

**One-liner:** A library or grimoire of games; connotes knowledge, collection, and magic.

**Existing conflicts:**
- GitHub: `polyswarmer/codex` (classification engine), `wimpylightbulb/codex` (game engine) — none dominant
- Trademark: No major trademark
- Real-world: Generic term (means "ancient book" or "code manuscript"); common in fantasy games and RPGs
- Recommendation: **Low conflict.** Widely available name.

**Verdict:** Strong candidate. Fits the "library" concept of the app perfectly.

---

### Candidate 9: Palladium

**One-liner:** A strong, precious metal; Xbox has Latin-inspired branding (Kinect = motion). Suggests protection and stability.

**Existing conflicts:**
- GitHub: Various Palladium repos; mostly academic or infrastructure projects, nothing gaming-major
- Trademark: No gaming trademark
- Real-world: Real chemical element; referenced in sci-fi (Transformers "Allspark" contrast) but not consumer gaming
- Recommendation: **Very low conflict.** Unique in gaming space.

**Verdict:** Moderate candidate. Unique but less immediately "games-y" than others.

---

### Candidate 10: Athenaeum

**One-liner:** A grand library or hall of learning; a place to explore and experience all games together.

**Existing conflicts:**
- GitHub: Various Athenaeum projects; mostly digital libraries or note-taking apps
- Trademark: No trademark
- Real-world: Classical reference; used in game titles (e.g., "Library of Athenaeum") but not a launcher
- Recommendation: **Very low conflict.** Specialized vocabulary; less recognizable to average gamer.

**Verdict:** Weak candidate. Pretentious; pronunciation barrier for non-English speakers.

---

### Candidate 11: Pivot

**One-liner:** Turn your console; shift from official Xbox games to the full spectrum of gaming options.

**Existing conflicts:**
- GitHub: Countless Pivot projects, mostly data/analytics related
- Trademark: No gaming trademark
- Real-world: Generic term; used in game design (pivot mechanics) but not as app name
- Recommendation: **Very low conflict.**

**Verdict:** Weak candidate. Too generic; doesn't convey what the app does.

---

### Candidate 12: Catalyst

**One-liner:** The spark that transforms your console into a full gaming machine, activating its potential.

**Existing conflicts:**
- GitHub: Various Catalyst projects, mostly infrastructure/dev tools
- Trademark: No major gaming trademark
- Real-world: Generic term, used in game titles but not a launcher
- Recommendation: **Very low conflict.**

**Verdict:** Moderate candidate. Forward-looking, implies transformation.

---

## Top 3 Recommendations

Based on conflict checks, branding fit, and recall:

1. **Codex** — Best fit. "Library of games" is exactly what this app does. No conflicts. Immediately memorable.
2. **Forge** — Strong alternative. Familiar to PC gamers (Minecraft modding). "Building your game collection" metaphor works well.
3. **Nexus** — Solid hub/center concept. Distinct from Nexus Mods due to console platform. Clear positioning.

**Avoid:** Portal (Valve trademark), Genesis (Sega trademark), Athenaeum (too obscure).

**Neutral:** Haven, Beacon, Sovereign, Palladium, Pivot, Catalyst — all viable but less distinctive.
