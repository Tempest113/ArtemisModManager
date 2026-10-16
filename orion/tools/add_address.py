"""Adds one function's address and first bytes to Orion's per-build data, for a plugin that patches that function (D62).

    python orion/tools/add_address.py --exe <stellaris.exe> --as render_frame --bytes 5 --function "CGameState::RenderFrame" --names <steam-windows.json>
    python orion/tools/add_address.py --exe <stellaris.exe> --as render_frame --bytes 5 --rva 0x24bdd0        (when the address is known another way)

The function is found by name in Exception Translator's per-build file (read only), or given as an RVA. The tool reads the first --bytes bytes of the
function from the exe (read only) and merges three things into <data>\\orion\\builds\\<build id>.json, leaving every other field alone, so the permission
the user gave for the build ("source": "user") and the addresses already there survive: addresses[<as>] = the RVA, values[<as>_prologue] = those bytes as
a little-endian number, values[<as>_prologue_len] = their count. A plugin that overwrites the function's start checks the bytes before it patches.
--bytes must end on an instruction boundary and the instructions must not depend on where they sit (no relative call, jump or address); the tool can't
check that, whoever reads the function's disassembly does. The first time a file is changed a copy is kept next to it as <id>.json.bak.

Game-derived data stays local: this tool is in the repo, the file it edits never is (it is outside the repo, D15).
"""
from __future__ import annotations

import argparse
import json
import os
import shutil
import struct
from pathlib import Path


def read_pe(exe: bytes) -> tuple[str, list[tuple[int, int, int]]]:
    """(build id as Orion computes it, sections as (rva, size, file offset))."""
    pe = struct.unpack_from("<I", exe, 0x3C)[0]
    stamp = struct.unpack_from("<I", exe, pe + 8)[0]
    nsec, optsz = struct.unpack_from("<H", exe, pe + 6)[0], struct.unpack_from("<H", exe, pe + 20)[0]
    size_of_image = struct.unpack_from("<I", exe, pe + 24 + 56)[0]
    secs = []
    for i in range(nsec):
        o = pe + 24 + optsz + 40 * i
        vsize, va, rsize, raw = struct.unpack_from("<IIII", exe, o + 8)
        secs.append((va, max(vsize, rsize), raw))
    return f"{stamp:08x}-{size_of_image:x}", secs


def bytes_at(exe: bytes, secs: list[tuple[int, int, int]], rva: int, n: int) -> bytes:
    for va, size, raw in secs:
        if va <= rva < va + size:
            return exe[raw + rva - va: raw + rva - va + n]
    raise SystemExit(f"RVA 0x{rva:x} is not inside any section of the exe")


def rva_of(names_path: Path, function: str) -> int:
    hits = [rva for rva, name, *_ in json.loads(names_path.read_text(encoding="utf-8"))["names"] if name == function]
    if len(hits) != 1:
        raise SystemExit(f"{function!r} is named {len(hits)} times in {names_path.name}; give --rva instead" if hits else f"{function!r} is not in {names_path.name}")
    return hits[0]


def merge(data: dict, key: str, rva: int, prologue: bytes) -> dict:
    """The build data with the new address and bytes added; nothing else changes."""
    out = dict(data)
    out["addresses"] = {**data.get("addresses", {}), key: rva}
    out["values"] = {**data.get("values", {}), f"{key}_prologue": int.from_bytes(prologue, "little"), f"{key}_prologue_len": len(prologue)}
    return out


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--exe", type=Path, required=True)
    ap.add_argument("--as", dest="key", required=True, help="the name the plugin asks for, such as render_frame")
    ap.add_argument("--bytes", type=int, required=True, help="how many bytes at the function's start to remember (whole instructions, 5 to 8)")
    ap.add_argument("--function", help="the function's name in the names file")
    ap.add_argument("--names", type=Path, help="Exception Translator's web/public/data/<version>/steam-windows.json")
    ap.add_argument("--rva", type=lambda s: int(s, 0), help="the function's offset from the exe's base, instead of --function")
    ap.add_argument("--data-dir", type=Path, default=Path(os.environ.get("APPDATA", ".")) / "Artemis", help="Artemis's data folder")
    args = ap.parse_args()
    if not 5 <= args.bytes <= 8:
        raise SystemExit("--bytes must be 5 to 8")
    if args.rva is None:
        if not (args.function and args.names):
            raise SystemExit("give --rva, or --function together with --names")
        args.rva = rva_of(args.names, args.function)
    exe = args.exe.read_bytes()
    build, secs = read_pe(exe)
    target = args.data_dir / "orion" / "builds" / f"{build}.json"
    if not target.exists():
        raise SystemExit(f"{target} doesn't exist: allow plugins on this build in Artemis first")
    data = json.loads(target.read_text(encoding="utf-8"))
    if data.get("build") != build:
        raise SystemExit(f"{target.name} says it is for build {data.get('build')}, not {build}; nothing changed")
    prologue = bytes_at(exe, secs, args.rva, args.bytes)
    backup = target.with_name(target.name + ".bak")
    if not backup.exists():
        shutil.copy2(target, backup)
    tmp = target.with_name(target.name + ".tmp")
    tmp.write_text(json.dumps(merge(data, args.key, args.rva, prologue), separators=(",", ":")), encoding="utf-8")
    os.replace(tmp, target)
    print(f"{target}: build {build}, added {args.key} with {args.bytes} bytes ({prologue.hex(' ')}); kept {len(data.get('addresses', {}))} addresses, {len(data.get('values', {}))} values"
          + (f", source: {data['source']}" if "source" in data else ""))


if __name__ == "__main__":
    main()
