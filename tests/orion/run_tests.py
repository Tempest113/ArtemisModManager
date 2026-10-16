"""Runs the built Orion stub and core inside a stand-in game and checks what they do and what they leave behind.

    python tests/orion/run_tests.py [--keep] [-v]

Build first with orion\\build.bat. Everything happens in a scratch folder: a pretend game folder (a tiny exe that calls winmm
the way the game does), a pretend Artemis data folder and a pretend Documents folder, reached through ARTEMIS_DATA_DIR and
ORION_USER_DIR. The real game, its folders and the real Documents are never touched. Needs Visual Studio's C++ tools.
"""
from __future__ import annotations

import argparse
import datetime
import hashlib
import json
import os
import re
import shutil
import struct
import subprocess
import sys
import tempfile
import traceback
from dataclasses import dataclass, field
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
BUILD = ROOT / "orion" / "build"
sys.path.insert(0, str(ROOT / "orion" / "tools"))
import gen_proxy  # noqa: E402

GAME_VERSION = "v4.5.2"
CORE_VERSION = "0.1.0"


def vcvars() -> str:
    vswhere = Path(os.environ.get("ProgramFiles(x86)", r"C:\Program Files (x86)")) / "Microsoft Visual Studio/Installer/vswhere.exe"
    out = subprocess.run([str(vswhere), "-latest", "-products", "*", "-requires", "Microsoft.VisualStudio.Component.VC.Tools.x86.x64",
                          "-property", "installationPath"], capture_output=True, text=True, check=True).stdout.strip()
    return str(Path(out) / "VC/Auxiliary/Build/vcvars64.bat")


def compile_with_msvc(args: str, cwd: Path) -> None:
    r = subprocess.run(f'"{vcvars()}" >nul && cl /nologo {args}', shell=True, capture_output=True, text=True, cwd=cwd)
    if r.returncode != 0:
        raise SystemExit("compile failed:\n" + r.stdout + r.stderr)


def build_id_of(exe: Path) -> str:
    """The build id Orion computes: the exe's PE timestamp and image size."""
    d = exe.read_bytes()
    pe = struct.unpack_from("<I", d, 0x3C)[0]
    stamp = struct.unpack_from("<I", d, pe + 8)[0]
    size = struct.unpack_from("<I", d, pe + 24 + 56)[0]
    return f"{stamp:08x}-{size:x}"


@dataclass
class Mod:
    key: str
    id: str
    mode: str = "ok"
    trusted: bool = True
    hash_override: str | None = None
    enabled: bool = True
    in_settings: bool = True
    in_list: bool = True
    manifest: str | None = None          # raw text instead of the generated manifest
    extra: dict = field(default_factory=dict)  # merged into the generated manifest
    values: dict = field(default_factory=dict)
    files: tuple = ("orion/p.dll",)
    write_files: bool = True
    absolute: bool = False               # the .mod file points at a folder outside the user's mod folder, as Workshop mods do
    archive_only: bool = False           # a zip mod: the .mod file has no path
    dll: bytes | None = None             # the plugin file's bytes, instead of the test plugin's
    counts: bool | None = None           # the user's choice sent as countsForChecksum; None leaves it to the manifest's cosmetic claim


