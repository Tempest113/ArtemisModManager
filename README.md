# Artemis

A mod manager for Stellaris that also loads plugin (DLL) mods. **Artemis** is the manager. **Orion** is the small
loader that runs inside the game and carries the DLL mods. (Working codenames, easy to rename.)

**Status: an unreleased preview. The script-mod manager works (`manager/`). Orion, the in-game loader (`orion/`), has been run in the real game (Stellaris v4.5.2) and the manager installs and removes it (D51), but plugins that need engine addresses won't work from this repo, because that data is game-derived and not published (D15). There is no signed release yet.** Needs Windows 10/11 and the .NET 10 SDK; Orion also needs Visual Studio's C++ tools (`orion/build.bat`). Start with
[docs/DESIGN.md](docs/DESIGN.md); the reasoning behind each choice is in [docs/DECISIONS.md](docs/DECISIONS.md).
Build and test with `dotnet test manager/tests/Artemis.Core.Tests`; run it with `dotnet run --project manager/src/Artemis.App`.
Build Orion with `orion/build.bat`, then `python tests/orion/run_tests.py`. `manager/tools/UiTests/PluginSmoke.ps1` drives the built app against a throw-away sandbox (see `Ui.ps1` for how it avoids touching anything else).

## The idea in four lines

- The manager is the main product: playsets, load order, fast conflict scanning, launching. It should replace Irony
  for daily use, without Irony's slow startup and slow scans.
- A DLL mod is an ordinary Stellaris mod folder that also carries a `modloader.json` and a DLL. It is enabled and
  ordered in the playset like any script mod.
- Orion reads the same `dlc_load.json` the game reads, so plugin and script mods can't get out of step. Each DLL loads
  only if you have approved its exact hash.
- Nothing here modifies `stellaris.exe` on disk or ships game files.

## Planned layout (folders are added when first used)

```
manager/   the mod manager: C#, WPF, .NET 10, with a UI-free core library and tests
orion/     the in-game side: the winmm.dll stub and the core loader (C++)
sdk/       the plugin C ABI, a thin C++ header, and test plugins
(orion/tools has the winmm export generator; address porting and offline analysis tools come with Step 5)
tests/     orion/: stand-in game harness (run_tests.py)
docs/      design and decision records
```

## Guardrails

- Never modify the game executable on disk. Never commit or ship game executables, DLLs, symbols, disassembly or other
  game files; `.gitignore` blocks them.
- Per-build address data is derived from the game binary. It stays local until it has been slimmed and deliberately
  published (D15).
- Plugin mods are programs and can do anything the user can. They load only when their mod is enabled and their hash is
  approved (D06).
- Unofficial community tool. Not affiliated with or endorsed by Paradox Interactive. Takedown requests
  are honoured.

## Reference projects (read-only, not dependencies)

`Stellaris DLL Shenanigans` (proxy + hook proof of concept), `Stellaris Mod Loader` (loader spikes and a first
manager), `Exception Translator` (per-build function data and crash analysis). See "Reference material" in
[docs/DESIGN.md](docs/DESIGN.md).

License: MIT.
