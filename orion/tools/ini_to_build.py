"""Turns an address file of the first proof-of-concept loader (addresses-<build>.ini) into Orion's per-build data (D15, D50, D54).

    python orion/tools/ini_to_build.py <addresses.ini> --game v4.5.2 [--out <folder>]

The ini holds the game build (build_timestamp, build_size_of_image) and name=number lines. A name ending in _rva is an address (the offset from the
exe's base) and becomes addresses[name without _rva]; every other number (struct offsets, token ids, flags) becomes values[name]. The result is
<folder>\\<build id>.json, by default %APPDATA%\\Artemis\\orion\\builds. If that file already exists its other fields are kept, so the permission the
user gave for the build ("source": "user") is not lost; only addresses and values are replaced.

Game-derived data stays local: this tool is in the repo, its output never is (the folder is outside it, and /local is git-ignored).
"""
from __future__ import annotations

import argparse
import json
import os
import re
from pathlib import Path

SKIP = {"build_timestamp", "build_size_of_image"}


def convert(text: str) -> tuple[str, dict, dict]:
    """(build id, addresses, values) from the ini's text."""
    numbers: dict[str, int] = {}
    for line in text.splitlines():
        line = line.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, value = (x.strip() for x in line.split("=", 1))
        value = re.split(r"\s", value, maxsplit=1)[0]
        try:
            numbers[key] = int(value, 0)
        except ValueError:
            continue
    if "build_timestamp" not in numbers or "build_size_of_image" not in numbers:
        raise SystemExit("the ini has no build_timestamp and build_size_of_image, so the build can't be identified")
    build = f"{numbers['build_timestamp']:08x}-{numbers['build_size_of_image']:x}"
    addresses = {k[:-4]: v for k, v in numbers.items() if k.endswith("_rva") and k not in SKIP}
    values = {k: v for k, v in numbers.items() if not k.endswith("_rva") and k not in SKIP}
    return build, addresses, values


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("ini", type=Path)
    ap.add_argument("--game", required=True, help='the game version the data is for, such as "v4.5.2"')
    ap.add_argument("--out", type=Path, default=Path(os.environ.get("APPDATA", ".")) / "Artemis" / "orion" / "builds")
    args = ap.parse_args()
    build, addresses, values = convert(args.ini.read_text(encoding="utf-8", errors="replace"))
    args.out.mkdir(parents=True, exist_ok=True)
    target = args.out / f"{build}.json"
    data = json.loads(target.read_text(encoding="utf-8")) if target.exists() else {}
    data.update({"build": build, "game": args.game, "addresses": addresses, "values": values, "from": f"ini_to_build.py ({args.ini.name})"})
    target.write_text(json.dumps(data, separators=(",", ":")), encoding="utf-8")
    print(f"{target}: build {build}, {len(addresses)} addresses, {len(values)} values" + (f", source kept: {data['source']}" if "source" in data else ""))


if __name__ == "__main__":
    main()
