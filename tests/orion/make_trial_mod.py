"""Builds the test plugin and packs it as a Stellaris mod archive for trying Orion in the real game.

    python tests/orion/make_trial_mod.py

Writes local/orion-trial/Orion Test Plugin.zip (the folder is git-ignored) and prints the plugin's SHA-256, which the
approval prompt in Artemis shows the start of. Install the archive from Artemis (Mods, Add a mod, Install from a .zip).
The plugin does nothing to the game: it writes lines to its own log, and "Fail on purpose" makes it report a failure.
"""
from __future__ import annotations

import hashlib
import json
import shutil
import tempfile
import zipfile
from pathlib import Path

from run_tests import ROOT, compile_with_msvc

NAME = "Orion Test Plugin"

DESCRIPTOR = f'''name="{NAME}"
version="0.1.0"
supported_version="v4.*"
tags={{
\t"Utilities"
}}
'''

MANIFEST = {
    "schema": 1,
    "id": "artemis.orion-test",
    "name": NAME,
    "version": "0.1.0",
    "apiVersion": 1,
    "files": ["orion/test_plugin.dll"],
    "stability": "stable",
    "cosmetic": True,
    "settings": [
        {"id": "mode", "type": "enum", "name": "What it does when the game starts",
         "description": "Start normally only writes to the plugin's log. Fail on purpose reports a failure, to see how Artemis shows it; the game still starts.",
         "default": "ok", "options": [{"value": "ok", "name": "Start normally"}, {"value": "fail", "name": "Fail on purpose"}]},
        {"id": "count", "type": "int", "name": "A number", "description": "Only here to see a setting reach the plugin.", "default": 7, "min": 0, "max": 100},
        {"id": "title", "type": "string", "name": "A text", "description": "Only here to see a setting reach the plugin.", "default": "hello"},
    ],
}


def main() -> None:
    out = ROOT / "local" / "orion-trial"
    out.mkdir(parents=True, exist_ok=True)
    work = Path(tempfile.mkdtemp(prefix="orion-trial-"))
    try:
        compile_with_msvc(f'/O1 /LD /MT /I"{ROOT / "sdk" / "include"}" /Fo"{work}\\\\" "{ROOT / "sdk" / "test_plugins" / "test_plugin.cpp"}" '
                          f'/Fe:"{work}\\test_plugin.dll"', work)
        dll = (work / "test_plugin.dll").read_bytes()
        archive = out / f"{NAME}.zip"
        with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as z:
            z.writestr("descriptor.mod", DESCRIPTOR)
            z.writestr("modloader.json", json.dumps(MANIFEST, indent=2))
            z.writestr("orion/test_plugin.dll", dll)
        print(f"wrote {archive}")
        print(f"plugin SHA-256 {hashlib.sha256(dll).hexdigest()}")
    finally:
        shutil.rmtree(work, ignore_errors=True)


if __name__ == "__main__":
    main()