class World:
    """One scenario's pretend machine."""

    def __init__(self, root: Path, name: str, tools: "Tools", mods: list[Mod], *, core=True, build_info=True, master=True,
                 game_exe: Path | None = None):
        self.root = root / name
        self.game, self.data, self.user = (self.root / d for d in ("game", "data", "user"))
        self.tools, self.mods = tools, mods
        for d in (self.game, self.data, self.user / "mod"):
            d.mkdir(parents=True)
        shutil.copy(game_exe or tools.fake_game, self.game / "stellaris.exe")
        shutil.copy(tools.fake_game, self.game / "dowser.exe")
        shutil.copy(BUILD / "winmm.dll", self.game / "winmm.dll")
        if core:
            (self.data / "orion" / CORE_VERSION).mkdir(parents=True)
            (self.data / "orion" / "current.txt").write_text(CORE_VERSION + "\n")
            shutil.copy(BUILD / "orion.dll", self.data / "orion" / CORE_VERSION / "orion.dll")
        if build_info:
            self.write_build_info(build_id_of(game_exe or tools.fake_game))
        self.order_file = self.root / "order.txt"
        self.write_mods(master)

    def write_build_info(self, build: str, claims: str | None = None) -> None:
        folder = self.data / "orion" / "builds"
        folder.mkdir(parents=True, exist_ok=True)
        (folder / f"{build}.json").write_text(json.dumps({"build": claims or build, "game": GAME_VERSION, "addresses": {"test.value": 0x1234},
                                                           "values": {"test.number": 42}}))

    def proof_file(self, key: str) -> Path:
        return self.root / f"proof-{key}.txt"

    def write_mods(self, master: bool) -> None:
        trust, settings, entries = {}, {}, []
        for m in self.mods:
            content = (self.root / "workshop" / "content" / "281990" / m.key) if m.absolute else (self.user / "mod" / m.key)
            outer = self.user / "mod" / f"{m.key}.mod"
            if m.archive_only:
                outer.write_text(f'name="{m.key}"\narchive="mod/{m.key}.zip"\n')
            else:
                shown = content.as_posix() if m.absolute else f"mod/{m.key}"
                outer.write_text(f'name="{m.key}"\nversion="1"\npath="{shown}"\n')
                content.mkdir(parents=True, exist_ok=True)
                manifest = m.manifest if m.manifest is not None else json.dumps(
                    {"schema": 1, "id": m.id, "name": m.key, "version": "1", "apiVersion": 1, "files": list(m.files),
                     "cosmetic": True, **m.extra})  # cosmetic unless a test says otherwise, so plugins need no honest-mode support in the stand-in game
                (content / "modloader.json").write_text(manifest, encoding="utf-8")
                hashes = {}
                for f in m.files:
                    if m.write_files:
                        (content / f).parent.mkdir(parents=True, exist_ok=True)
                        (content / f).write_bytes(m.dll or self.tools.plugin_bytes)
                    hashes[f] = m.hash_override or hashlib.sha256(m.dll or self.tools.plugin_bytes).hexdigest()
                if m.trusted:
                    trust[m.key] = {"name": m.key, "files": hashes, "followUpdates": False, "autoApprove": False, "approvedUtc": "2026-10-15T00:00:00Z"}
            if m.in_settings:
                values = {"mode": m.mode, "proof": str(self.proof_file(m.key)), "order": str(self.order_file), "who": m.id, **m.values}
                settings[m.key] = {"id": m.id, "enabled": m.enabled, "values": values}
                if m.counts is not None:
                    settings[m.key]["countsForChecksum"] = m.counts
            if m.in_list:
                entries.append(f"mod/{m.key}.mod")
        (self.data / "trust.json").write_text(json.dumps({"version": 1, "plugins": trust}), encoding="utf-8")
        (self.data / "plugin-settings.json").write_text(
            json.dumps({"version": 1, "playset": "test", "pluginsEnabled": master, "mods": settings}, ensure_ascii=False), encoding="utf-8")
        (self.user / "dlc_load.json").write_text(json.dumps({"disabled_dlcs": [], "enabled_mods": entries}))

    def run(self, exe="stellaris.exe", env=None) -> subprocess.CompletedProcess:
        full = dict(os.environ, ARTEMIS_DATA_DIR=str(self.data), ORION_USER_DIR=str(self.user), **(env or {}))
        full.pop("ORION_PLUGINS", None) if not env or "ORION_PLUGINS" not in env else None
        return subprocess.run([str(self.game / exe)], cwd=self.game, env=full, timeout=60, capture_output=True, text=True)

    @property
    def modloader(self) -> Path:
        return self.user / "modloader"

    def status(self) -> dict | None:
        f = self.modloader / "status.json"
        return json.loads(f.read_text(encoding="utf-8")) if f.exists() else None

    def entry(self, key: str) -> dict | None:
        st = self.status()
        return next((p for p in (st or {}).get("plugins", []) if p["mod"] == key), None)

    def proof(self, key: str) -> dict | None:
        f = self.proof_file(key)
        if not f.exists():
            return None
        return dict(line.split("=", 1) for line in f.read_text(encoding="utf-8").splitlines() if "=" in line)

    def order(self) -> list[str]:
        return self.order_file.read_text().split() if self.order_file.exists() else []


class Tools:
    """Things built once: the stand-in game and the test plugin."""

    def __init__(self, work: Path):
        compile_with_msvc(f'/O1 /Fo"{work}\\\\" "{Path(__file__).with_name("fake_game.c")}" /Fe:"{work}\\fake_game.exe"', work)
        compile_with_msvc(f'/O1 /LD /MT /I"{ROOT / "sdk" / "include"}" /Fo"{work}\\\\" "{ROOT / "sdk" / "test_plugins" / "test_plugin.cpp"}" '
                          f'/Fe:"{work}\\test_plugin.dll"', work)
        # the honest-mode spike's stand-ins (forward slashes: cl accepts them and they need no escaping)
        w = work.as_posix()
        compile_with_msvc(f'/O1 /EHsc /MT /Fo"{w}/" "{(Path(__file__).with_name("fake_physfs_game.cpp")).as_posix()}" /Fe:"{w}/fake_physfs_game.exe"', work)
        compile_with_msvc(f'/O1 /LD /MT /EHsc /I"{(ROOT / "sdk" / "include").as_posix()}" /Fo"{w}/" "{(ROOT / "sdk" / "test_plugins" / "honest_spike.cpp").as_posix()}" '
                          f'/Fe:"{w}/honest_spike.dll"', work)
        # the sampling profiler and a stand-in game that spends a few seconds in a known call chain
        compile_with_msvc(f'/O1 /LD /MT /EHsc /W4 /I"{(ROOT / "sdk" / "include").as_posix()}" /Fo"{w}/" "{(ROOT / "sdk" / "test_plugins" / "profiler.cpp").as_posix()}" '
                          f'/Fe:"{w}/profiler.dll"', work)
        compile_with_msvc(f'/O1 /Ob0 /Fo"{w}/" "{(Path(__file__).with_name("fake_busy_game.c")).as_posix()}" /Fe:"{w}/fake_busy_game.exe"', work)
        self.fake_busy_game = work / "fake_busy_game.exe"
        self.profiler_bytes = (work / "profiler.dll").read_bytes()
        self.fake_physfs_game = work / "fake_physfs_game.exe"
        self.spike_bytes = (work / "honest_spike.dll").read_bytes()
        self.fake_game = work / "fake_game.exe"
        self.plugin_bytes = (work / "test_plugin.dll").read_bytes()
        self.build_id = build_id_of(self.fake_game)


class Case:
    def __init__(self, name: str):
        self.name, self.failures = name, []

    def check(self, cond, msg: str) -> None:
        if not cond:
            self.failures.append(msg)

    def game_ok(self, r: subprocess.CompletedProcess) -> None:
        self.check(r.returncode == 0, f"the game should start and exit 0, got {r.returncode}: {r.stdout.strip()!r} {r.stderr.strip()!r}")


def skipped(case: Case, w: World, key: str, reason: str, state: str = "skipped") -> None:
    e = w.entry(key)
    case.check(e is not None, f"{key}: no entry in status.json")
    if e:
        case.check(e["state"] == state and e["reason"] == reason, f"{key}: wanted {state}/{reason}, got {e['state']}/{e['reason']} ({e['detail']})")
        case.check(e["detail"], f"{key}: the detail should say something")
    case.check(w.proof(key) is None, f"{key}: the plugin must not have run")


