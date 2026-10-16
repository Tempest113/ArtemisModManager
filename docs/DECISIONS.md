# Decision records

Decided in the design discussion of 2026-10-13. Each record: the decision, why, and what it costs. To change one, add a
new record that supersedes it instead of editing history. Open items are in `DESIGN.md` ("Open and unverified").

**D01 Fresh repo, full rewrite.** Artemis starts clean. `Stellaris DLL Shenanigans` and `Stellaris Mod Loader` are
read-only references; code is ported deliberately, nothing is inherited by default.
*Why:* both are proofs of concept. *Cost:* some re-solving; the research is reused, not the structure.

**D02 Personal first, public-minded.** The first user is the author; choices should not block a public release, even if
that later needs partial rewrites. Scope: Windows, Steam, Stellaris 4.5.x; the current build plus the previous one.
*Cost:* GOG, Xbox, Linux/Proton are out for now.

**D03 Manager stack.** C#, .NET 10, WPF, with a UI-free core library so the UI can change later.
*Why:* the loader is Windows-only, so cross-platform UI buys little now; WPF virtualizes long lists well; "outdated" is a
styling problem. *Cost:* UI is Windows-only.

**D04 DLL mods are ordinary mod folders.** A folder with `descriptor.mod`, a `modloader.json` and the DLL(s), enabled and
ordered in the playset like a script mod. One mod can ship script content and native code together.

**D05 Distribution: no Workshop.** DLL mods ship as archives (GitHub, Nexus) and are installed by the manager's "Install
from archive". Native-only mods (descriptor + `modloader.json` + DLL, no script content) are first-class. A script mod on
the Workshop plus a separate native companion mod, linked by a dependency, is the supported pattern for Workshop reach.
Workshop-hosted DLLs are neither supported nor tested, and we will not upload test DLLs to the Workshop.
*Why:* no evidence Paradox's uploader or rules allow it; Workshop moderation is active; account risk.

**D06 Trust model.** Trust is per mod, pinned to the DLL's SHA-256. First enable asks once. A changed hash skips that
plugin (the game still launches) and the manager shows a non-blocking "re-approve" badge. Mods in local folders
(outside the Steam Workshop folder) auto-approve their own rebuilds. "Follow updates" is an opt-in per mod, off by
default. The core re-hashes right before loading and loads from a content-addressed copy, so a file can't be swapped
between check and load and rebuilds don't hit file locks.
*Why:* native code has the user's full permissions; nothing can sandbox it, so the goal is no silent changes and
visible native code. *Rejected:* per-update prompts everywhere (fatigue), trust-and-follow (a compromised update loads
silently), signatures and registries (later, for a public release).

**D07 Orion follows the game's own list.** Orion reads `dlc_load.json` (the file the game reads), resolves each `.mod`,
finds `modloader.json`, and loads trusted DLLs in list order. The manager owns the trust store, per-mod settings, a master
switch, per-playset overrides (e.g. "native part of mod X off here") and preflight checks.
*Why:* one source of truth; native mods follow script mods whichever tool wrote the list (Launcher, Irony, Artemis).
*Rejected:* a strict manifest written by the manager at launch. It would make the manager the only working launch path
and keep two lists that must agree. *Cost:* a small JSON and `.mod` parser inside Orion (the old POC already has one);
per-playset settings are written at each manager launch, and other launches use the last written values.

**D08 Manager scope versus Irony.** In v1: two-pane load order, playsets, Launcher import, conflict detection, logs,
create-a-mod, plus native-mod management. Later: Workshop subscribe and download, in-app updater. Never: other Paradox
games, and a conflict-resolution editor or patch generation (Irony has one and it causes more problems than it fixes).

**D09 Speed and discovery.** The pains to fix are slow startup and slow conflict scans. Each mod gets a persistent index
(file list, sizes, modified times, parsed top-level definition names) in a disposable cache. The window paints from the
last session's state and only changed mods rescan in the background; conflicts come from intersecting indexes, not
re-reading files. Playsets, trust and settings stay as plain JSON. The manager scans mod folders itself as the source of
truth; the Paradox Launcher's `mods_registry.json` and database are optional extras (names, IDs, thumbnails), never
required.

**D10 Conflict scope.** Whole-file replacement, same-named top-level definitions and localisation keys; mod-vs-mod and
mod-vs-vanilla; says who wins and why. No value-level diff.

**D11 Plugin language and ABI.** Plugins are C/C++ against a versioned C ABI (one integer version, additive within a
major) with a thin C++ header on top. No C# host inside the game for now.

**D12 Orion is plumbing only.** Proxy, resolver, plugin host, trust check, registry access, file hooks. No first-party
plugins ship yet; `xor`, `num_assigned_workforce` and the megastructure build-speed hook are test plugins only.

