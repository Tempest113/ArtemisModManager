#!/usr/bin/env python3
"""Turns a report from the sampling-profiler plugin (sdk/test_plugins/profiler.cpp) into names and shares.

    python orion/tools/profile_report.py <profile-....txt> [--names <Exception Translator's steam-windows.json>] [--threads 3] [--top 25]
                                          [--bucket "name=regex" ...] [--out report.txt]

The report holds, per game thread, a tree of call paths with the number of samples that ended in each. This tool adds what the sampler can't know:
function names from Exception Translator's per-build data (read only, named with ~99% precision by matching the Linux build; about a third of the game's
code has no name, shown as sub_<rva>), and for unnamed code the nearest named function above it on the stack. Without --names every function is sub_<rva>.

The report and the names file are game-derived: keep them out of the repository (the profiles land in Documents, and local/ is ignored). The shares are
percentages of ALL samples of that thread, so a thread that waits a lot shows a large "outside the game's code" share (waiting is spent in Windows).
The subsystem shares are inclusive and heuristic: a sample counts for every subsystem whose name pattern matches any function on its stack.
"""
from __future__ import annotations

import argparse
import base64
import bisect
import json
import re
import sys
from collections import Counter, defaultdict
from dataclasses import dataclass, field
from pathlib import Path

DEFAULT_BUCKETS = [
    ("fleet pathfinding", r"AStar|Pathfind|PathFind|FindPath|CalcMovementPath"),
    ("pop and job assignment", r"PopJob|AssignPopsToJobs|EnsurePopJobsAreUpToDate|NPlanetJobs|Workforce"),
    ("pops, species, growth", r"CPop[A-Z:]|CPop$|Species|PopGrowth|Growth"),
    ("planets and colonies", r"CColony|CPlanet"),
    ("AI", r"\bCAI[A-Z]|::CAI[A-Z]|AIMinister|AIBudget"),
    ("script triggers and effects", r"Trigger::|Effect::|Trigger>|Effect>|ScriptedList|ActualEvaluate|ExecuteActual"),
    ("events and on_actions", r"Event|OnAction"),
    ("economy", r"Economic|Resource|Upkeep|Produces|Stockpile"),
    ("fleets and ships", r"CFleet|CShip|Fleet|Ship[A-Z:]"),
    ("combat", r"Combat|Battle|Weapon|Projectile"),
    ("user interface and drawing", r"View|Gui|Window|Render|Draw|Tooltip|Interface|Sprite|Texture"),
]


@dataclass
class Report:
    header: dict[str, str] = field(default_factory=dict)
    modules: list[tuple[int, int, str]] = field(default_factory=list)  # base, size, name
    threads: dict[int, dict] = field(default_factory=dict)
    roots: dict[int, int] = field(default_factory=dict)  # node index -> thread index
    nodes: dict[int, tuple[int, int, int]] = field(default_factory=dict)  # index -> parent, function start, samples ending here

    @property
    def exe_base(self) -> int:
        return int(self.header.get("exe_base", "0"), 16)


def parse(text: str) -> Report:
    r = Report()
    lines = text.splitlines()
    if not lines or not lines[0].startswith("orion-profile "):
        raise ValueError("not an Orion profile report")
    for line in lines[1:]:
        p = line.split()
        if not p:
            continue
        if p[0] == "module":
            r.modules.append((int(p[1], 16), int(p[2], 16), " ".join(p[3:])))
        elif p[0] == "thread":
            r.threads[int(p[1])] = {"tid": int(p[2]), "samples": int(p[3]), "in_exe": int(p[4]), "cpu_ms": int(p[5]), "name": " ".join(p[6:])}
        elif p[0] == "root":
            r.roots[int(p[1])] = int(p[2])
        elif p[0] == "n":
            r.nodes[int(p[1])] = (int(p[2]), int(p[3], 16), int(p[4]))
        else:
            r.header[p[0]] = " ".join(p[1:])
    return r