# ---- scenarios ----

def loads_and_forwards(c: Case, root: Path, tools: Tools) -> None:
    mod = Mod("alpha", "demo.alpha", values={"count": 7, "speed": 2.5, "flag": True, "title": "héllo ☃"})
    w = World(root, "loads", tools, [mod])
    r = w.run()
    c.game_ok(r)
    st = w.status()
    c.check(st is not None, "status.json was not written")
    if st:
        c.check(st["version"] == 1 and st["orion"] == CORE_VERSION, f"version/orion fields: {st.get('version')} {st.get('orion')}")
        c.check(st["build"] == tools.build_id, f"build {st['build']} != {tools.build_id}")
        c.check(st["game"] == GAME_VERSION and st["buildSupported"] is True, "game version / buildSupported")
        try:
            datetime.datetime.strptime(st["startedUtc"], "%Y-%m-%dT%H:%M:%SZ")
        except ValueError:
            c.check(False, f"startedUtc isn't ISO 8601 UTC: {st['startedUtc']}")
    e = w.entry("alpha")
    c.check(e is not None and e["state"] == "loaded" and e["id"] == "demo.alpha", f"alpha should be loaded: {e}")
    p = w.proof("alpha")
    c.check(p is not None, "the plugin didn't run")
    if p:
        want = {"size_ok": "1", "api": "1", "orion": CORE_VERSION, "build": tools.build_id, "game": GAME_VERSION, "base_ok": "1", "flag": "1", "count": "7",
                "speed": "2.500", "title": "héllo ☃", "memory": "round trip", "address_offset": "1234", "missing_is_null": "1", "missing_setting": "0",
                "build_value": "1:42", "build_value_missing": "0"}
        for k, v in want.items():
            c.check(p.get(k) == v, f"proof {k}: wanted {v!r}, got {p.get(k)!r}")
    c.check(not (w.modloader / "loading.json").exists(), "loading.json should be removed when start-up is over")
    plugin_log = w.modloader / "plugins" / "alpha.log"
    c.check(plugin_log.exists() and "test plugin starting" in plugin_log.read_text(), "the plugin's own log is missing its line")
    log = w.modloader / "orion.log"
    c.check(log.exists() and re.match(r"\[\d\d:\d\d:\d\d\] info: Orion " + re.escape(CORE_VERSION) + " started", log.read_text()), "orion.log missing or oddly formatted")
    copy = w.data / "orion" / "cache" / (hashlib.sha256(tools.plugin_bytes).hexdigest() + ".dll")
    c.check(copy.exists() and copy.read_bytes() == tools.plugin_bytes, "the content-addressed copy is missing or differs")


def other_processes_untouched(c: Case, root: Path, tools: Tools) -> None:
    w = World(root, "dowser", tools, [Mod("alpha", "demo.alpha")])
    c.game_ok(w.run("dowser.exe"))  # the exe still runs, so its winmm calls were forwarded
    c.check(not w.modloader.exists(), "the Launcher's dowser.exe must not make Orion write anything")
    c.check(w.proof("alpha") is None, "a plugin loaded into dowser.exe")
    c.check(not (w.data / "orion" / "cache").exists(), "nothing should have been copied to the cache")


def no_core(c: Case, root: Path, tools: Tools) -> None:
    w = World(root, "nocore", tools, [Mod("alpha", "demo.alpha")], core=False)
    c.game_ok(w.run())
    c.check(not w.modloader.exists() and w.proof("alpha") is None, "with no core installed nothing should happen")
    for text, name in (("..\n", "dotdot"), ("..\\..\\x\n", "path"), ("9.9.9\n", "missing")):
        w2 = World(root, "nocore-" + name, tools, [Mod("alpha", "demo.alpha")], core=False)
        (w2.data / "orion").mkdir(parents=True, exist_ok=True)
        (w2.data / "orion" / "current.txt").write_text(text)
        c.game_ok(w2.run())
        c.check(not w2.modloader.exists() and w2.proof("alpha") is None, f"current.txt {text!r} should lead nowhere")


def unknown_build(c: Case, root: Path, tools: Tools) -> None:
    w = World(root, "nobuild", tools, [Mod("alpha", "demo.alpha")], build_info=False)
    c.game_ok(w.run())
    skipped(c, w, "alpha", "buildUnsupported")
    st = w.status()
    c.check(st and st["buildSupported"] is False and st["build"] == tools.build_id, "status should say the build is unsupported")
    w2 = World(root, "otherbuild", tools, [Mod("alpha", "demo.alpha")], build_info=False)
    w2.write_build_info(tools.build_id, claims="00000000-1000")  # a file under this build's name that claims another build
    c.game_ok(w2.run())
    skipped(c, w2, "alpha", "buildUnsupported")
    w3 = World(root, "otherfile", tools, [Mod("alpha", "demo.alpha")], build_info=False)
    w3.write_build_info("00000000-1000")  # data for a different build only
    c.game_ok(w3.run())
    skipped(c, w3, "alpha", "buildUnsupported")


def build_allowed_by_the_manager(c: Case, root: Path, tools: Tools) -> None:
    """The build file Artemis writes when the user allows an unverified build (OrionBuilds.Allow) must be one Orion accepts."""
    w = World(root, "allowed", tools, [Mod("alpha", "demo.alpha")], build_info=False)
    folder = w.data / "orion" / "builds"
    folder.mkdir(parents=True)
    (folder / f"{tools.build_id}.json").write_text(json.dumps({"build": tools.build_id, "game": GAME_VERSION, "source": "user", "addresses": {}}, separators=(",", ":")))
    c.game_ok(w.run())
    c.check((w.entry("alpha") or {}).get("state") == "loaded", f"a plugin should load on a build the user allowed: {w.entry('alpha')}")
    p = w.proof("alpha") or {}
    c.check(p.get("address_offset") == "0" and p.get("missing_is_null") == "1", f"with no addresses, resolve should give null: {p}")


