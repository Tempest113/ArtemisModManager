"""Builds the sampling-profiler plugin and packs it as a Stellaris mod archive for trying it in the real game.

    python tests/orion/make_profiler_mod.py

Writes local/orion-trial/Orion Sampling Profiler.zip (git-ignored) and prints the plugin's SHA-256. Install it from Artemis (Mods, Add a mod, Install
from a .zip). It needs no build data of its own, only a build Orion allows plugins on. Ctrl+Shift+F9 in the game starts and stops sampling; the report
lands in Documents\\Paradox Interactive\\Stellaris\\modloader\\profiles. Read it with orion/tools/profile_report.py.
"""
from __future__ import annotations

import hashlib
import json
import shutil
import tempfile
import zipfile
from pathlib import Path

from run_tests import ROOT, compile_with_msvc

NAME = "Orion Sampling Profiler"

DESCRIPTOR = f'''name="{NAME}"
version="0.1.0"
supported_version="v4.5.*"
tags={{
\t"Utilities"
}}
'''

MANIFEST = {
    "schema": 1,
    "id": "artemis.sampling-profiler",
    "name": NAME,
    "version": "0.1.0",
    "apiVersion": 1,
    "files": ["orion/profiler.dll"],
    "stability": "stable",
    "cosmetic": True,
    "settings": [
        {"id": "hotkey", "type": "int", "name": "Hotkey (virtual-key code)", "min": 0, "max": 255, "default": 120,
         "description": "Press Ctrl+Shift together with this key in the game to start sampling, and again to stop and write the report. 120 is F9; 0 turns the hotkey off."},
        {"id": "intervalMs", "type": "int", "name": "Sample every (ms)", "min": 1, "max": 50, "default": 2,
         "description": "How often every game thread is paused for a moment to see where it is. Smaller is finer and slows the game more."},
        {"id": "autoSeconds", "type": "int", "name": "Sample start-up for (seconds)", "min": 0, "max": 900, "default": 0,
         "description": "Sample from the moment the game loads this plugin, for this many seconds, then write the report. 0 means only by hotkey."},
    ],
}


def main() -> None:
    out = ROOT / "local" / "orion-trial"
    out.mkdir(parents=True, exist_ok=True)
    work = Path(tempfile.mkdtemp(prefix="orion-profiler-"))
    try:
        w = work.as_posix()
        compile_with_msvc(f'/O1 /LD /MT /EHsc /W4 /I"{(ROOT / "sdk" / "include").as_posix()}" /Fo"{w}/" '
                          f'"{(ROOT / "sdk" / "test_plugins" / "profiler.cpp").as_posix()}" /Fe:"{w}/profiler.dll"', work)
        dll = (work / "profiler.dll").read_bytes()
        archive = out / f"{NAME}.zip"
        with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as z:
            z.writestr("descriptor.mod", DESCRIPTOR)
            z.writestr("modloader.json", json.dumps(MANIFEST, indent=2))
            z.writestr("orion/profiler.dll", dll)
        print(f"wrote {archive}")
        print(f"plugin SHA-256 {hashlib.sha256(dll).hexdigest()}")
    finally:
        shutil.rmtree(work, ignore_errors=True)


if __name__ == "__main__":
    main()
