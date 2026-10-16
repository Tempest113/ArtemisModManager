# Artemis design

Status: agreed in discussion, 2026-10-13. No code exists yet. Each choice has a record in [DECISIONS.md](DECISIONS.md);
`(D06)` below points to it.

## What it is

**Artemis** is a Stellaris mod manager (playsets, load order, conflicts, launching) built to replace Irony for daily
use: faster to start, faster to scan, and visually modern. It also manages **native mods**: ordinary mod folders that
carry a DLL. **Orion**, a small loader injected through a `winmm.dll` proxy, loads those DLLs inside the game so mods can
add logic the script language can't express (new triggers and effects, localisation commands, UI behaviour).

The manager is the main product and gets the design care. The loader is deliberately small.

**Terminology (D26):** user-facing text says **plugin**, not "native". Below, "native mod" / "native code" (from the
earlier decision records) means a mod that carries a plugin.

## Goals and non-goals

Goals: a manager that starts and scans fast; native mods that behave like script mods (enable, order, per playset);
native code that is always visible and never loads silently; a loader that fails safe on unknown game builds.

Non-goals (v1): multiplayer, GOG/Xbox/Linux, Workshop-hosted DLLs, a conflict-resolution editor, auto-sorting, other
Paradox games, shipping first-party plugins, a C# plugin host.

## How the pieces fit

```
Artemis (manager, WPF)                      Stellaris process
  playsets (JSON)                             stellaris.exe
  index cache (disposable)                      winmm.dll  (stub, forwards winmm, loads Orion)
  trust store, settings, overrides  --files-->    Orion core: reads dlc_load.json, modloader.json,
  writes dlc_load.json at launch                  trust store, settings; checks hashes; loads plugins;
  <-- status.json, per-plugin logs --             contributes the native-mod fingerprint (honest mode)
```

The game's `dlc_load.json` (`{"disabled_dlcs":[...],"enabled_mods":["mod/name.mod",...]}`) is the single list of what is
enabled. Whoever wrote it last wins (Launcher, Irony, Artemis). Orion reads it too, so native mods follow script mods
whichever tool started the game (D07). Artemis never needs to be running while the game runs.

## The manager

- **Layout (D29, D30):** Vortex-style neutral greys with one blue accent and our own controls (no UI library). A left
  column holds the playset menu, the Launch split button and page navigation; the main panel holds Irony's two panes as separate
  cards: Installed (flat or grouped by source) and Load order, with the library collapsible to a thin strip. Real mod
  thumbnails, a supported-version column, find-next in the load order, type-a-position to move, drag a row anywhere to reorder.
  Dark only. Mockups: `docs/design/ui-vortex-direction.html`.
- **Playsets (D18):** JSON files owned by the manager; export and import as files; read-only import from the Paradox
  Launcher database and from Irony. Applied at launch. Drift detection if another tool changed `dlc_load.json`.
- **Launching (D18):** writes `dlc_load.json`, starts `stellaris.exe` directly. Optional launch via Steam/Launcher.
- **Mod list (D13, D32, D36):** MO2-style separators in the load order (suggested or custom names, visual only, never
  assigned automatically); no categories or tags. A details drawer per mod. Outdated `supported_version` and a mod loading
  above one its descriptor lists (an ordering hint, not a requirement, D36) are flagged on the row. Per-mod notes and "new or updated" markers are built (D37). Later: a per-playset switch for a mod's plugin part.