def trust_checks(c: Case, root: Path, tools: Tools) -> None:
    w = World(root, "trust", tools, [
        Mod("fresh", "t.fresh", trusted=False),
        Mod("changed", "t.changed", hash_override="0" * 64),
        Mod("gone", "t.gone", write_files=False),
        Mod("good", "t.good"),
    ])
    c.game_ok(w.run())
    skipped(c, w, "fresh", "untrusted")
    skipped(c, w, "changed", "hashChanged")
    skipped(c, w, "gone", "fileMissing")
    c.check((w.entry("good") or {}).get("state") == "loaded", "the trusted one should still load next to the others")
    w2 = World(root, "trust-upper", tools, [Mod("good", "t.good", hash_override=hashlib.sha256(tools.plugin_bytes).hexdigest().upper())])
    c.game_ok(w2.run())
    c.check((w2.entry("good") or {}).get("state") == "loaded", "an upper-case hash should match")


def switches(c: Case, root: Path, tools: Tools) -> None:
    w = World(root, "master", tools, [Mod("alpha", "demo.alpha")], master=False)
    c.game_ok(w.run())
    skipped(c, w, "alpha", "disabled")
    w = World(root, "envoff", tools, [Mod("alpha", "demo.alpha")])
    c.game_ok(w.run(env={"ORION_PLUGINS": "OFF"}))
    skipped(c, w, "alpha", "disabled")
    w = World(root, "modoff", tools, [Mod("alpha", "demo.alpha", enabled=False)])
    c.game_ok(w.run())
    skipped(c, w, "alpha", "disabled")
    w = World(root, "unlisted", tools, [Mod("alpha", "demo.alpha", in_settings=False)])
    c.game_ok(w.run())
    skipped(c, w, "alpha", "disabled")
    w = World(root, "notinlist", tools, [Mod("alpha", "demo.alpha", in_list=False)])
    c.game_ok(w.run())
    c.check(w.entry("alpha") is None and w.proof("alpha") is None, "a mod that isn't in dlc_load.json must not load or be reported")


def init_fails(c: Case, root: Path, tools: Tools) -> None:
    w = World(root, "initfail", tools, [Mod("bad", "t.bad", mode="fail"), Mod("good", "t.good")])
    c.game_ok(w.run())
    e = w.entry("bad")
    c.check(e and e["state"] == "failed" and e["reason"] == "error" and "returned 3" in e["detail"], f"bad: {e}")
    c.check((w.entry("good") or {}).get("state") == "loaded", "a plugin after a failed one should still load")
    c.check(not (w.modloader / "loading.json").exists(), "a handled failure must not leave the crash marker")


def init_crashes(c: Case, root: Path, tools: Tools) -> None:
    w = World(root, "crash", tools, [Mod("good", "t.good"), Mod("boom", "t.boom", mode="crash"), Mod("after", "t.after")])
    (w.modloader / "plugins").mkdir(parents=True)
    (w.modloader / "status.json").write_text(json.dumps({"version": 1, "plugins": [{"mod": "stale", "id": "x", "state": "loaded"}]}))
    r = w.run()
    c.check(r.returncode != 0, "a plugin that crashes in init must take the game down (that is what the marker is for)")
    marker = w.modloader / "loading.json"
    c.check(marker.exists(), "loading.json should be left behind")
    if marker.exists():
        m = json.loads(marker.read_text())
        c.check(m == {"mod": "boom", "id": "t.boom", "file": "orion/p.dll"}, f"marker: {m}")
    c.check(w.proof("after") is None, "nothing after the crash can have run")
    c.check(w.entry("stale") is None, "the previous session's status should be replaced at the start")
    c.check((w.entry("good") or {}).get("state") == "loaded", "what loaded before the crash is in status.json")


def order_and_dependencies(c: Case, root: Path, tools: Tools) -> None:
    w = World(root, "order", tools, [
        Mod("first", "p.first", extra={"dependencies": ["p.a"]}),
        Mod("a", "p.a"),
        Mod("c", "p.c", extra={"dependencies": ["p.a"]}),
        Mod("d", "p.d", extra={"incompatible": ["p.a"]}),
        Mod("e", "p.e", extra={"incompatible": ["p.f"]}),
        Mod("f", "p.f"),
    ])
    c.game_ok(w.run())
    skipped(c, w, "first", "dependencyMissing")
    skipped(c, w, "d", "incompatible")
    skipped(c, w, "f", "incompatible")
    for key in ("a", "c", "e"):
        c.check((w.entry(key) or {}).get("state") == "loaded", f"{key} should load")
    c.check(w.order() == ["p.a", "p.c", "p.e"], f"load order: {w.order()}")
    st = w.status()
    c.check([p["mod"] for p in st["plugins"]] == ["first", "a", "c", "d", "e", "f"], "status.json should list plugins in load-list order")


def versions_and_api(c: Case, root: Path, tools: Tools) -> None:
    w = World(root, "versions", tools, [
        Mod("api", "v.api", extra={"apiVersion": 99}),
        Mod("old", "v.old", extra={"builds": ["v3.*"]}),
        Mod("minor", "v.minor", extra={"builds": ["v4.5.*"]}),
        Mod("multi", "v.multi", extra={"builds": ["v3.*", "v4.*"]}),
        Mod("exact", "v.exact", extra={"builds": ["v4.5.2"]}),
        Mod("next", "v.next", extra={"builds": ["v4.6.*"]}),
    ])
    c.game_ok(w.run())
    skipped(c, w, "api", "apiUnsupported")
    skipped(c, w, "old", "buildUnsupported")
    skipped(c, w, "next", "buildUnsupported")
    for key in ("minor", "multi", "exact"):
        c.check((w.entry(key) or {}).get("state") == "loaded", f"{key} should load")


