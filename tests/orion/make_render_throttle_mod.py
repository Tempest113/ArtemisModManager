"""Builds the render-throttle plugin and packs it as a Stellaris mod archive for trying it in the real game.

    python tests/orion/make_render_throttle_mod.py

Writes local/orion-trial/Orion Render Throttle.zip (git-ignored) and prints the plugin's SHA-256. Install it from Artemis (Mods, Add a mod, Install from a
.zip). It needs the build data added by orion/tools/add_address.py (render_frame); without it the plugin shows as skipped and the game runs as before.
Ctrl+Shift+F8 in the game switches the throttle off and on, to compare speeds; its log says what happened.
"""
from __future__ import annotations

import hashlib
import json
import shutil
import tempfile
import zipfile
from pathlib import Path

from run_tests import ROOT, compile_with_msvc

NAME = "Orion Render Throttle"

DESCRIPTOR = f'''name="{NAME}"
version="0.1.0"
supported_version="v4.5.*"
tags={{
\t"Utilities"
}}
'''

MANIFEST = {
    "schema": 1,
    "id": "artemis.render-throttle",
    "name": NAME,
    "version": "0.1.0",
    "apiVersion": 1,
    "files": ["orion/render_throttle.dll"],
    "builds": ["v4.5.*"],
    "stability": "fragile",
    "cosmetic": True,
    "settings": [
        {"id": "minIntervalMs", "type": "int", "name": "Draw extra frames at most every (ms)", "min": 0, "max": 2000, "default": 250,
         "description": "While the game works through a day or a month it draws extra frames between its steps. With this on, those extra frames happen at most this often. 0 turns the plugin off."},
        {"id": "hotkey", "type": "int", "name": "Hotkey (virtual-key code)", "min": 0, "max": 255, "default": 119,
         "description": "Press Ctrl+Shift together with this key in the game to switch the throttle off and on, for comparing speeds; the same chord with Alt held (Ctrl+Shift+Alt+F8) steps the interval through 50, 100, 250 and 500 ms. 119 is F8; 0 turns the hotkeys off."},
    ],
}


def main() -> None:
    out = ROOT / "local" / "orion-trial"
    out.mkdir(parents=True, exist_ok=True)
    work = Path(tempfile.mkdtemp(prefix="orion-throttle-"))
    try:
        w = work.as_posix()
        compile_with_msvc(f'/O1 /LD /MT /EHsc /W4 /I"{(ROOT / "sdk" / "include").as_posix()}" /Fo"{w}/" '
                          f'"{(ROOT / "sdk" / "test_plugins" / "render_throttle.cpp").as_posix()}" /Fe:"{w}/render_throttle.dll"', work)
        dll = (work / "render_throttle.dll").read_bytes()
        archive = out / f"{NAME}.zip"
        with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as z:
            z.writestr("descriptor.mod", DESCRIPTOR)
            z.writestr("modloader.json", json.dumps(MANIFEST, indent=2))
            z.writestr("orion/render_throttle.dll", dll)
        print(f"wrote {archive}")
        print(f"plugin SHA-256 {hashlib.sha256(dll).hexdigest()}")
    finally:
        shutil.rmtree(work, ignore_errors=True)


if __name__ == "__main__":
    main()