**D13 Look and mod list.** Irony-style two panes (installed on the left, active load order on the right) with search
across both, a details drawer, a visible "native code" badge and status chips on rows; dark-first, flat, custom title
bar, light theme later. Features: tags and notes per mod, groups or separators in the load order, search and filters
(name, tag, native, problems, conflict), "new or updated since last launch" markers, a per-playset switch for a mod's
native part. Not: auto-sort (needs community knowledge we don't have), online browsing or ratings.

**D14 Stub and core.** `winmm.dll` in the game folder is a tiny stub: it forwards the real `winmm` exports and loads
Orion from a versioned folder the manager manages, outside the game folder. It stays installed once the loader is
enabled (a Steam or Launcher start must load plugins too). The manager installs or removes it while the game is closed
and updates Orion freely. If Steam's file verification removes the stub, the manager reinstalls it at next launch. With
nothing trusted to load, Orion installs no hooks and the game runs as vanilla.
*Why a stub:* less for antivirus heuristics to flag (Defender already blocked one test build with an inline-hook library).

**D15 Game builds and address data.** Orion touches the engine only on a build with a verified address set; otherwise
it passes through as vanilla and native mods are marked "unsupported build". A per-user override exists for people who
accept the risk. Address data is loaded from a data folder, never compiled in, so how it is delivered can change.
Whether address files are ever published is decided at the public-release gate (they are derived from the game binary).

**D16 `modloader.json` and settings.** Read by the manager without loading the DLL. Declares: id, name, version,
required API version; DLL files; supported builds; dependencies and incompatibilities; script keywords it provides (so
the manager can tell which mods need it); named feature toggles; a typed settings schema (bool, int, float, enum,
string, path, with defaults and ranges); stability tier (D21); the `cosmetic` flag (D22). The manager renders a settings
page from the schema per playset and writes the values at launch. Plugins cannot embed UI in the manager.

**D17 Runtime feedback.** Orion writes `status.json` per session (each plugin: loaded, skipped with a reason such as
untrusted / hash changed / build unsupported / dependency missing, or failed) and a log per plugin. The manager alerts on
next open and groups its Logs page by plugin. No in-game popups in v1. Mapping a crash dump to the faulting plugin comes
later, using the Exception Translator work.

**D18 Playsets and launching.** The manager owns playsets as human-readable JSON files with export and import. Import
from the Paradox Launcher database and from Irony is read-only; the Launcher database is never written. The Launch
button writes `dlc_load.json` and starts `stellaris.exe` directly; "launch via Steam/Launcher" uses the Launcher's own
playset and still loads trusted DLL mods (D07). Changes apply at launch, not live. On open the manager detects that
another tool changed `dlc_load.json` since its last write and asks whether to import or overwrite.

**D19 Repo, license, distribution, defaults.** Monorepo (see README layout), MIT license, portable self-contained zip
first; installer, code signing and auto-update at the public-release gate. English-only UI with strings in resource
files; no telemetry, accounts or phone-home; no auto-update in v1; all data local (app data in `%APPDATA%\Artemis`, logs
and status in `Documents\Paradox Interactive\Stellaris\modloader`). Tests: xUnit for the manager core, a stand-in game
harness for the loader. Decisions live in `docs/`.

**D20 Codename.** Artemis is the repo, manager and product; Orion is the in-game core. Both are common names ("Master of
Orion" is a 4X game): check for overlap before a public release. The stub must stay named `winmm.dll`; the codename goes
in its version resource, which is how the manager recognises its own file.

**D21 Stability tiers.** A plugin declares what kind of engine access it uses. *Stable:* adding script keywords and
localisation commands (survives patches best). *Fragile:* hooking specific engine functions (re-validate every patch).
The manager badges each native mod and, after a game update, warns which fragile mods are unverified.

**D22 Multiplayer, achievements, saves; honest mode.** v1 is singleplayer-only and says so. The multiplayer checksum
covers `.txt`/`.shader`/`.csv` under `common`, `.txt` under `events`, and `.shader`/`.txt` under `map` (from the game's
`checksum_manifest.txt`); `gfx`, `interface` and `localisation` mods leave it unchanged and stay achievement-compatible.
Honest mode: native mods feed a virtual fingerprint file (working name `common/modloader/manifest.txt`: plugin id,
version, hash) into the checksummed set through Orion's file hooks, so multiplayer mismatches are caught at join and
achievements turn off exactly as they do for script mods. Loader code never spoofs a vanilla checksum. A plugin may
declare `"cosmetic": true` (UI-only) to stay out of the fingerprint; the manager shows it as "claims cosmetic" and the
user can override it to count. The manager also shows an achievement-compatible badge per mod and per playset, computed
offline from the game's own `checksum_manifest.txt`, read at runtime and never copied into the repo. A spike (D25)
confirms in-game that the checksum gates achievements and that the fingerprint file changes it; it must pass before any
public release.

**D23 Plugin developer workflow.** Create Mod scaffolds `modloader.json` (a code template later). Local mods auto-trust
their rebuilds (D06). A live session view shows each plugin's status and log tail while the game runs. A native DLL can't
be unloaded safely, so a change needs a game restart; there is no hot reload.

**D24 First run, crash guard, escape hatches.** A first-run wizard: detect Steam libraries (several are normal) and the
Stellaris install with override, detect the Documents folder, offer playset import from the Launcher and Irony, create a
default playset, explain native code in plain words and ask explicit consent before installing the stub. Orion writes a
"loading plugin X" marker; if the game dies during startup, the next manager open flags that plugin as the suspect and
offers to disable it. A "Launch without native mods" button and a one-click full restore (original `dlc_load.json` back,
stub removed) are always available.

**D25 Build order.**
1. Design doc and repo skeleton.
2. Script-mod manager: scan, index cache, playsets, conflicts, launch, achievement badge. Should replace Irony day to day.
3. Orion: stub/core split, plugin host, trust store, status reporting; old experiments return as test plugins.
4. Native-mod features in the manager: trust, settings, status, install from archive.
5. Honest-mode spike and the build/address pipeline.
6. Public-release gate: signing, installer, docs, legal review.

*Why manager first:* it is useful early, and the risky loader work was already de-risked in the Stellaris Mod Loader spikes.

**D26 User-facing term: "plugin".** Replaces "native" in all UI and docs from now on: badge and filter "Plugin", rail
page "Plugins", toggle "Plugin in this playset", "Launch without plugins", "plugin-only mod". One fixed explanation
wherever one is needed: "Runs code inside the game. Only approve plugins you trust." Records D04–D25 keep their original
wording; read "native mod" / "native code" as "plugin mod" / "plugin".
*Rejected:* "code mod" (scripts are code too), "extension" (sounds too safe), "DLL mod" (jargon for newcomers).

**D27 Step 2 order and speed targets.** Vertical slice: M1 core library (no UI) → a bare app shell that lists the real
mods and launches → M2 index and conflicts → M4 the rest (Launcher/Irony import, groups/tags/notes, first-run wizard,
logs, restore). The author reports Irony takes about 10 s to open on their Windows system and minutes to scan even small
playsets. Working targets, to be measured on the author's 50-mod setup: interactive window in under 1 s on a warm start;
the mod list appears immediately on a first-ever start while indexing continues in the background; warm conflict view in
under 1 s; cold full index in under 10 s. Layout: `manager/` with `Artemis.slnx`, `Artemis.Core`, `Artemis.App`,
`Artemis.Core.Tests`; the cache is SQLite via `Microsoft.Data.Sqlite`. Nothing is committed until the manager is usable.

**D28 UI libraries.** The app uses WPF-UI (Fluent dark theme, Mica backdrop, title bar, styled controls) and
CommunityToolkit.Mvvm (view-model plumbing). *Why:* a modern look with little hand-written template XAML, which is the
point of D03/D13. *Cost:* a community dependency for the look. Switching is cheap while the UI is small and gets dearer
with every screen; revisit before the details drawer and conflicts view are built if the dependency feels wrong.

**D29 Custom UI, no UI library (supersedes D28).** WPF-UI is removed. The theme, window frame and controls are our own;
only CommunityToolkit.Mvvm stays (compile-time plumbing). *Why:* fewer dependencies and room for our own identity. *Cost:*
we write the control templates ourselves. Icons come from Windows' built-in symbol font, listed in one file; the UI font
is Segoe UI Variable. Dark only through v1; solid colours, no blur.

**D30 Look and Mods page layout.** After comparing layouts and researching Vortex 2.x, Irony, Mod Organizer 2 and the
Stellaris UI: Vortex's neutral near-black greys with one accent (signal blue `#4c8dff`; red was rejected because it
already means "problem"), a left column (playset picker, Launch split button, page nav) and a raised main panel.
Irony's two panes stay: **Installed** on the left (flat or grouped by source, one-click add) and **Load order** on the right,
with the left pane collapsible so the order can use the full width. Carried over from Irony: a Launch menu (apply without
launching), typing a position number to move a mod, find-next search in the load order (matches are highlighted, rows
are not hidden), a supported-version column with outdated versions flagged. Real mod thumbnails on rows, with a
generated placeholder when a mod has none. The footer stats (conflict count, achievements, scan time) were dropped as
redundant: users need it to feel fast, not to read milliseconds. (Separators were tried without first; see D32.)

**D31 Categories (MO2-style). Superseded by D32.** Categories are created by the user, shared across playsets, several per mod (the first is
the primary one and drives grouping). On first run they are suggested from the mods' Workshop tags. They label rows,
group the Installed list and filter. One level only for now; nesting comes later. Stored in `categories.json` next to the
playsets.

**D32 Separators replace categories (supersedes D31).** Workshop tags are too coarse to describe most mods and we can't
fix that, so categories are gone: no tag chips, no category grouping or filter, no `categories.json`, no seeding from tags.
In their place the load order has MO2-style **separators**: named divider rows the user adds above any mod or at the end
(a few suggested names, "Gameplay", "UI", "Fixes", "Shipsets & portraits", "Compatibility patches", or any custom name),
renames, drags and removes. They are purely visual, stored in the playset next to the mods, never assigned
automatically, never counted in positions, and never written to `dlc_load.json`. Not built: collapsing a separator's group.
The Installed list is flat or grouped by source.

**D33 Window and table polish.** The main panel is rounded on all four corners and inset 12 px from the window's right and
bottom (the top inset is the title bar). A borderless window used to maximise over the taskbar (the last rows were
unreachable); it now maximises to the monitor's usable area. Column dividers and header rules separate the table cells.
The whole load-order row is the drag handle (not its position box or switch). One spacing scale throughout: 4 / 8 / 12 / 16,
with panels, panes and rows inset 12, header blocks 16, and 8 between the two lists. Each list is its own rounded pane
(a darker card) inside the Mods panel. Both panes have the same four rows (title, search, column header, list) so their rules line up. The scroll bar has its own 12 px
column, always reserved, so hover and selection highlights stop before it and headers stay aligned with rows. Mod pictures
are drawn over black, as Steam does, so transparent pixels read as black instead of showing the placeholder underneath.

**D34 Multi-select in the load order, and a real minimum size.** Several load-order rows (mods and separators) can be
selected (Shift/Ctrl-click) and dragged, moved to the top or bottom, or removed together; they keep their relative order,
and dragging up lands just above the drop target, dragging down just below it. Pressing on one row of a selection keeps
the selection until release, so it can be dragged; a plain click then selects just that row. The window's own size hook
(needed to stop maximising over the taskbar) used to swallow WPF's minimum size; it now supplies it (960 x 520).

**D35 Everyday polish round.** (a) Dragging rows shows a drop line and scrolls the list near its edges; Esc cancels. (b) Keyboard:
Delete removes, Space switches mods on/off, F2 renames a separator, Alt+Up/Down moves the selection, Ctrl+F finds in the load
order, Ctrl+Shift+F searches Installed, F3 / Shift+F3 step through matches, Enter adds or removes the selected installed mods,
Alt+Enter toggles the details drawer. (c) Undo and redo for every load order change (Ctrl+Z, Ctrl+Y or Ctrl+Shift+Z, 100 steps,
cleared when the playset is switched); removing mods also shows an Undo button. (d) Empty lists say what to do. The first run
offers a welcome list: the game's own mod list, the Paradox Launcher's playsets and Irony's collections, all read-only (Launcher
via a copy of its SQLite file); "Import from other tools" in the playset menu does the same later. (e) The gap between the panes
is a splitter; window size and position, the split and the collapsed state are remembered in `settings.json`. (f) The Installed
list can be filtered (not in / in this playset, made for an older game version, missing files) and sorted by name or recently
added (the .mod file's date). (g) Faint text was lifted to 5.3:1 contrast; the app declares per-monitor DPI awareness.
(h) The Launch button shows "Game running" and is disabled while stellaris.exe runs. When `dlc_load.json` no longer matches what
Artemis last saw or wrote, a banner offers to import that list as a playset or dismiss it (whoever writes last wins, so this is
the only warning the user gets). (i) Test hooks: `--user-dir` points the app at a scratch Documents folder and
`ARTEMIS_NO_WELCOME=1` skips the first-run dialog, so tests never touch the real files.

**D36 Mod details drawer; descriptor dependencies are ordering hints only.** A drawer over the right edge shows the selected mod:
playset position, source, versions, when it was added, tags, folder, id, the mods its descriptor lists, and buttons to add or
remove it, open its folder and open it on the Workshop. It follows the selection in either list. *A descriptor's
`dependencies` only ask the game to load those mods before this one; they don't mean the mod needs them to work, and
Steam's "required items" are no more reliable (the author's example: the "How to read" mod).* So Artemis never calls a
listed mod missing or required. The drawer lists them under "Asks to load after" with a neutral state (in this playset, not in
this playset, not installed, switched off), and the only warning, on the load order row and in the drawer, is for a listed mod
that is switched on and sits below the mod. Steam data is not read for this and is not to be trusted for it later.
Conflicts and the achievement badge are still the next milestone (D25, D27).