def manifests_and_paths(c: Case, root: Path, tools: Tools) -> None:
    lenient = '{ // a comment, which Artemis allows\n "id": "m.lenient", "apiVersion": 1, "cosmetic": true, "files": ["orion/p.dll",],\n}'
    w = World(root, "manifests", tools, [
        Mod("badjson", "m.bad", manifest="{ nope"),
        Mod("escape", "m.escape", manifest='{"id":"m.escape","apiVersion":1,"files":["../evil.dll"]}'),
        Mod("lenient", "m.lenient", manifest=lenient),
        Mod("ws", "m.ws", absolute=True),
        Mod("has space", "m.space"),
        Mod("zip", "m.zip", archive_only=True),
        Mod("backslash", "m.back", manifest='{"id":"m.back","apiVersion":1,"cosmetic":true,"files":["orion\\\\p.dll"]}', files=("orion/p.dll",)),
    ])
    c.game_ok(w.run())
    skipped(c, w, "badjson", "error", state="failed")
    skipped(c, w, "escape", "error", state="failed")
    for key in ("lenient", "ws", "has space"):
        c.check((w.entry(key) or {}).get("state") == "loaded", f"{key} should load: {w.entry(key)}")
    c.check((w.modloader / "plugins" / "has space.log").exists(), "a plugin's log is named after its mod key, spaces and all")
    c.check(w.entry("zip") is None, "a zip mod has no plugin and must not be reported")
    c.check((w.entry("backslash") or {}).get("state") == "loaded", "backslashes in files should be read as slashes, like Artemis does")


def cache_is_checked(c: Case, root: Path, tools: Tools) -> None:
    w = World(root, "cache", tools, [Mod("alpha", "demo.alpha")])
    c.game_ok(w.run())
    copy = w.data / "orion" / "cache" / (hashlib.sha256(tools.plugin_bytes).hexdigest() + ".dll")
    c.check(copy.exists(), "no cached copy after the first run")
    copy.write_bytes(b"not the plugin at all")  # someone tampers with the private copy
    w.proof_file("alpha").unlink()
    c.game_ok(w.run())
    c.check((w.entry("alpha") or {}).get("state") == "loaded" and w.proof("alpha") is not None, "a tampered copy should be replaced, then load")
    c.check(copy.read_bytes() == tools.plugin_bytes, "the cached copy should hold the approved bytes again")
    # a rebuilt DLL gets its own copy; the old one stays, so a running game's file is never replaced
    rebuilt = tools.plugin_bytes + b"\0" * 16
    (w.user / "mod" / "alpha" / "orion" / "p.dll").write_bytes(rebuilt)
    trust = json.loads((w.data / "trust.json").read_text())
    trust["plugins"]["alpha"]["files"]["orion/p.dll"] = hashlib.sha256(rebuilt).hexdigest()
    (w.data / "trust.json").write_text(json.dumps(trust))
    w.proof_file("alpha").unlink()
    c.game_ok(w.run())
    c.check(w.proof("alpha") is not None, "the rebuilt, re-approved plugin should load")
    c.check(len(list((w.data / "orion" / "cache").glob("*.dll"))) == 2, "old and new copies should both exist")


def honest_mode_spike(c: Case, root: Path, tools: Tools) -> None:
    """The spike plugin against a stand-in game that exports PhysFS under the real C++ names: it watches the search path, mounts a zip holding the
    fingerprint file once the mod's own folder is there, and what it mounts opens as a valid zip."""
    import zipfile

    def spike_world(name: str, mode: str) -> World:
        return World(root, name, tools, [Mod("Orion Honest Spike", "artemis.honest-spike", mode=mode, dll=tools.spike_bytes, values={"mountAfter": "Orion Honest Spike"})],
                     game_exe=tools.fake_physfs_game)

    w = spike_world("spike-mount", "mount")
    out = w.root / "mounted.zip"
    c.game_ok(w.run(env={"FAKE_MOUNT_OUT": str(out)}))
    c.check((w.entry("Orion Honest Spike") or {}).get("state") == "loaded", f"the spike should load: {w.entry('Orion Honest Spike')}")
    log_file = w.modloader / "plugins" / "Orion Honest Spike.log"
    log = log_file.read_text(encoding="utf-8") if log_file.exists() else ""
    for want in ("watching CreateFileW: yes", "PhysFS is initialised", 'first CreateFileW touching "dlc_load.json"', "search path +C:\\no-such-folder\\game",
                 "mounting the fingerprint now (trigger: search path has", "returned 1", "exists(common/modloader/manifest.txt) = 1"):
        c.check(want in log, f"the spike's log should say {want!r}")
    c.check(log.find("search path +C:\\no-such-folder\\user\\mod\\Orion Honest Spike") != -1 and
            log.find("search path +C:\\no-such-folder\\user\\mod\\Orion Honest Spike") < log.find("mounting the fingerprint now"), "it should wait for the mod's own folder")
    c.check(out.exists(), "the game was never asked to mount the archive")
    if out.exists():
        with zipfile.ZipFile(out) as z:
            c.check(z.testzip() is None, "the archive's checksums should hold")
            c.check("common/modloader/manifest.txt" in z.namelist(), f"the fingerprint file is missing: {z.namelist()}")
            c.check(z.read("common/modloader/manifest.txt").decode().startswith("# Orion honest-mode fingerprint"), "the fingerprint file's content")

    w = spike_world("spike-observe", "observe")
    out2 = w.root / "mounted.zip"
    c.game_ok(w.run(env={"FAKE_MOUNT_OUT": str(out2)}))
    c.check(not out2.exists(), "observe mode must not mount anything")
    c.check("PhysFS is initialised" in (w.modloader / "plugins" / "Orion Honest Spike.log").read_text(encoding="utf-8"), "observe mode should still log the timeline")

    w = World(root, "spike-nophysfs", tools, [Mod("Orion Honest Spike", "artemis.honest-spike", mode="mount", dll=tools.spike_bytes)])
    c.game_ok(w.run())
    e = w.entry("Orion Honest Spike")
    c.check(e and e["state"] == "failed" and "returned 2" in e["detail"], f"a game without PhysFS should make the spike fail cleanly: {e}")