- **Discovery and speed (D09):** scan mod folders directly. Persistent per-mod index in a disposable cache (built, D38);
  the window never waits for it, and only changed mods are read again in the background. Irony baseline (author's report): about 10 s to open,
  minutes to scan small playsets. Working targets are in D27. The art is never read: the author's 33 Workshop mods are
  12.7 GB but only about 5,000 text files, and the index lists everything else by size and time only.
- **Conflicts (D10, built: D39):** whole files, same-named top-level definitions, localisation keys; mod-vs-mod and mod-vs-vanilla;
  says who wins and why. Folder-by-folder override rules follow the community wiki table (not documented by Paradox, may
  change), as the Stellaris Mod Loader scanner did.
- **Achievement badge (D22):** a mod is achievement-compatible when none of its files match the rules in the game's own
  `checksum_manifest.txt` (read from the install at runtime). Shown as a trophy (or a cracked one) beside each mod in both lists (D57); a playset is compatible only if every enabled mod is.
- **Native mods (D04–D06, D16, D17):** trust approvals, schema-rendered settings per playset, per-plugin status from the
  last session, install from archive, Create Mod scaffolding.
- **Safety (D24):** first-run wizard, crash guard, "launch without native mods", one-click full restore.
- **Logs (D17, D56):** the manager's log, Orion's log, per-plugin logs, and the game's own logs, on one page; the chosen log follows new lines live, and "clean up" sends clear leftovers to the Recycle Bin.

## The in-game side

- **Stub (D14):** `winmm.dll` next to `stellaris.exe`. Forwards every real `winmm` export, loads Orion, does nothing
  else. Recognised by its version-resource company name (starts with "Artemis"); a foreign `winmm.dll`, including the old
  POC's or the Stellaris Mod Loader's, is never overwritten. Stays installed once the loader is enabled. The manager installs,
  repairs, updates and removes it, and Orion with it (Plugins page, D51); on a game build with no verified data the user can
  allow plugins for that exact build.
- **Orion (D12, D50):** plugin host, address resolver, trust check; file hooks and registry access come with Step 5. Built
  (`orion/`, C++, no third-party code); it installs no hooks of its own and, with nothing trusted to load, the game runs as vanilla.
- **Plugin load (D06, D07):** read `dlc_load.json`, resolve each `.mod` to its folder, find `modloader.json`, check
  build support and dependencies, hash the DLL, compare with the trust store, load from a content-addressed copy in list
  order, record the outcome in `status.json`.
- **Plugin API (D11, D21):** C ABI, one integer version, additive within a major. Tiers: stable (new script keywords,
  localisation commands: engine registries and file reads) and fragile (hooks on specific engine functions, opt-in,
  re-validated every patch). Memory crossing the boundary is allocated and freed through loader-provided functions.
- **Builds (D15):** address data per game build, matched on the exe's PE timestamp and size, loaded from a data folder.
  Unknown build means vanilla passthrough and "unsupported build" in the manager.
- **Honest mode (D22, D52, D53):** native mods contribute id, version and hash to a virtual file inside the checksummed set, so
  multiplayer mismatches show at join and achievements go off as for script mods. `cosmetic` plugins opt out. Built: Orion mounts
  the file through the game's exported PhysFS after the game has mounted its mods (confirmed in the real game, D52).

## `modloader.json`

Id, name, version, required API version, DLL files, supported builds, dependencies and incompatibilities, provided
script keywords, feature toggles, settings schema, stability tier, `cosmetic`. Schema 1 is written down in D48, with the other files
Artemis and Orion exchange (`trust.json`, `plugin-settings.json`, `status.json`, `loading.json`); the manager side is built (D49).
The manager reads it without loading the DLL.

## Files and locations

| What | Where | Written by |
|---|---|---|
| Playsets, settings, trust store, index cache | `%APPDATA%\Artemis` | manager |
| Orion core, pointer file, build data, plugin copies | `%APPDATA%\Artemis\orion\<version>\orion.dll`, `current.txt`, `builds\<id>.json`, `cache\` (D50) | manager (core, pointer); tools (build data); Orion (cache) |
| `winmm.dll` stub | game folder | manager (game closed) |
| `dlc_load.json`, original backup | Documents `Paradox Interactive\Stellaris` | manager (backup once) |
| `trust.json`, `plugin-settings.json` | `%APPDATA%\Artemis` (Orion finds it through `ARTEMIS_DATA_DIR`) | manager |
| `status.json`, `loading.json`, plugin logs, Orion log | Documents `...\Stellaris\modloader` | Orion |
| Per-build address data | data folder, git-ignored | tools |

## What to know (plain English)

1. A DLL mod is a program, not data. It runs with the user's full permissions. Hence hash trust, and the badge.
2. Every Stellaris patch can break engine-level mods. The loader finds internals by address and a patch moves them. The
   failure is safe (mods don't load) but native mods stay off until the addresses are remapped. A public tool is on call
   for each patch. Stable-tier plugins are far less affected.
3. The game can't tell it's modded unless we tell it (honest mode). Other multiplayer players won't have the DLL. Saves
   made with native keywords may not load without them. v1 is singleplayer-only.
4. Windows and antivirus distrust software that injects into games. A Defender heuristic already blocked a test build
   that linked an inline-hook library. A public release needs a code-signing certificate and false-positive reports.
5. Paradox hasn't blessed this. The approach (no exe edits, no game files in the repo) is the lowest-risk way, but it is
   a gray area. Stay non-commercial, honour takedowns, get a legal read before a public release.
6. A running game can't swap a DLL. Plugin changes need a restart.

## Open and unverified

- In-game test that Orion's fingerprint file changes the checksum (D22). That the checksum gates achievements is confirmed (D43).
- What the game and the Paradox Launcher do to a stub left in the game folder across updates and Steam file verification;
  what the Launcher writes to `dlc_load.json` on a Steam start; whether the game rewrites it at boot (affects drift
  detection).
- What the game does with an unknown keyword when a plugin is missing (expected: `error.log` entries, not a crash).
- Whether Paradox's uploader or rules allow DLLs on the Workshop. Deliberately not tested (D05).
- Whether address data may ever be published (D15); legal read; code signing and false positives.
- Orion has only been run inside a stand-in game (D50). Whether plugin init under the loader lock behaves in the real game is untested.
- Working names, not final: the fingerprint file path, dependency version-range semantics, the codename overlap check (D20).

## Reference material (read-only, kept outside this repo)

- **Stellaris DLL Shenanigans:** `src/proxy.cpp`, `tools/gen_proxy.py` (forwarding `winmm`), `src/main.cpp` (import-table
  file hooks), `src/generator.cpp` (playset and `.mod` parsing in C++, the starting point for Orion's parser).
- **Stellaris Mod Loader:** `docs/PLAN.md` findings log (keyword registries are `std::map<int, Entry*>` inside lazily
  created databases; `RegisterEffectEntry`/`RegisterTriggerEntry` are find-or-insert; new keywords need a token as well as
  an entry), `tools/port_addresses.py`, `tests/dll_harness`, and in `manager/src/SML.Core`: `DlcLoad`, `LauncherImporter`,
  `PdxScript`, `ConflictScanner` and the rule table in `manager/README.md`, `LoaderInstaller` (ownership check),
  `CompatChecker`. Its `research/` folder holds local-only, game-derived address data that must not be copied here.
- **Exception Translator:** per-build function data and crash analysis, used for address porting and later for mapping
  crashes to plugins.

Reuse means reading and rewriting with intent (D01), not copying files across.
