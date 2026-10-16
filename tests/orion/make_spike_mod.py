"""Builds the honest-mode spike plugin and packs it as a Stellaris mod archive for trying it in the real game.

    python tests/orion/make_spike_mod.py

Writes local/orion-trial/Orion Honest Spike.zip (git-ignored) and prints the plugin's SHA-256. Install it from Artemis (Mods, Add a mod,
Install from a .zip). Its setting "mode" starts at "observe" (only watch and log); "mount" makes it mount the fingerprint archive.
"""
from __future__ import annotations

import hashlib
import json
import shutil
import tempfile
import zipfile
from pathlib import Path

from run_tests import ROOT, compile_with_msvc

NAME = "Orion Honest Spike"

DESCRIPTOR = f'''name="{NAME}"
version="0.1.0"
supported_version="v4.5.*"
tags={{
\t"Utilities"
}}
'''

MANIFEST = {
    "schema": 1,
    "id": "artemis.honest-spike",
    "name": NAME,
    "version": "0.1.0",
    "apiVersion": 1,
    "files": ["orion/honest_spike.dll"],
    "stability": "fragile",
    "cosmetic": False,
    "settings": [
        {"id": "mode", "type": "enum", "name": "What the spike does",
         "description": "Only watch writes a timeline of the game's start-up to the plugin's log. Mount the fingerprint also adds a small virtual file, common/modloader/manifest.txt, to the game's files.",
         "default": "observe", "options": [{"value": "observe", "name": "Only watch"}, {"value": "mount", "name": "Mount the fingerprint"}]},
        {"id": "mountAfter", "type": "string", "name": "Mount once the game has loaded this",
         "description": "Text that must appear in the game's list of mounted folders before the fingerprint is added: the name of this mod's folder, so every mod is mounted first. Empty means as early as possible.",
         "default": NAME},
    ],
}


def main() -> None:
    out = ROOT / "local" / "orion-trial"
    out.mkdir(parents=True, exist_ok=True)
    work = Path(tempfile.mkdtemp(prefix="orion-spike-"))
    try:
        w = work.as_posix()
        compile_with_msvc(f'/O1 /LD /MT /EHsc /I"{(ROOT / "sdk" / "include").as_posix()}" /Fo"{w}/" '
                          f'"{(ROOT / "sdk" / "test_plugins" / "honest_spike.cpp").as_posix()}" /Fe:"{w}/honest_spike.dll"', work)
        dll = (work / "honest_spike.dll").read_bytes()
        archive = out / f"{NAME}.zip"
        with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as z:
            z.writestr("descriptor.mod", DESCRIPTOR)
            z.writestr("modloader.json", json.dumps(MANIFEST, indent=2))
            z.writestr("orion/honest_spike.dll", dll)
        print(f"wrote {archive}")
        print(f"plugin SHA-256 {hashlib.sha256(dll).hexdigest()}")
    finally:
        shutil.rmtree(work, ignore_errors=True)


if __name__ == "__main__":
    main()