def honest_world(root: Path, name: str, tools: Tools, mods: list[Mod], **kw) -> World:
    return World(root, name, tools, mods, game_exe=tools.fake_physfs_game, **kw)


def run_honest(w: World, **env) -> Path:
    """Runs the PhysFS stand-in with the mods' folders as what the game mounts (written with forward slashes, as the real game's search path is)."""
    out = w.root / "mounted.zip"
    mounts = ";".join((w.user / "mod" / m.key).as_posix() for m in w.mods if m.in_list)
    w.last = w.run(env={"FAKE_MOUNTS": mounts, "FAKE_MOUNT_OUT": str(out), "FAKE_LINGER_MS": "1200", **env})
    return out


def fingerprint_of(zip_path: Path) -> str | None:
    import zipfile
    if not zip_path.exists():
        return None
    with zipfile.ZipFile(zip_path) as z:
        assert z.testzip() is None
        return z.read("common/modloader/manifest.txt").decode()


def honest_mode(c: Case, root: Path, tools: Tools) -> None:
    sha = hashlib.sha256(tools.plugin_bytes).hexdigest()
    mods = [Mod("zeta", "p.zeta", extra={"cosmetic": False}), Mod("calm", "p.calm", extra={"cosmetic": True}),
            Mod("alpha", "p.alpha", extra={"cosmetic": False, "version": "2.0"})]
    w = honest_world(root, "honest", tools, mods)
    out = run_honest(w)
    c.game_ok(w.last)
    for key in ("zeta", "calm", "alpha"):
        c.check((w.entry(key) or {}).get("state") == "loaded", f"{key} should load: {w.entry(key)}")
    text = fingerprint_of(out)
    c.check(text is not None, "no fingerprint was mounted")
    if text:
        lines = [l for l in text.splitlines() if l and not l.startswith("#")]
        c.check(len(lines) == 2, f"two plugins count, so two lines: {lines}")
        c.check(lines[0].startswith('plugin = { id = "p.alpha" version = "2.0" ') and lines[1].startswith('plugin = { id = "p.zeta" version = "1" '),
                f"sorted by id, with each version: {lines}")
        c.check(f'path = "orion/p.dll" sha256 = "{sha}"' in lines[0], f"the file's hash is in the line: {lines[0]}")
        c.check("p.calm" not in text, "a cosmetic plugin must stay out of the fingerprint")
    st = w.status()["honest"]
    c.check(st["state"] == "mounted" and st["plugins"] == 2 and st["detail"].startswith("2 plugins count"), f"honest status: {st}")
    log = (w.modloader / "orion.log").read_text(encoding="utf-8")
    c.check("mounted the fingerprint after the game mounted its mods" in log, "orion.log should say it waited for the game's mods")

    # the same plugins give the same bytes whatever the order of the list
    w2 = honest_world(root, "honest-reordered", tools, [mods[2], mods[1], mods[0]])
    out2 = run_honest(w2)
    c.check(fingerprint_of(out2) == text and out2.read_bytes() == out.read_bytes(), "the fingerprint must not depend on the load order")


def honest_mode_not_needed_or_unavailable(c: Case, root: Path, tools: Tools) -> None:
    w = honest_world(root, "honest-none", tools, [Mod("calm", "p.calm", extra={"cosmetic": True})])
    out = run_honest(w)
    c.game_ok(w.last)
    c.check(not out.exists(), "a cosmetic plugin alone must not change the game's checksum")
    c.check(w.status()["honest"]["state"] == "notNeeded", f"honest status: {w.status()['honest']}")

    # a game that doesn't export its file system: plugins that count can't load, cosmetic ones can
    w = World(root, "honest-unavailable", tools, [Mod("zeta", "p.zeta", extra={"cosmetic": False}), Mod("calm", "p.calm", extra={"cosmetic": True})])
    c.game_ok(w.run())
    skipped(c, w, "zeta", "buildUnsupported")
    c.check("checksum" in (w.entry("zeta") or {}).get("detail", ""), "the detail should explain the checksum")
    c.check((w.entry("calm") or {}).get("state") == "loaded", "a cosmetic plugin doesn't need honest mode")
    c.check(w.status()["honest"]["state"] == "notNeeded", "nothing counted, nothing to mount")


def honest_mode_user_choice(c: Case, root: Path, tools: Tools) -> None:
    w = honest_world(root, "honest-choice", tools, [Mod("claims", "p.claims", extra={"cosmetic": True}, counts=True),
                                                    Mod("honest", "p.honest", extra={"cosmetic": False}, counts=False)])
    out = run_honest(w)
    c.game_ok(w.last)
    text = fingerprint_of(out) or ""
    c.check("p.claims" in text and "p.honest" not in text, f"the user's choice should win over the manifest's claim: {text!r}")