class Namer:
    """Function names for the game's exe (from Exception Translator's data) and module+offset for everything else."""

    def __init__(self, rep: Report, names_path: Path | None):
        self.rep = rep
        self.labels: dict[int, str] = {}
        self.starts: list[int] = []
        self.ends: list[int] = []
        self.owners: list[int] = []
        self.cache: dict[int, tuple[str, bool]] = {}
        exe = rep.exe_base
        self.exe = next((m for m in rep.modules if m[0] == exe), None)
        if names_path:
            data = json.loads(Path(names_path).read_text(encoding="utf-8"))
            for rva, name, *_ in data.get("names", []):
                self.labels[rva] = name
            fn = data.get("functions")
            if fn and fn.get("encoding") == "varint-delta-v1":
                self._decode(fn["data"], fn["count"])

    def _decode(self, b64: str, count: int) -> None:
        buf, pos = base64.b64decode(b64), 0

        def nxt() -> int:
            nonlocal pos
            result = shift = 0
            while True:
                b = buf[pos]
                pos += 1
                result += (b & 0x7F) << shift
                if b < 0x80:
                    return result
                shift += 7

        prev = 0
        for i in range(count):
            start = prev + nxt()
            self.starts.append(start)
            self.ends.append(start + nxt())
            zz = nxt()
            self.owners.append(i + (zz // 2 if zz % 2 == 0 else -(zz + 1) // 2) if zz else i)
            prev = start

    def module_of(self, fn: int):
        for m in self.rep.modules:
            if m[0] <= fn < m[0] + m[1]:
                return m
        return None

    def label(self, fn: int) -> tuple[str, bool]:
        """(name, is a real name). Names of code outside the exe are module+offset and not 'real'."""
        got = self.cache.get(fn)
        if got:
            return got
        if fn == 0:
            got = ("<code outside every module>", False)
        else:
            m = self.module_of(fn)
            if m is None:
                got = (f"<unknown 0x{fn:x}>", False)
            elif self.exe and m[0] == self.exe[0]:
                rva = fn - m[0]
                name = self.labels.get(rva)
                if not name and self.starts:
                    i = bisect.bisect_right(self.starts, rva) - 1
                    if i >= 0 and rva < self.ends[i]:
                        name = self.labels.get(self.starts[self.owners[i]])
                got = (name, True) if name else (f"sub_{rva:x}", False)
            else:
                got = (f"{m[2]}+0x{fn - m[0]:x}", False)
        self.cache[fn] = got
        return got


def analyze(rep: Report, namer: Namer, thread: int, buckets: list[tuple[str, re.Pattern]]) -> dict:
    sub: dict[int, int] = defaultdict(int)
    for idx in sorted(rep.nodes, reverse=True):  # a child always has a higher index than its parent
        parent, _, count = rep.nodes[idx]
        sub[idx] += count
        sub[parent] += sub[idx]
    children: dict[int, list[int]] = defaultdict(list)
    for idx, (parent, _, _) in rep.nodes.items():
        children[parent].append(idx)
    root = next((n for n, t in rep.roots.items() if t == thread), None)
    out = {"self": Counter(), "self_named": Counter(), "incl": Counter(), "bucket": Counter(), "top_module": Counter(), "seen": 0}
    if root is None:
        return out
    bcache: dict[int, tuple[str, ...]] = {}

    def bset(fn: int) -> tuple[str, ...]:
        got = bcache.get(fn)
        if got is None:
            name, real = namer.label(fn)
            got = tuple(b for b, rx in buckets if real and rx.search(name))
            bcache[fn] = got
        return got

    on_stack: Counter = Counter()
    active: Counter = Counter()
    named = [""]
    stack = [(c, False) for c in children.get(root, [])]
    while stack:
        node, leaving = stack.pop()
        _, fn, count = rep.nodes[node]
        if leaving:
            on_stack[fn] -= 1
            for b in bset(fn):
                active[b] -= 1
            named.pop()
            continue
        total = sub[node]
        if on_stack[fn] == 0:
            out["incl"][fn] += total
        on_stack[fn] += 1
        for b in bset(fn):
            if active[b] == 0:
                out["bucket"][b] += total
            active[b] += 1
        name, real = namer.label(fn)
        named.append(name if real else named[-1])
        if count:
            out["self"][fn] += count
            out["self_named"][named[-1] or "<no named function above>"] += count
            m = namer.module_of(fn)
            out["top_module"][m[2] if m else "<unknown>"] += count
            out["seen"] += count
        stack.append((node, True))
        stack.extend((c, False) for c in children.get(node, []))
    return out


def tree(rep: Report, namer: Namer, thread: int, start: str | None, depth: int, min_pct: float, unnamed: bool) -> str:
    """The thread's call tree from the top (or from every function matching `start`), as the share of the thread's samples under each call. Code with
    no name is passed through (its children are shown in its place) unless `unnamed` is set."""
    sub: dict[int, int] = defaultdict(int)
    for idx in sorted(rep.nodes, reverse=True):
        sub[idx] += rep.nodes[idx][2]
        sub[rep.nodes[idx][0]] += sub[idx]
    children: dict[int, list[int]] = defaultdict(list)
    for idx, (parent, _, _) in rep.nodes.items():
        children[parent].append(idx)
    root = next((n for n, t in rep.roots.items() if t == thread), None)
    total = rep.threads[thread]["samples"] or 1
    rx = re.compile(start) if start else None
    out: list[str] = []

    def show(node: int, level: int, inside: bool) -> None:
        name, real = namer.label(rep.nodes[node][1])
        share = 100 * sub[node] / total
        if share < min_pct:
            return
        here = inside or rx is None or bool(real and rx.search(name))
        printed = here and (real or unnamed)
        if printed:
            out.append(f"{'  ' * level}{share:5.1f}%  {short(name, 110)}")
        nxt = level + 1 if printed else level
        if here and printed and level >= depth:
            return
        for c in sorted(children.get(node, []), key=lambda c: -sub[c]):
            show(c, nxt, here)

    for c in sorted(children.get(root, []), key=lambda c: -sub[c]):
        show(c, 0, rx is None)
    return "\n".join(out) + "\n"


def short(name: str, width: int = 100) -> str:
    return name if len(name) <= width else name[: width - 3] + "..."


def render(rep: Report, namer: Namer, threads: int, top: int, buckets: list[tuple[str, re.Pattern]]) -> str:
    h = rep.header
    o: list[str] = []
    wall_ms = int(h.get("duration_ms", "0")) or 1
    o.append(f"Orion sampling profile: game {h.get('game')} build {h.get('build')}, {wall_ms / 1000:.1f} s, every {h.get('interval_ms')} ms, "
             f"{h.get('cycles')} cycles of {h.get('avg_cycle_us')} us (pausing all threads once)")
    o.append(f"call paths dropped: {h.get('dropped')}, stacks cut at the depth limit: {h.get('truncated')}")
    o.append("")
    o.append("threads (cpu is the share of wall time the thread was running; in-exe is the share of samples whose innermost frame was in the game's own code):")
    for i, t in sorted(rep.threads.items(), key=lambda kv: -kv[1]["in_exe"]):
        if t["samples"] == 0:
            continue
        o.append(f"  #{i:<3} tid {t['tid']:<6} {t['name'][:24]:<24} samples {t['samples']:>7}  cpu {100 * t['cpu_ms'] / wall_ms:5.1f}%  in-exe {100 * t['in_exe'] / t['samples']:5.1f}%")
    busy = [i for i, t in sorted(rep.threads.items(), key=lambda kv: -kv[1]["in_exe"]) if t["in_exe"] > 0][:threads]
    for i in busy:
        t = rep.threads[i]
        a = analyze(rep, namer, i, buckets)
        n = t["samples"]

        def pct(x: int) -> str:
            return f"{100 * x / n:5.1f}%"

        o.append("")
        o.append("=" * 100)
        o.append(f"thread #{i} (tid {t['tid']}, {t['name']}): {n} samples, {100 * t['in_exe'] / n:.1f}% in the game's code")
        o.append("-" * 100)
        o.append("where the innermost frame was: " + ", ".join(f"{m} {pct(c)}" for m, c in a["top_module"].most_common(6)))
        o.append("")
        o.append("subsystems (inclusive, heuristic name patterns; a sample counts for every one on its stack):")
        for b, c in a["bucket"].most_common():
            o.append(f"  {pct(c)}  {b}")
        if not a["bucket"]:
            o.append("  (none matched: run with --names to get function names)")
        o.append("")
        o.append(f"busiest functions by their own time (top {top}):")
        for fn, c in a["self"].most_common(top):
            o.append(f"  {pct(c)}  {short(namer.label(fn)[0])}")
        o.append("")
        o.append(f"own time charged to the nearest named function above it (top {top}):")
        for name, c in a["self_named"].most_common(top):
            o.append(f"  {pct(c)}  {short(name)}")
        o.append("")
        o.append(f"named functions by time spent in them and everything they call (top {top}):")
        shown = 0
        for fn, c in a["incl"].most_common():
            name, real = namer.label(fn)
            if not real:
                continue
            o.append(f"  {pct(c)}  {short(name)}")
            shown += 1
            if shown >= top:
                break
    return "\n".join(o) + "\n"


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("report", type=Path)
    ap.add_argument("--names", type=Path, help="Exception Translator's web/public/data/<version>/steam-windows.json for this game version")
    ap.add_argument("--threads", type=int, default=3, help="how many of the busiest threads to break down (default 3)")
    ap.add_argument("--top", type=int, default=25, help="rows per table (default 25)")
    ap.add_argument("--bucket", action="append", default=[], metavar="NAME=REGEX", help="add a subsystem pattern; matched against function names")
    ap.add_argument("--out", type=Path, help="also write the text here")
    ap.add_argument("--tree", type=int, metavar="THREAD", help="print the call tree of this thread (its #number in the thread list) instead of the tables")
    ap.add_argument("--from", dest="start", metavar="REGEX", help="with --tree: start at functions whose name matches")
    ap.add_argument("--depth", type=int, default=8, help="with --tree: levels of named functions to show (default 8)")
    ap.add_argument("--min", type=float, default=1.0, dest="min_pct", help="with --tree: hide calls under this share of the thread's samples (default 1.0)")
    ap.add_argument("--unnamed", action="store_true", help="with --tree: show code that has no name too")
    args = ap.parse_args()
    rep = parse(args.report.read_text(encoding="utf-8", errors="replace"))
    buckets = [(n, re.compile(rx)) for n, rx in DEFAULT_BUCKETS]
    for spec in args.bucket:
        name, _, rx = spec.partition("=")
        buckets.append((name, re.compile(rx)))
    if args.tree is not None:
        text = tree(rep, Namer(rep, args.names), args.tree, args.start, args.depth, args.min_pct, args.unnamed)
        sys.stdout.write(text)
        if args.out:
            args.out.write_text(text, encoding="utf-8")
        return 0
    text = render(rep, Namer(rep, args.names), args.threads, args.top, buckets)
    sys.stdout.write(text)
    if args.out:
        args.out.write_text(text, encoding="utf-8")
    return 0


if __name__ == "__main__":
    sys.exit(main())