**D37 Leftovers round: playset files, notes, markers, Steam launch, logs.** (a) The playset menu exports the playset to a `.json` file and
imports one; an imported playset always gets a fresh id and a unique name, and lines with no mod key are dropped. (b) A free-text note per
mod, kept in `settings.json` under the mod's id, edited in the details drawer; rows show a note icon whose tooltip is the note. (c) Rows
show "New" or "Updated" since the last launch. At each launch or "save without launching" Artemis snapshots every mod's folder date and
version; the first start seeds the snapshot so nothing is flagged. The `.mod` file's date is not used because the Paradox Launcher rewrites
them all; the folder's date only moves when something directly inside it is added or removed, so a few updates may go unmarked. The
Installed filter has a matching entry. (d) "Launch via Steam" in the Launch menu saves the load order, then opens `steam://rungameid/281990`.
The Paradox Launcher that opens may use its own playset and replace the list (unverified, and the drift banner would show it). (e) A Logs
page lists the game's non-empty `.log` files from `Documents\Paradox Interactive\Stellaris\logs`, error.log first, and shows the last
256 KB of one. Artemis writes no log of its own yet, and Orion's logs come with Orion.

**D38 Index and speed.** The index lives in `%APPDATA%\Artemis\index.db` (SQLite). It is disposable: it carries a format number, and a file that
is damaged or from another format is deleted and built again. It holds one *source* per installed mod folder and one per game build (the
build is the version text plus the exe's size and date; older builds are dropped). For every file: path, size, date. For `common/**.txt` its
top-level names, for `events/**.txt` its event ids, for `localisation*/**.yml` its keys (every language), packed into one compressed blob per
file; everything else (art, sound, models) is listed and never opened. Row-per-name was tried first: 21 s cold and 207 MB, against 2.5 s and
36 MB with blobs. Conflict detection will read a whole source's names at once (`IndexStore.Names`), so nothing is lost by this.
*Incremental:* every update lists each mod's files (sizes and dates only, no file opened), hashes the listing and reads the mod again only if
the hash changed; mods no longer installed are dropped. The game is read once per build. Archive-only (zip) mods are not indexed.
*In the app:* the index starts after the window and the lists are up, uses all cores but one, and shows "Indexing mods… 12 of 50" only while it
is really reading; a failure shows one short line and never blocks anything. The details drawer shows a mod's file and name counts. No separate
cache of the mod list: reading the 50 `.mod` files takes about 20 ms, so there is nothing to paint ahead of.
*Measured* (author's 50 mods: 35,288 files, 6,603 text files; game: 42,969 files; files in the OS cache): window at about 0.6 s, mod list about
0.7 s, warm index check 0.3 s in the background, cold full index 2.5 s, peak memory under 100 MB. Not measured with a cold OS cache. `tools/IndexBench`
repeats the index timing on the real folders, and `ARTEMIS_TIMING=<file>` makes the app log its start-up steps.

**D39 Conflict detection.** Built on the index alone (D38): no mod file is opened. The Conflicts page checks the *switched-on* mods of the active
playset, in load order, and the game. Three kinds. *Files:* the same path in two places (only game content: `common`, `events`, `gfx`,
`interface`, `localisation`... and known file types; loose files at a mod's root, hidden folders and editor leftovers are ignored); the later mod
replaces the earlier file completely, and a mod's file replaces the game's. *Definitions:* the same top-level name (or event id) in the same
folder, counted only in files the game really reads, so a file replaced by a later mod's same-path file takes its names with it. *Text:* the same
localisation key in the same language. Two files of one mod defining the same name are not a conflict. *Who wins:* files of a folder are read in
file-name order (case-insensitive, across mods and the game together) and the last definition wins, except in the first-wins folders (events,
scripted_variables, component_templates and a few more) where the first wins; the same file name settles it by load order, and text in a `replace`
folder beats text elsewhere. This is the community wiki's table, as in D10: Paradox does not document it, so the page says "likely result, not a
promise". Two folders (traits and a few others) are marked as not always following the rule. *Not conflicts:* `on_actions` (merged), `defines`
(merged value by value; an addition to the wiki table, from community knowledge, unverified) and `inline_scripts` (their name is a path, so a clash is
already a file conflict). *Intended:* a conflict is marked probably intended when the winner's descriptor asks to load after every mod it overrides;
this reads the dependency list as the ordering hint it is (D36), never as a requirement. *Page:* kind chips, search, a box to include conflicts of a
single mod with the game itself (off by default; most are text and art), and a details pane listing the entries in load order with Used or
Overridden, file and line, and the reason in a sentence. The left column shows the count of conflicts between mods; the details drawer shows a mod's
own count. It recomputes half a second after any change to the load order, a switch or the index.
*Measured* (author's 16 enabled mods): 4,750 conflicts, 160 between mods (24 files, 106 definitions, 30 text) and 4,590 over the game; 0.6 to 0.75 s,
in the background. Not built: archive (zip) mods, conflicts inside a value, resolving them (D08), and showing a conflict mark on each row.

**D40 Small round: navigation, scrolling, conflict marks, Artemis log.** (a) The mouse's side buttons step back and forward through the pages
last viewed (Mods, Conflicts, Logs), like a browser; a new page visit clears the forward list. (b) A mouse wheel's sideways tilt pans the scroll
view under the pointer, which WPF ignores by itself (the window handles `WM_MOUSEHWHEEL`). (c) The log text box has its own scroll viewer, so the
corner where its two scroll bars meet is no longer drawn white. (d) A mod with conflicts against other mods shows a small mark and its count in
both lists, with a tooltip saying how many it wins; the count is the same one the details drawer shows. (e) Artemis keeps its own log,
`%APPDATA%\Artemis\artemis.log` (about a line per start-up, scan, index update, launch and save, plus every error with its details; set aside as
`.old` once past 1 MB at start-up). It is the first entry on the Logs page. Writing it never throws.

**D41 Wording and polish round (supersedes parts of D39 and D40).** (a) The conflict count on load order and Installed rows (D40 d) is gone: the
Conflicts page and the left column's count already say it; the details drawer keeps a mod's own count. (b) The Conflicts page box "Include the
game's own files" became **Exclude vanilla**, on by default: it hides conflicts where a single mod only replaces something of the game's. (c) The
game is shown as "Stellaris", and the reason for a winner is one short sentence in the engine's words, not the wiki's: "Technology definitions use
last in, only served." (LIOS; "first in" for the first-wins folders), with a second sentence only when the file names match (load order decides),
for a replace folder, or for the folders the game doesn't reliably follow. The footnote that the wiki's rules are not Paradox's was removed: the
rules are still the community wiki's table (D39) and still not confirmed in the game by Artemis; that stays recorded here and in D39, not on screen.
(d) Both mod lists scroll by pixel, not by row, so rows of different heights (separators, group headers) still end flush at the bottom; a wheel notch
moves three rows as before. (e) The game's own logs show one line per entry: the game's messages may contain line breaks of their own, and those
are joined to their entry. Artemis's log is shown as written. (f) The Logs list highlight reaches the box's right edge when there is no scroll bar,
and names sit centred in it. (g) "Find in the load order" is "Search enabled".

**D42 Achievement badge (D22, built).** The game's `checksum_manifest.txt` (a handful of lines: a folder, whether its subfolders count, a file type)
is read from the install when Artemis starts and never copied. A mod "turns achievements off" when its index lists any file the rules cover
(a folder match with the file type, subfolders only if the rule allows them); art, interface and localisation files never do. The left column
shows "Achievements: on" or "Achievements: off (3 mods)" under Launch, for the enabled mods of the playset, with the mods named in the tooltip;
it stays empty until every enabled mod has been indexed. The details drawer says per mod "Not affected" or how many covered files it adds or
changes. If the game has no such list (another build) nothing is shown. *Not confirmed in the game:* that the checksum really gates achievements,
and that Orion's fingerprint file changes it (D22 spike, before any public release).

**D43 Launch variants, from the author's tests (2026-10-15).** (a) *Achievement badge confirmed:* playing with a playset the badge calls "on" and one it calls
"off" behaved as the badge said (D22, D42). The other half of D22 (that Orion's fingerprint file changes the checksum) is still for the Orion spike.
(b) *Launch via Steam does not use the Artemis playset:* after starting through Steam the game's error log named mods that are not in Artemis's list,
so the Paradox Launcher rewrote `dlc_load.json` from its own playset (as D18 said). The menu item is now "Open via Steam (uses the Paradox Launcher's
playset)" and saves nothing first. (c) That same run showed the game exiting right after start-up with exit code 3221226505 (0xC0000409); Windows
logged `stellaris.exe` itself faulting at the same place on every attempt, with the Launcher's playset loaded. Not traced to a cause and
not caused by Artemis's list; Orion's crash-to-mod mapping (D17) is the later tool for this. (d) *Scaling and a second monitor* (125%, 150%, two
monitors) behave as expected. (e) *Continue last save:* the game's exe contains the argument `-continuelastsave`; "Launch and continue the last save"
in the Launch menu starts the game directly with it, after saving the load order as Launch does. Untested in the game when written.

**D44 Launch and Resume go through Steam (supersedes D18's direct start and D43 b, e).** Steam counts playtime only for a game it started itself, and
a game started by Artemis directly was not counted (author's report). So **Launch** now saves the load order and asks Steam to start the game with
`steam://run/281990//<the game's own arguments>`, the way Irony does (read from Irony's program; it was seen to work on the author's machine while the
plain Paradox Launcher failed). **Resume** is the same with `-continuelastsave` added, an argument found in the game's exe; it is enabled only when the
game's `continue_game.json` names a save, and its tooltip says which. The two halves share the old button; the menu keeps "Save the load order without
launching", "Start without Steam (playtime isn't counted)" (the old direct start, as a fallback) and "Restore the original dlc_load.json". The plain
Launcher (D43 b) is no longer opened by Artemis. *Not confirmed:* that the Launcher stays out of the way and leaves Artemis's `dlc_load.json` alone when
Steam starts the game with arguments. If it rewrites the list, the "changed outside Artemis" banner (D35 h) appears. Artemis never writes the
Launcher's database (D18).

**D45 Launch is a direct start with Steam's game-id variables; the Launcher failure was the old winmm.dll (supersedes D44, and D43 b and c).** (a) Starting
through `steam://run/...` made Steam open the Paradox Launcher (its launch entry is `dowser.exe`, and the arguments went to it), which is not what Irony
does. Steam's own logs show what Irony does: `stellaris.exe` started directly, with the game id given in the environment (`SteamAppId`, `SteamGameId`,
`SteamOverlayGameId` = 281990, the names Irony's game handler uses), and Steam then lists that process as the game and counts the playtime.
Artemis now starts `stellaris.exe` the same way (`LaunchService.StartInfo`), after making sure Steam is running, and the game reads Artemis's
`dlc_load.json` because no Launcher is involved. Launch and Resume (`-continuelastsave`, as Irony passes it) both start this way. The menu no longer has a
Steam variant. (b) *The crash and the Launcher failure (D43 c) came from the old proof-of-concept `winmm.dll` loader in the game folder* (found by the
author in another session; it has been moved aside as `winmm.dll.disabled-by-claude`, with the older copy kept as `winmm.dll.poc.bak`). Consequences for
Orion (D12, D14, D15): the stub must do nothing in any process but `stellaris.exe` (the Launcher's `dowser.exe` is in the same folder and loads it too);
it must forward every `winmm` export correctly; and it must not hook anything on a game build it has no verified addresses for, which the old loader did
not guarantee (a loader built for an older build is the likely reason for a fail-fast crash after a patch). Artemis should also notice a `winmm.dll` it
did not install in the game folder and say so in plain words (D24): not built yet, planned with the stub.

**D46 Resume argument corrected; signing plan (2026-10-15).** (a) *Resume:* with `-continuelastsave` (one dash) the game only reached the main menu (author's test, a
fresh save made and resumed). Steam's logs show Irony starting `stellaris.exe -gdpr-compliant --continuelastsave` (two dashes) for sessions of minutes on the
author's machine, so Resume now passes `--continuelastsave`. The single dash found in the exe is not what the game accepts from the command line. (b) *Signing
(Step 6):* the author cannot pay for a certificate, and none is needed to use or share the tool: an unsigned portable zip works, Windows SmartScreen shows a
warning once, and the source is public. Free or cheap routes to look at when releasing, none verified here: SignPath Foundation signs open-source projects
for free (OSI licence, public repo, automated build, a published signing policy, the Foundation becomes the publisher); Azure Artifact Signing is about
$10 a month but, for individuals, only in the US and Canada; false-positive reports to Microsoft Defender are free. Signing matters most for Orion's
`winmm.dll` (an unsigned DLL that loads into a game is what antivirus heuristics distrust), less for the manager. Release unsigned first, with file hashes
published next to the download, and apply for free signing later.

**D47 Manager-first order.** The author wants the script-mod manager feature complete, with its half of the plugin features built, before Orion (Step 3) and the
honest-mode spike (Step 5), so a working manager exists even if Orion hits a roadblock. Step 4 therefore comes before Step 3. The manager defines the files
Orion will read and write (the "Orion contract", D48); Step 3 follows that contract and may change it by a new record.

**D48 The Orion contract, version 0 (manager side; Step 3 follows it and may change it by a new record).** *Where:* Artemis's data folder is `%APPDATA%\Artemis`; every
game start Artemis makes puts its path in the game's environment as `ARTEMIS_DATA_DIR`, and Orion uses that first. `ORION_PLUGINS=off` there means "load no plugin this run".
*Artemis writes* (JSON, UTF-8, written atomically): **`trust.json`** `{ "version": 1, "plugins": { "<mod key>": { "name", "files": { "<path in the mod>": "<sha256 hex, lower case>" },
"followUpdates", "autoApprove", "approvedUtc" } } }`, and **`plugin-settings.json`** `{ "version": 1, "playset": "<id>", "pluginsEnabled": true|false, "mods": { "<mod key>": { "id":
"<plugin id>", "enabled": true|false, "values": { "<setting or feature id>": <bool|number|text> } } } }`, written at every launch or save-without-launching, for the enabled mods of that playset that
have a valid plugin, with every default filled in and out-of-range values already corrected. The mod key is the name of the mod's `.mod` file without the extension, the same entry as in
`dlc_load.json`. *Orion writes* into `Documents\Paradox Interactive\Stellaris\modloader\`: **`status.json`** `{ "version": 1, "orion", "build", "startedUtc" (ISO 8601, UTC), "plugins": [ { "mod",
"id", "state": "loaded"|"skipped"|"failed", "reason", "detail" } ] }` with reasons `untrusted`, `hashChanged`, `buildUnsupported`, `dependencyMissing`, `incompatible`, `disabled`, `fileMissing`,
`apiUnsupported`, `error`; **`loading.json`** `{ "mod", "id", "file" }`, written before each plugin is loaded and removed when start-up is over (a file left behind means the game died there);
and logs (`orion.log`, `plugins\<mod key>.log`), which the Logs page lists. *Orion's rules:* load a plugin only if `pluginsEnabled`, the mod is in `dlc_load.json`, it is `enabled` in
`plugin-settings.json`, every listed file hashes (SHA-256, taken again right before loading, D06) to the approved value in `trust.json`, and the build and dependencies fit; otherwise skip it with the
reason. A plugin's files are loaded from its mod folder in the order of `dlc_load.json`. *`modloader.json` schema 1* (read by Artemis without loading anything): `schema` (1), `id` (1 to 64
of letters, digits, `.`, `_`, `-`), `name`, `version` (text), `apiVersion` (whole number, 1 or more), `files` (DLL paths inside the mod folder, forward slashes, no `..`), `builds` (patterns like
`v4.5.*`, same rules as a mod's `supported_version`; empty means any), `dependencies` and `incompatible` (plugin ids; version ranges are not defined yet and are kept as text), `keywords`, `features`
(`id`, `name`, `description`, `default`), `settings` (`id`, `type` bool, int, float, enum, string or path, `name`, `description`, `default`, `min`, `max`, `options`), `stability` (`stable` or `fragile`,
fragile when absent), `cosmetic`. Setting and feature ids are unique within a plugin. *The stub* says "Artemis" as the company in its version resource (D14, D45).

**D49 Plugin features in the manager (Step 4, built before Orion, D47).** *Reading:* a mod with a `modloader.json` is a plugin mod; an unusable manifest is shown with the reasons, never loaded.
*Approving (D06):* adding a plugin mod to a playset, or switching one on, asks once in plain words ("runs code inside the game, with the same permissions as you", the files and the start of
each hash, "only approve plugins you trust"); declining leaves it in the playset but skipped. Approval is stored per mod and pinned to the exact files; a changed file shows "Changed since
approved" until approved again. A mod in a local folder (not the Workshop's) approves its own rebuilds, and "follow updates" is an opt-in box (off), both by bringing the stored hash along at the
next launch or save. *Per playset:* a plugin can be switched off in a playset while the mod's scripts still load, and settings and feature switches are kept in the playset. *Screens:* a "Plugin"
mark on rows (orange when something needs doing), the Plugins page (state, files, approve or take back, follow updates, run here, settings drawn from the schema with range checks), a Plugins
filter in the Installed list and a line in the details drawer. *Safety (D24):* a master switch, "Launch without plugins (this time)", and notices for a `winmm.dll` that is not ours,
for a plugin the game died loading (one click switches it off in this playset) and for plugins that did not load last time. *Mods:* Create a mod (with an optional plugin manifest) and Install
from a .zip archive (extracts safely inside the mod's folder, refuses names and paths that escape it, asks before replacing, says when an archive carries a plugin or loose DLLs); 7z and rar are not
supported. Mods that are zip files are indexed like folders (D38), so conflicts and the achievement badge see them. *Not built:* installing or removing Orion's stub (it needs Orion), and
anything Orion reports until Orion exists: the page says so ("Orion isn't installed yet").


**D50 Orion, first build (Step 3; follows D48, adds to it).** *Parts:* the stub is `winmm.dll` (its forwarders are generated at build time from the machine's own System32 `winmm.dll` export table, 181
exports with their ordinals; nothing generated is checked in) and the core is `orion.dll`; both say "Artemis" as company. The stub finds the core through a pointer file: `<data>\orion\current.txt` names a
version folder and the core is `<data>\orion\<version>\orion.dll` (`<data>` is `ARTEMIS_DATA_DIR`, else `%APPDATA%\Artemis`). It acts only when the host exe is named `stellaris.exe` (so the Launcher's `dowser.exe`
is left alone, D45), starts no thread, writes nothing, hooks nothing, and does nothing at all if the pointer, the folder or the core is missing or odd. *Start-up runs inside the stub's `DllMain`*, before any
game code: nothing can race it and Step 5's hooks will need that. The cost is that plugins initialise under the Windows loader lock, so the header tells authors to keep init short and not to wait on threads or
load libraries; rejected: a worker thread (races the game's own start-up). Revisit when Step 5 finds a safe point. *Crashes:* a crash while a plugin loads or initialises is deliberately not caught, so the game dies and
`loading.json` stays (D24); a crash in Orion's own code is caught, logged, and the game carries on as vanilla. *Builds (D15):* the build id is `<PE timestamp, 8 hex digits>-<SizeOfImage, hex>`, read from the headers
in memory. `<data>\orion\builds\<id>.json` `{ "build", "game": "v4.5.2", "addresses": { "<name>": <offset from the exe's base> } }` is the verified data; without a file whose `build` equals the id, every plugin is
skipped as `buildUnsupported`, and the game version in it is what a plugin's `builds` patterns are matched against. The "per-user override" is simply a file the user puts in that folder. *Order of checks* per plugin
(first failure wins): switched off by `ORION_PLUGINS=off`, the master switch, the playset or absence from `plugin-settings.json` (`disabled`); `apiUnsupported`; `buildUnsupported` (no data, or `builds` doesn't cover
the game); `dependencyMissing` (a dependency must already be loaded, so it must come earlier in the list); `incompatible` (checked both ways against plugins already loaded); `fileMissing`; `untrusted` (no approval, or a
file not in it) or `hashChanged`; then load. A bad `modloader.json` is `failed`/`error`; Orion reads it as leniently as Artemis does (comments, trailing commas, backslashes) and checks only what loading needs.
*Trust and load (D06):* each file is read once and hashed (SHA-256, written out in the core so no crypto DLL loads during start-up), compared with `trust.json` (hex in either case), written to
`<data>\orion\cache\<sha256>.dll`, reopened with writers locked out, hashed again through that handle, and loaded while the handle is held. A tampered copy is replaced. Copies of older builds stay, so a rebuild never
touches a file a running game has loaded; they are not pruned yet. *Plugin ABI v1* (`sdk/include/orion_plugin.h`): one export, `OrionPluginInit(const OrionHost*)`, returning 0 for success; the host table holds its
size, API and Orion versions, build id, game version, the exe's base, `alloc`/`free`, `log` (to that plugin's own log), `getBool`/`getInt`/`getFloat`/`getString` (settings and feature switches by id) and
`resolve(name)` (base plus the offset from the build data, or null). No shutdown call (D23). Every file in `files` is loaded in order and must export the init function. Orion installs no hooks itself; a plugin patches
the game with `resolve`. *Additions to the D48 files:* `status.json` also has `game` and `buildSupported`; Orion replaces `status.json` at the very start of every run (so a start that dies leaves no stale list) and
again after each plugin; `ORION_USER_DIR` replaces the Documents game folder (for tests; Artemis never sets it); every log line starts `[hh:mm:ss]` so the Logs page shows one line per entry. *Not in this step:* hooks
and honest mode (Step 5), so the old experiments (`xor`, workforce, build speed) do not return yet, because they need real addresses; the test plugin (`sdk/test_plugins`) proves the host instead. Also not built: the thin C++
header, cache pruning, and the manager installing or removing the stub and the core with the D24 consent (the manager still only detects a `winmm.dll`). *Verified:* 13 scenarios in `tests/orion/run_tests.py` against a
stand-in game (export set equals System32's; winmm calls forwarded; `dowser.exe` untouched; no core, unknown build, untrusted, changed, missing, switched-off; failing and crashing init; order, dependencies and
incompatibilities; builds and API patterns; odd manifests and paths; tampered cache). Removing the `stellaris.exe` check from the stub makes the `dowser.exe` scenario fail. *Never run inside the real Stellaris.*


**D51 The manager installs Orion (Step 3b; supersedes "not built" in D49 and D50).** *Files:* Artemis.exe carries Orion's two files in an `orion` folder beside it (the project file copies them from `orion/build` when
`orion/build.bat` has been run; `--orion-dir` points elsewhere, for tests; real packaging is Step 6). *Install* ("Install Orion…" on the Plugins page; asks once, in plain words, D24): the core is copied to `<data>\orion\<its file
version>\orion.dll`, the pointer `current.txt` is written (no byte-order mark, the stub reads plain bytes), then the stub is copied to the game folder as `winmm.dll` through a temp file; other version folders are removed and
identical files are left alone. It refuses while Stellaris runs, when the copy of Artemis has no Orion files, and when the game folder has a `winmm.dll` that is not ours (never replaced, and Remove never deletes it either);
Windows refusing the write (a game under Program Files) is explained in words. *Remove* deletes our stub, the pointer, the core folders and the plugin copies, and keeps the user's permissions for builds, the trust store and
settings. *Repair and update:* the page shows NotInstalled, Installed, NeedsRepair (Steam's "verify files" took the stub), Outdated (this Artemis carries a different Orion), ForeignStub or NoPayload by comparing files. Once the
user has agreed (`OrionInstalled` in settings), every launch and every save-without-launching puts back what is missing and brings Orion up to date; if that fails it is logged and the game starts as vanilla.
*Unverified builds (D15, D50):* the panel shows the game's build id, read from the exe's headers the way Orion does, and whether it is verified. "Allow plugins on this build…" is D15's per-user override: after a warning in plain
words it writes `<data>\orion\builds\<id>.json` `{ "build", "game" (the launcher's rawVersion), "source": "user", "addresses": {} }`. It covers that exact build only, so a game update asks again; "Stop allowing" deletes only a
file marked `source: "user"`, and data from the address tools is never overwritten or deleted from here. With no addresses `resolve` gives null, so a plugin that needs engine addresses has to fail cleanly there.
*Verified:* 12 more core tests (155), the UI smoke test (install, allow, remove against a sandbox; 13 checks), and a scenario in the Orion harness that loads a plugin using the file the manager writes (14 scenarios). *Not tried in the real
game folder.*


**D52 Honest-mode spike, design and first findings (Step 5a; results to be added).** *Finding from the exe's export table (read from the file, nothing run):* `stellaris.exe` exports 108 PhysFS functions, but under their C++
names (for example `?PHYSFS_mountMemory@@YAHPEBX_KP6AXPEAX@ZPEBD4H@Z`, `?PHYSFS_isInit@@YAHXZ`, `?PHYSFS_getSearchPath@@YAPEAPEADXZ`), so a plugin finds them with `GetProcAddress` on those exact names: no address data, no
hook. That includes `PHYSFS_mountMemory`, which can add an in-memory zip to the game's search path. *Question:* if Orion mounts a zip holding `common/modloader/manifest.txt` (a fingerprint of the loaded plugins), does the game
count it in its checksum, so that the multiplayer checksum changes and achievements turn off as for a script mod (D22)? *Method:* `sdk/test_plugins/honest_spike.cpp`, a plugin with two settings (`mode` observe or mount, `mountAfter`):
it watches four kernel32 file calls through the exe's import table (read-only, the way the first proof of concept did) and polls PhysFS from its own thread, never inside the game's calls, and logs a timeline (when PhysFS starts,
when each folder is mounted, the first touch of `common`, `events` and so on). In mount mode it builds the zip in memory (stored entries) and mounts it, appended, once the search path holds the mod's own folder (so all mods are
mounted first), then checks that `common/modloader/manifest.txt` exists and that the archive stays in the search path. *Oracle:* the author compares, with and without the mount, the place in the game used for the D43 achievement
test (and the checksum text if the game shows one). *Stand-in test:* `tests/orion/fake_physfs_game.cpp` exports the same C++ names and plays out a start-up; the harness checks the log, the order of events and that the mounted bytes
open as a valid zip (15 scenarios). *Not yet known:* whether the game builds its checksum from PhysFS at all, and when. Loader code still never spoofs a vanilla checksum (D22).


**D52 result (2026-10-15, real game, build 6abeaa3f-37f2000, v4.5.2): confirmed.** With the spike only watching, the menu checksum was unmodified; with it mounting `orion-fingerprint.zip` (498 bytes, one file,
`common/modloader/manifest.txt`) the author saw the checksum modified, and the checksum is the author's indicator for achievements (D43). So a file added through the game's own PhysFS exports counts in its
checksum, with no address data and no engine hook. *Timeline from the logs:* PhysFS starts about 1.0 s after launch; at about 1.75 s the game replaces its whole search path in one burst (the DLC archives, the
mods in `dlc_load.json` order, the game folder again; 26 entries here); the fingerprint was mounted at about 1.77 s, appended after that burst, and stayed in the search path for the 2 minutes watched; the game
reads `checksum_manifest.txt` at about 19.3 s. Mod folders appear in the search path with forward slashes (`C:/Users/.../mod/<name>`), DLC archives as `.../dlc/<dlc>/<dlc>.zip`, and the game's own folder also
as `tweakergui_assets`. The spike's `\common\` label also matches `steamapps\common\Stellaris`, which is a flaw in the log, not in the result. *Still to do for honest mode:* make it Orion's own feature (D53).


**D53 Honest mode in Orion (Step 5a; built from the D52 result, stand-in tested, real-game check pending).** *What:* a plugin that changes how the game plays is added to the game's checksum, so achievements go off and multiplayer
mismatches show, exactly as for a script mod (D22). *How:* once every plugin has loaded, Orion starts a watcher thread (never inside the game's own calls). It waits for the game's PhysFS to start, then for the last mod of
`dlc_load.json` that has a folder to appear in the game's search path (paths compared lower-case with forward slashes), then for the list to stop changing for 40 ms, and mounts a zip (stored entries) holding
`common/modloader/manifest.txt`, appended. It checks the game can see the file, and if the game drops the archive from its list it mounts it again (up to three times, for 30 s after PhysFS starts). If the last mod never appears it
mounts anyway after 8 s (`ORION_HONEST_WAIT_MS`, for tests); a refused or invisible mount is reported as failed. *What counts:* the manifest's `cosmetic` is the author's claim (absent means the plugin counts); the manager may send the
user's choice per mod as `countsForChecksum` in `plugin-settings.json`, and that wins. Today the manager sends `!cosmetic`; a user override is not built. *The fingerprint:* `plugin = { id version file = { path sha256 } }` per
counting plugin that loaded, sorted by id, so the same plugins give the same bytes on every machine whatever the load order; nothing about Orion's own version or the machine is in it. With no counting plugin nothing is mounted and
the game's checksum is untouched. *Gating (fail closed, D15):* a counting plugin only loads if the game's PhysFS exports (the six Orion calls, found by their C++ names) are all there; otherwise it is skipped as
`buildUnsupported` with a plain sentence, and cosmetic plugins load as before. *Status:* `status.json` gains `honest` `{ "state": "notNeeded"|"pending"|"mounted"|"failed", "detail", "plugins" }`, rewritten by the watcher; the manager shows
it on the Plugins page and raises a notice when it failed. A watcher crash is caught, and never turns an already mounted fingerprint into "failed" (the game's file system may be gone at shutdown; the harness found this race).
*Tests:* six scenarios in the harness (what counts and in what order, the same bytes for a reordered list, cosmetic only, no PhysFS, the user's choice, a dropped archive, the timeout), 19 in all; two core tests (156). Loader
code still never spoofs a vanilla checksum. *Not yet:* the real-game check of Orion's own mount, a user override in the manager, multiplayer (v1 is singleplayer), and what other tools that add checksummed files do to the fingerprint.


**D53 real-game check (2026-10-15, build 6abeaa3f-37f2000, v4.5.2): passed.** With the spike only watching (so only Orion's own honest mode could mount anything), Orion mounted the fingerprint 840 ms after PhysFS started, and
the spike's own watcher saw `common/modloader/manifest.txt` exist. The menu then read `Version: Cygnus v4.5.2 (30f1)` with the hex in yellow. A control launch of the same mod list with `ORION_PLUGINS=off` (the plugin skipped, nothing
mounted, honest state `notNeeded`) read `(9776)` in white, the build number Steam's launcher-settings gives, so the menu shows the game's checksum in place of the build number once it is modified. Orion also updated itself through Artemis at
launch (D51) before this run. Not run in the game: the cosmetic-only case (covered by the harness) and a user override. The checks were read from the logs and from a capture of the game's own window only.


**D54 Build data with values, the game-start moment, and the first experiments as a plugin (Step 5b; stand-in tested, real-game check pending).** *Build data* (`<data>\orion\builds\<id>.json`) now has `values`
beside `addresses`: whole numbers a plugin needs that are not addresses (struct offsets, token ids, flags), read with the new `host->getBuildValue`. `orion/tools/ini_to_build.py` turns a first-proof-of-concept `addresses-<build>.ini`
into it (a name ending `_rva` becomes an address without the suffix, anything else a value) and merges into an existing file, so the user's permission (`"source": "user"`) survives; the file stays local, it is game-derived
(D15). The 4.5.2 data for the author's build was converted from the earlier loader's port; its struct offsets are copied from 4.5.1 and still unproven in-game, and the plugin checks the donor vtable and evaluate function against the
data before it registers anything, so a stale entry registers nothing. *ABI additions* (API stays 1, appended to the host table; a plugin checks `host->size`): `getBuildValue(name, &out)` and `onGameStart(callback, user)`, which runs the
callback once, on the thread the plugin started on (the game's main thread), the first time that thread touches a file under the exe's own `common` folder, the moment the first proof of concept registered keywords at, after the
game's databases exist and before it parses a script. Orion makes the read-only import patches for it (CreateFileW, FindFirstFileW, FindFirstFileExW), chained onto whatever the slot held; a fault in a callback is caught and logged.
`OrionPluginInit` may return `ORION_INIT_UNSUPPORTED` (1): the plugin is shown as skipped, `buildUnsupported`, not failed. *The plugin:* `sdk/test_plugins/keywords.cpp` is `xor` and `num_assigned_workforce` ported from the
earlier loader (features `xor` and `num_assigned_workforce` switch each off), with the old in-game test mods as `sdk/test_mods/keyword_tests` (`event xortest.1`, `event wftest.1` in the console). The megastructure build-speed
hook is not ported: it needs an inline-hook library, which antivirus heuristics flagged before (D14). *Tests:* the harness checks the values, the callback (once, on the main thread, not for another thread's read, case-insensitive
path), the unsupported return and the converter (21 scenarios at the time); the engine code itself can only be tried in the game.


**D54 first real-game result (2026-10-15, build 6abeaa3f-37f2000, v4.5.2).** The keyword plugin loaded; the game-start hook fired on the first read of the exe's own `common` folder; both donor checks passed and both
keywords registered (trigger registry 1098 to 1100 entries). `xor` evaluated correctly for 2 and 3 children with 0, 1 and 2 or more true. `num_assigned_workforce` returned 202 = 150 raw pops + 52.5 bonus every time, where the
engine's own count says 150. The test events showed different text on each firing because several valid `desc` blocks are picked from at random; the events now show each line as an option (shown only when its trigger is true).
*Open:* every logged workforce evaluation had scope mask `0x10000000000` (colony), although the event fires in the country scope and also tests `capital_scope`; a mask the code does not recognise falls back silently to the raw
count. Either the engine hands the value getter a colony scope per colony (then the results are right) or the mask offset moved in 4.5.2. The plugin now logs each new mask with the scope object's first bytes, the scope and colony
pointers, and every unhandled mask; the next run decides which.


**D54 second real-game result (2026-10-15, same build).** The new logging answered the open question. A scope mask of `0x4` (country) and of `0x10000000000` (colony) both reach `num_assigned_workforce`. In colony scope
(which is what `capital_scope` hands it) the result is right: 150 raw pops + 30 bonus = 180, the colony resolved through the pop-job pool every time. In country scope the plugin produced no result at all: the path that reads the country's
colony list (offsets copied from 4.5.1) found no colonies, returned false, and the donor's raw count was used, so a country-scope `num_assigned_workforce` is wrong in 4.5.2 until those offsets are fixed. The plugin now looks through the
country object for an array of colony handles (a pointer with an int count next to it, whose first handle is the colony seen in colony scope) and logs the candidates, so the 4.5.2 offsets can be read off one more run. Also seen: `xor`
was evaluated 338,100 times in 2.5 minutes by the test window (it re-evaluates its triggers every frame), so the periodic log line is now every 20,000 evaluations; and one game line,
`num_assigned_jobs trigger uses inexistent job` with an empty job name, appeared once at load, not yet explained.


**D54 closed (2026-10-15).** The experiments were only ever a way to prove the plumbing, and it is proven in the real game: build data with addresses and values, the game-start moment, registering new triggers in the engine's registry, and
reading engine structures from a plugin. `xor` is fully working. `num_assigned_workforce` works in colony and planet scope on 4.5.2 and is wrong in country scope (the 4.5.1 offsets for the country's colony list moved); that is a fault of
that one experiment, not of Orion, and it is left as a known limit rather than fixed. The plugin keeps its diagnostics (they print what a fix would need) and the build data keeps its numbers; neither is shipped. The megastructure
build-speed experiment stays unported. What Step 5 still lacks is the pipeline for new game builds: honest mode needs only the game's exported file-system names, so after a patch the user need only allow the new build (D51); fragile plugins need
new address data, which is a per-plugin author's job until a porting tool exists.


**D55 Mod folder upkeep, as Irony does it (requested by the author, 2026-10-15).** *Mod folders without a .mod file:* the game only sees a mod through a .mod file in `mod/`, so on every scan (start-up, Rescan, after an install) Artemis looks for folders anywhere under `mod/`
(`outer/inner/descriptor.mod` counts, to six levels; a folder with a descriptor.mod is a mod's root, so the search does not go on inside it) that no .mod file points at, and writes one in `mod/`: the folder's descriptor with its own stale `path` and
`archive` lines dropped and a `path="mod/<relative folder>"` line, named after the mod; it never replaces a file (a free name, "Name 2", is found) and says what it made. *Mods whose files are gone:* a .mod file whose folder or archive no longer
exists is reported by a notice ("N mods have a .mod file but no files any more: ..."), with "Review…" (also in the Add a mod menu as "Clear leftover .mod files…") listing them and asking before anything is removed. The files are sent to the
Recycle Bin, not deleted outright (changed on 2026-10-15 with D56; first they went to `%APPDATA%\Artemis\backup\descriptors\<time>`); each is looked at again first and one whose files came back, or that is not directly in `mod/`, is left alone; playsets keep their entries. The dialog warns that Workshop mods may only
be mid-download or on an unplugged drive. Dismissing the notice puts that set aside until the set of missing mods changes. Where it differs from Irony: a notice instead of a prompt at every start, and the Recycle Bin instead of a delete. Tests: four core tests
(160 in all) and `manager/tools/UiTests/ModFolderSmoke.ps1`, which drives the app against a sandbox.


**D56 Logo, live logs, log clean-up (requested by the author, 2026-10-15).** *Logo:* the flat blue "A with an orbit" supplied by the author. `manager/tools/make_logo.py` turns the source picture (`Assets/logo-source.webp`, white background) into a
transparent, square `logo.png` and a multi-size `app.ico`; the transparency is recovered from the picture's own anti-aliasing, so there is no white fringe on the dark UI. The icon is the exe's icon and the window's (taskbar); the
title bar shows the PNG in place of the old placeholder ring. The logo is a working draft: replacing the source picture and running the script is the whole job. *Live logs:* while the Logs page is open, once a second the list of logs and
the end of the chosen log are looked at (`LogTail`, polling: a file watcher is late on files that are only appended to). Only new lines are appended to the view, so a text selection and the scroll position survive; the view follows
the end unless the reader has scrolled up; a log the game started over (a new run) is shown whole again; a last line still being written waits for its line break; past about a million characters the oldest half of the view is
dropped. The refresh button now re-reads the chosen log from its end. *Clean-up (Logs page, trash button; not while the game runs):* offers only what is clearly left over and shows each file with its reason before anything moves.
Leftovers are: a game log (`logs/*.log`) more than 30 days older than the game's *newest* log (measured against that, not against today, so a long break never makes the last run's logs look old); `modloader/modloader.log`
(an earlier loader's, not Orion's); and a plugin log in `modloader/plugins` whose plugin is not an installed mod any more (the log is named after the mod's key, Orion's `SafeFileName`; installed means present in the mod folder, in a
playset or not). Never offered: the newest game logs, `orion.log`, Artemis's own log, logs of installed plugins, empty files. It does not delete outright: each file is sent to the Recycle Bin (no backup folder of Artemis's own; the author asked for this),
after being looked at again (one that stopped being a leftover, or that is open in the game, stays). *Limits:* a line that continues a game log entry begun in an earlier poll shows on its own line;
the game's `logs/script_documentation` folder (about 5 MB, rewritten each run) is not a log of anything and is left alone. Tests: 17 new core tests and `manager/tools/UiTests/LogsSmoke.ps1`, which also checks the files really are in the Recycle Bin and then takes its own test files out of it.


**D57 Achievement trophies, header wording (requested by the author, 2026-10-15).** The "Achievements: on / off (3 mods)" text under Launch (D22) is gone. Each mod now carries its own mark in both lists: a trophy when it adds or changes no file the
game's checksum covers, a broken one when it does (white, the two halves of the trophy split along a jagged crack and tilted 15 degrees away from each other into a V, so the two can't be mixed up at a glance; the intact trophy stays grey); nothing until the mod's files have been read, or when the game has no checksum list. It is a column of its own, 36 px wide, left of "Game version" in
Installed and left of the enabled switch in Load order (that list has no version column), so names are cut off a little sooner. The tooltip says how many covered files the mod changes and names the first. The playset-wide answer is now "any broken
trophy among the enabled mods"; there is no summary any more. The icons are drawn in `Themes/Tokens.xaml` (no icon font has them). Header wording: "Game" is "Game version", "#" is "Number" (left-aligned over its column), and "Name" starts at the
left edge of the thumbnail column in both lists. (The plugin gap noted here is closed in D58.)
Test: `manager/tools/UiTests/ListsSmoke.ps1`.


**D58 The trophy counts plugins too; documentation brought up to date (2026-10-16).** A mod's plugin that counts toward the game's checksum (honest mode, D53) turns achievements off by itself, whatever the mod's files say, so the trophy (D57) and the
drawer's "Achievements" line now judge both (`AchievementCheck.Judge`, one rule for both places). A plugin counts when it is not marked `cosmetic`, plugins are switched on (the "Load plugins" box) and it is not switched off for this mod in the
playset: the same conditions under which Orion is told `countsForChecksum` (D53). Approval is not asked about: an unapproved plugin is skipped by Orion until approved, but the mark warns ahead of time rather than showing a trophy that turns
broken the moment the plugin is approved; the tooltip says "while it is switched on". The marks refresh when plugins are switched on or off for the playset or for a mod. *Limit:* when the user override of `countsForChecksum` is built (D53,
not yet), `PluginCounts` must follow it. Also: the README lost its temporary-repo wording, and DESIGN's "Open and unverified" list was corrected (the fingerprint test and "Orion has only run in a stand-in game" were already resolved by D52 to D54; what remains
unverified is the cosmetic-only case, other game builds, and the Launcher and stub questions). Tests: five core tests for the verdict (182 in all), and `ListsSmoke.ps1` now has a counting, a cosmetic and a switched-off plugin mod.


**D59 Conflict scanner: what is not a definition name (2026-10-16).** The author saw an `inline_script` "conflict" in common/buildings between the game and two mods whose files share nothing. Cause: the scanner (D38) took every top-level word as a
definition name, and some top-level words are not names. Checked against the game's own files and the author's real playset, three kinds were fixed; the index version went from 2 to 3, so every source is read again once (about 3 s warm for 48 mods and the game).
(a) `inline_script` is a statement that expands into definitions: never a name, in any folder. (b) Folders whose blocks all start with the same word and carry the name inside (`weapon_component_template = { key = X }`, `defined_text = { name = X }`,
`ship_section_template`, `component_set`, `special_project`, `message_type`, `ship_design`, `ship_behavior`, `ambient_object`, `random_list` in planet classes, the `*_parts_list` blocks of random_names, ...) are now named by that field (`key` or `name`, any case);
a block without it (its content comes from an inline script) is left out, because its name can't be known; blocks that are not named things at all (`terraform_link`, `part`, the `*_format` blocks) are left out too. The table is `Wrapped` in `ScriptScanner`; a mod-added word
in one of these folders is still read as its own name. Before this, every pair of mods with component templates or scripted localisation "conflicted" on the shared word. (c) A `@variable` is a name only in common/scripted_variables; elsewhere it is local to its file.
Also found by comparing counts on the real playset: (d) a quoted string may run over several lines (an inline script's `TRIGGER = "owner = { ... }"` is a block of script in quotes, used by the game's own buildings). The scanner ended strings at the line break, lost
its place in the braces, and then read parameters such as `AMOUNT` and whole sub-blocks (`triggered_planet_modifier`) as top-level definitions for the rest of that definition. Strings now end at their closing quote. A quote never closed now swallows the rest of its file
(the game would not read such a file either). *Result on the real playset (16 enabled mods):* no `inline_script` conflict, 4 fewer definition conflicts from the string fault (1308 to 1304; the wrapper change moves the names of component templates and the like
to their real keys, where the real overlaps remain). *Not covered:* wrapper words the game files don't show (a new block type in a patch) are read as before until added to the table; folders where the name is neither the word nor `key`/`name` are not known to exist.
Tests: six scanner and conflict tests (192 in all).

**D60 A separator's group can be folded away (requested by the author, 2026-10-16; supersedes "not built" in D32).** In the load order each separator has a chevron: it hides the mods below it, down to the next separator, and brings them back. The separator
shows how many mods its group holds. Mods above the first separator belong to no group. A group stays folded between sessions: `collapsed` is saved on the separator's line in the playset file (only when set). Folding changes no order, so it is not an
undo step and does not touch `dlc_load.json`; positions still count the hidden mods. Hidden rows are never acted on: they are deselected when their group folds, and Ctrl+A with Delete, Space or a drag leaves them alone. A hidden mod comes back into view when
it is found with Find next or previous, when a mod is moved into the group, and when a mod is added at the end of the list while the last group is folded. The separator's right-click menu has "Fold this group away" or "Show the mods of this group", and
"Fold all groups away" and "Show all groups". The Installed list's groups (Source) were already collapsible. *Limit:* Undo and Redo bring back the fold state the playset had at the moment of the step. The logic that decides what is hidden is `Playset.Groups`
(four core tests, 192 in all); `manager/tools/UiTests/SeparatorsSmoke.ps1` drives the app.

**D61 Plugin ideas studied; a sampling profiler first (2026-10-16, requested by the author).** *Councillor slots:* the council is built for 6 positions (`MAX_COUNCIL_POSITIONS` in `unchecked_defines`, a 6-column grid in `council_view.gui`, and the engine's own
slot handling: `unlock_council_slots` only opens locked ones). The author takes the engine cap as fact, so a 7th slot is not a script mod; it would be a code-level (fragile) plugin whose feasibility depends on how the engine stores the slots (a fixed array
would rule it out). Not started; reading the engine's slot functions needs a Ghidra pass on the Linux build (full symbols, on disk; no analysed project exists). *Performance:* the aim is late-game days per second, for personal use (multiplayer desync is not a concern,
but a non-cosmetic plugin still counts toward the checksum and turns achievements off). Suspects are fleet pathfinding and pop/job assignment; nothing was measured, so no hook target is chosen. *The profiler* (`sdk/test_plugins/profiler.cpp`, manifest id
`artemis.sampling-profiler`, stable tier, claims `cosmetic`: it only observes): its own thread pauses each game thread for tens of microseconds every `intervalMs` (default 2), walks the stack with the unwind tables of the exe and the other modules (no engine addresses,
no hooks, nothing from the build data), and counts call paths in a tree. While a thread is paused the sampler touches only memory prepared beforehand (no heap, locks, logging or loader calls), which is what stops the pause from deadlocking against a lock the paused
thread holds. Functions without an unwind entry (small leaf functions) are told apart by the gap after the previous entry. Ctrl+Shift+hotkey (default F9, read from the keyboard state) starts and stops; `autoSeconds` samples from plugin load (for start-up);
a run stops by itself after 20 minutes. The report goes to `Documents\...\Stellaris\modloader\profiles\profile-<time>.txt`. `orion/tools/profile_report.py` reads it with Exception Translator's names file (read only, never copied; about a third of the game's code is named,
the rest shows as `sub_<rva>` and is charged to the nearest named caller) and prints per thread: where the innermost frame was, subsystem shares, busiest functions, and named inclusive time. *Checked against the real exe offline:* the exe's unwind table has 27,136
chained entries and following each chain to its first entry gives exactly Exception Translator's function owner for all of them. *Tests:* a stand-in game with a known call chain, one function with unwind data and one without (22 scenarios in all). *Not yet run in the
real game at the time of writing;* the hotkey path is not covered by the stand-in (only the automatic run is), and the pause overhead on the real thread count is unmeasured (the report records the average cost of one sampling round).

**D61 first real-game result (2026-10-16, v4.5.2, the author's mid-2300s save, `one_year` at top speed, 105 s sampled).** The hotkey worked: 71 threads, 2.4 million samples, no dropped or cut call paths; one sampling round took 0.7 ms
(each thread is paused for only a small part of that). *Where the time goes (main thread, 97% busy; about 11 worker threads are each busy about a fifth of the time and wait the rest, so the game is bound by its main thread):* the simulation tick
(`HandleTurnTick`) holds about two thirds (daily update 32%, micro update 23%, AI 8%, scripted triggers 8% inclusive); drawing a frame (`RenderFrame`) holds about 22%, and updating the interface (map icons, buttons) about another 5 to 9%. The game
draws frames from inside its own long updates (a frame is pumped from the daily update, the micro update and AI, and also while the main thread waits for the parallel ship update), so part of that drawing overlaps waiting and would not turn into speed;
the rest would. The suspects from the author's experience are not what this year shows: fleet pathfinding about 0.5% and pop/job assignment about 0.3% of the main thread (a war-heavy period may differ). Another sim cost seen: rebuilding each empire's
default ship designs (`SetDefaultShipDesigns`, about 3.5%), which looks like a repeated calculation. The profile slowed the game itself (no clean baseline yet). `profile_report.py` gained `--tree` (a top-down call tree from the top or from a function).