def honest_mode_keeps_it_there(c: Case, root: Path, tools: Tools) -> None:
    w = honest_world(root, "honest-drop", tools, [Mod("zeta", "p.zeta", extra={"cosmetic": False})])
    run_honest(w, FAKE_DROP_ARCHIVE="1", FAKE_LINGER_MS="1500")
    c.game_ok(w.last)
    log = (w.modloader / "orion.log").read_text(encoding="utf-8")
    c.check("the game dropped the fingerprint from its file list; mounting it again" in log, "a dropped archive should be mounted again")
    c.check(w.status()["honest"]["state"] == "mounted", f"honest status: {w.status()['honest']}")

    # the last mod never shows up in the game's list: mount anyway after the wait
    w = honest_world(root, "honest-timeout", tools, [Mod("zeta", "p.zeta", extra={"cosmetic": False}), Mod("never", "p.never", extra={"cosmetic": True})])
    out = w.root / "mounted.zip"
    w.last = w.run(env={"FAKE_MOUNTS": (w.user / "mod" / "zeta").as_posix(), "FAKE_MOUNT_OUT": str(out), "FAKE_LINGER_MS": "1200", "ORION_HONEST_WAIT_MS": "300"})
    c.game_ok(w.last)
    c.check(out.exists(), "after the wait the fingerprint should be mounted anyway")
    c.check("without seeing the last mod mounted" in (w.modloader / "orion.log").read_text(encoding="utf-8"), "orion.log should say it gave up waiting")


def game_start_and_unsupported(c: Case, root: Path, tools: Tools) -> None:
    """host->onGameStart runs the callback once, on the thread the plugin started on, when that thread first reads the exe's own common folder; another
    thread reading it first doesn't count. A plugin that returns ORION_INIT_UNSUPPORTED is skipped, not failed."""
    proof = None
    w = honest_world(root, "gamestart", tools, [Mod("watcher", "p.watcher", values={"startProof": ""})])
    proof = w.root / "start.txt"
    s = json.loads((w.data / "plugin-settings.json").read_text(encoding="utf-8"))
    s["mods"]["watcher"]["values"]["startProof"] = str(proof)
    (w.data / "plugin-settings.json").write_text(json.dumps(s))
    c.game_ok(w.run(env={"FAKE_MOUNTS": (w.user / "mod" / "watcher").as_posix()}))
    c.check(proof.exists(), "the game-start callback never ran")
    if proof.exists():
        got = dict(l.split("=") for l in proof.read_text().split())
        c.check(got == {"calls": "1", "main_thread": "1"}, f"once, on the main thread: {got}")
    c.check("onGameStart scheduled: 1" in (w.modloader / "plugins" / "watcher.log").read_text(encoding="utf-8"), "the plugin should be told it was scheduled")
    log = (w.modloader / "orion.log").read_text(encoding="utf-8")
    c.check("the game started reading its own scripts" in log and "from-another-thread" not in log, "orion.log should show the main thread's read, not the other thread's")

    w = World(root, "unsupported", tools, [Mod("old", "p.old", mode="unsupported")])
    c.game_ok(w.run())
    skipped(c, w, "old", "buildUnsupported")
    c.check(w.proof("old") is None, "an unsupported plugin writes nothing")


def ini_to_build_tool(c: Case, root: Path, tools: Tools) -> None:
    import ini_to_build  # orion/tools, already on the path
    text = "# a comment\nbuild_timestamp=0x5f000000\nbuild_size_of_image=0x1234000\neffect_db_global_rva=0x100abc0\nkw_xor_donor_key=1234\nflag=1 # trailing words\nbad line\n"
    build, addresses, values = ini_to_build.convert(text)
    c.check(build == "5f000000-1234000", f"build id: {build}")
    c.check(addresses == {"effect_db_global": 0x100ABC0}, f"addresses: {addresses}")
    c.check(values == {"kw_xor_donor_key": 1234, "flag": 1}, f"values: {values}")
    # what it writes is what Orion reads: a plugin gets the address and the value, and a user's permission already on file is kept
    out = root / "converted"
    out.mkdir()
    (out / f"{build}.json").write_text(json.dumps({"build": build, "game": "v0", "source": "user", "addresses": {}}))
    import subprocess as sp
    ini = out / "a.ini"
    ini.write_text(text)
    r = sp.run([sys.executable, str(ROOT / "orion" / "tools" / "ini_to_build.py"), str(ini), "--game", "v4.5.2", "--out", str(out)], capture_output=True, text=True)
    c.check(r.returncode == 0, r.stdout + r.stderr)
    data = json.loads((out / f"{build}.json").read_text())
    c.check(data["source"] == "user" and data["game"] == "v4.5.2" and data["addresses"] == addresses and data["values"] == values, f"merged file: {data}")


def sampling_profiler(c: Case, root: Path, tools: Tools) -> None:
    """The profiler plugin samples a stand-in game for two seconds (autoSeconds), and its report shows the game's hot call chain on its busiest thread
    and a sleeping thread as waiting; the report tool names the functions it is given names for."""
    import profile_report  # orion/tools, already on the path

    w = World(root, "profiler", tools, [Mod("Orion Sampling Profiler", "artemis.sampling-profiler", dll=tools.profiler_bytes,
                                            values={"autoSeconds": 2, "intervalMs": 2, "hotkey": 0})], game_exe=tools.fake_busy_game)
    r = w.run()
    c.game_ok(r)
    where = dict(kv.split("=") for kv in r.stdout.split() if "=" in kv)
    c.check((w.entry("Orion Sampling Profiler") or {}).get("state") == "loaded", f"the profiler should load: {w.entry('Orion Sampling Profiler')}")
    reports = sorted((w.modloader / "profiles").glob("profile-*.txt"))
    c.check(len(reports) == 1, f"one report was expected: {reports}")
    log = w.modloader / "plugins" / "Orion Sampling Profiler.log"
    text = log.read_text(encoding="utf-8") if log.exists() else ""
    c.check("sampling started" in text and "sampling stopped" in text and "report: " in text, f"the plugin's log should tell the story: {text!r}")
    if not reports or "leaf" not in where:
        return
    rep = profile_report.parse(reports[0].read_text(encoding="utf-8"))
    base, leaf, flat, parent = (int(where[k], 16) for k in ("base", "leaf", "flat", "parent"))
    c.check(rep.header["build"] == build_id_of(tools.fake_busy_game) and rep.exe_base == base, f"header: {rep.header}")
    c.check(int(rep.header["cycles"]) > 200, f"too few sampling cycles in two seconds: {rep.header['cycles']}")
    c.check(any(m[0] == base for m in rep.modules), "the exe should be in the module list")
    busy = max(rep.threads, key=lambda i: rep.threads[i]["in_exe"])
    c.check(rep.threads[busy]["in_exe"] > 0.8 * rep.threads[busy]["samples"] > 100, f"the busy thread should be mostly in the exe: {rep.threads[busy]}")
    waiting = [t for i, t in rep.threads.items() if i != busy and t["samples"] > 100 and t["in_exe"] < 0.2 * t["samples"]]
    c.check(waiting, f"the sleeping thread should show as waiting outside the exe: {rep.threads}")
    # the busy thread's samples end in HotLeaf (a function with unwind data) and in FlatLeaf (none: found from the gap after the previous entry), both called by HotParent
    root_node = next(n for n, t in rep.roots.items() if t == busy)
    ends = [n for n, v in rep.nodes.items() if v[2] > 0 and _descends_from(rep, n, root_node)]
    in_leaf = sum(rep.nodes[n][2] for n in ends if rep.nodes[n][1] == leaf and rep.nodes[rep.nodes[n][0]][1] == parent)
    in_flat = sum(rep.nodes[n][2] for n in ends if 0 <= rep.nodes[n][1] - flat < 0x20 and rep.nodes[rep.nodes[n][0]][1] == parent)
    c.check(in_leaf > 50, f"HotLeaf (0x{leaf:x}, called by HotParent 0x{parent:x}) should be where many samples end: {in_leaf} of {rep.threads[busy]['samples']}")
    c.check(in_flat > 50, f"FlatLeaf (0x{flat:x}, no unwind data) should be found at its likely start: {in_flat} of {rep.threads[busy]['samples']}")
    # names: the report tool uses a names file in Exception Translator's shape, and falls back to sub_<rva> without one
    names = root / "profiler-names.json"
    names.write_text(json.dumps({"names": [[leaf - base, "HotLeaf(unsigned)", "test", 1.0], [parent - base, "HotParent(unsigned)", "test", 1.0]]}))
    shown = profile_report.render(rep, profile_report.Namer(rep, names), 1, 5, [("hot chain", __import__("re").compile("Hot"))])
    c.check("HotLeaf(unsigned)" in shown and "hot chain" in shown, f"named report:\n{shown}")
    calls = profile_report.tree(rep, profile_report.Namer(rep, names), busy, None, 6, 1.0, False)
    c.check("HotParent(unsigned)" in calls and "HotLeaf(unsigned)" in calls.split("HotParent(unsigned)", 1)[1], f"the call tree should show HotLeaf under HotParent:\n{calls}")
    plain = profile_report.render(rep, profile_report.Namer(rep, None), 1, 5, [])
    c.check(f"sub_{leaf - base:x}" in plain, "without names the hot function should show as sub_<rva>")


def _descends_from(rep, node: int, root: int) -> bool:
    while node in rep.nodes:
        node = rep.nodes[node][0]
    return node == root


def export_parity(c: Case, root: Path, tools: Tools) -> None:
    real = set(gen_proxy.read_exports(Path(os.environ.get("SystemRoot", r"C:\Windows")) / "System32" / "winmm.dll"))
    ours = set(gen_proxy.read_exports(BUILD / "winmm.dll"))
    c.check(real == ours, f"the stub's exports differ from System32's winmm: missing {sorted(real - ours)[:5]}, extra {sorted(ours - real)[:5]}")
    c.check(len(real) > 150, f"only {len(real)} exports found in the real winmm")
    version = subprocess.run(["powershell", "-NoProfile", "-Command", f"(Get-Item '{BUILD / 'winmm.dll'}').VersionInfo.CompanyName"],
                             capture_output=True, text=True).stdout.strip()
    c.check(version.startswith("Artemis"), f"the stub's company name must start with Artemis (that is how the manager recognises it): {version!r}")


SCENARIOS = [export_parity, sampling_profiler, game_start_and_unsupported, ini_to_build_tool, honest_mode_spike,honest_mode, honest_mode_not_needed_or_unavailable, honest_mode_user_choice, honest_mode_keeps_it_there, loads_and_forwards,other_processes_untouched, no_core, unknown_build, build_allowed_by_the_manager, trust_checks, switches, init_fails, init_crashes,
             order_and_dependencies, versions_and_api, manifests_and_paths, cache_is_checked]


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--keep", action="store_true", help="keep the scratch folder and print where it is")
    args = ap.parse_args()
    if not (BUILD / "winmm.dll").exists() or not (BUILD / "orion.dll").exists():
        raise SystemExit("orion\\build\\winmm.dll and orion.dll not found: run orion\\build.bat first")
    work = Path(tempfile.mkdtemp(prefix="orion-tests-"))
    bad = 0
    try:
        tools = Tools(work)
        root = work / "worlds"
        root.mkdir()
        for scenario in SCENARIOS:
            case = Case(scenario.__name__)
            try:
                scenario(case, root, tools)
            except Exception:  # a scenario that blows up is a failure with its traceback, not a stop
                case.failures.append(traceback.format_exc())
            print(f"[{'PASS' if not case.failures else 'FAIL'}] {case.name}")
            for f in case.failures:
                print("       - " + f.replace("\n", "\n         "))
            bad += bool(case.failures)
        print(f"{len(SCENARIOS) - bad}/{len(SCENARIOS)} scenarios passed")
        return 1 if bad else 0
    finally:
        if args.keep:
            print("kept " + str(work))
        else:
            shutil.rmtree(work, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main())
