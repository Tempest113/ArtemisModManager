#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include "honest.h"

#include <algorithm>
#include <cstdlib>
#include <set>

#include "json.h"

namespace orion {
namespace {

// ---- the game's PhysFS, by its exported C++ names (found in the exe's export table; see D52) ----

using IsInitFn = int (*)();
using GetSearchPathFn = char** (*)();
using FreeListFn = void (*)(void*);
using MountMemoryFn = int (*)(const void*, unsigned long long, void (*)(void*), const char*, const char*, int);
using ExistsFn = int (*)(const char*);
using LastErrorFn = const char* (*)();

struct Fs {
  IsInitFn isInit = nullptr;
  GetSearchPathFn getSearchPath = nullptr;
  FreeListFn freeList = nullptr;
  MountMemoryFn mountMemory = nullptr;
  ExistsFn exists = nullptr;
  LastErrorFn lastError = nullptr;
};

Fs g_fs;
bool g_bound = false;
std::string g_whyNot;

template <typename T>
void Bind(HMODULE exe, T& out, const char* mangled) {
  out = reinterpret_cast<T>(GetProcAddress(exe, mangled));
  if (!out) g_whyNot += std::string(g_whyNot.empty() ? "" : ", ") + mangled;
}

const char kArchiveName[] = "orion-fingerprint.zip";
const char kManifestPath[] = "common/modloader/manifest.txt";

// ---- a zip in memory ----

uint32_t Crc32(const std::string& data) {
  static uint32_t table[256];
  if (!table[1]) {
    for (uint32_t i = 0; i < 256; ++i) {
      uint32_t c = i;
      for (int k = 0; k < 8; ++k) c = (c & 1) ? 0xEDB88320u ^ (c >> 1) : c >> 1;
      table[i] = c;
    }
  }
  uint32_t crc = 0xFFFFFFFFu;
  for (const unsigned char b : data) crc = table[(crc ^ b) & 0xFF] ^ (crc >> 8);
  return ~crc;
}

void Put16(std::string& s, uint16_t v) { s.append(reinterpret_cast<const char*>(&v), 2); }
void Put32(std::string& s, uint32_t v) { s.append(reinterpret_cast<const char*>(&v), 4); }

// ---- the watcher ----

struct Job {
  Log* log;
  std::string zip, waitFor;
  HonestReport report;
  int waitMs;
  volatile bool mountedReported = false;
};

double NowMs() {
  static LARGE_INTEGER freq = [] { LARGE_INTEGER f; QueryPerformanceFrequency(&f); return f; }();
  LARGE_INTEGER t;
  QueryPerformanceCounter(&t);
  return t.QuadPart * 1000.0 / freq.QuadPart;
}

std::set<std::string> SearchPath() {
  std::set<std::string> now;
  if (char** list = g_fs.getSearchPath()) {
    for (char** p = list; *p; ++p) now.insert(*p);
    g_fs.freeList(list);
  }
  return now;
}

bool HasNormalised(const std::set<std::string>& paths, const std::string& want) {
  for (const std::string& p : paths)
    if (NormalisePath(p) == want) return true;
  return false;
}

// Mounts the archive and checks that the game can see the file. Returns "" or what went wrong.
std::string Mount(Job& job) {
  const int ok = g_fs.mountMemory(job.zip.data(), job.zip.size(), nullptr, kArchiveName, "/", 1);
  if (!ok) return std::string("the game refused the archive: ") + (g_fs.lastError() ? g_fs.lastError() : "no reason given");
  if (!g_fs.exists(kManifestPath)) return "the game doesn't see common/modloader/manifest.txt after mounting";
  return "";
}

void Watch(Job& job) {
  Log& log = *job.log;
  const double start = NowMs();
  while (!g_fs.isInit()) {
    if (NowMs() - start > 90000) { job.report("failed", "The game's file system never started, so the plugins could not be added to its checksum."); return; }
    Sleep(1);
  }
  const double init = NowMs();
  log.Write("info", "Honest mode: the game's file system is up " + std::to_string(static_cast<long>(init - start)) + " ms after Orion started.");

  std::set<std::string> prev;
  double changed = init;
  bool targetSeen = false, mounted = false;
  int remounts = 0;
  while (NowMs() - init < 30000) {  // the game reads its checksum list about 19 s after launch (D52); watch past that
    const std::set<std::string> cur = SearchPath();
    const double now = NowMs();
    if (cur != prev) { prev = cur; changed = now; }
    if (!mounted) {
      targetSeen = targetSeen || (!job.waitFor.empty() && HasNormalised(cur, job.waitFor));
      const bool settled = targetSeen && now - changed >= 40;
      const bool giveUp = now - init >= job.waitMs;
      if (settled || giveUp) {
        const std::string why = Mount(job);
        if (!why.empty()) { job.report("failed", "Orion couldn't add the plugins to the game's checksum: " + why + "."); return; }
        mounted = true;
        log.Write("info", std::string("Honest mode: mounted the fingerprint ") + (settled ? "after the game mounted its mods" : "without seeing the last mod mounted (timed out)") + ", " +
                              std::to_string(static_cast<long>(now - init)) + " ms after the file system started.");
        job.mountedReported = true;
        job.report("mounted", "");
      }
    } else if (!HasNormalised(cur, kArchiveName)) {
      if (++remounts > 3) { job.report("failed", "The game keeps removing the plugins' fingerprint from its file list."); return; }
      log.Write("warn", "Honest mode: the game dropped the fingerprint from its file list; mounting it again (" + std::to_string(remounts) + ").");
      const std::string why = Mount(job);
      if (!why.empty()) { job.report("failed", "Orion couldn't put the plugins' fingerprint back: " + why + "."); return; }
    }
    Sleep(mounted ? 20 : 1);
  }
  if (!mounted) job.report("failed", "The fingerprint was never mounted.");
}

// A crash in here must not take the game down with it; the plugins just stay out of the checksum and the status says so.
bool WatchGuarded(Job* job) {
  __try {
    Watch(*job);
    return true;
  } __except (EXCEPTION_EXECUTE_HANDLER) {
    return false;
  }
}

DWORD WINAPI WatchThread(LPVOID p) {
  Job* job = static_cast<Job*>(p);
  // If the game is shutting down while this still watches, its file system can already be gone; that is no reason to call a mounted fingerprint failed.
  if (!WatchGuarded(job) && !job->mountedReported) job->report("failed", "Orion's watcher stopped on an internal error.");
  return 0;
}

}  // namespace

bool HonestAvailable(std::string& why) {
  if (!g_bound) {
    g_bound = true;
    const HMODULE exe = GetModuleHandleW(nullptr);
    Bind(exe, g_fs.isInit, "?PHYSFS_isInit@@YAHXZ");
    Bind(exe, g_fs.getSearchPath, "?PHYSFS_getSearchPath@@YAPEAPEADXZ");
    Bind(exe, g_fs.freeList, "?PHYSFS_freeList@@YAXPEAX@Z");
    Bind(exe, g_fs.mountMemory, "?PHYSFS_mountMemory@@YAHPEBX_KP6AXPEAX@ZPEBD4H@Z");
    Bind(exe, g_fs.exists, "?PHYSFS_exists@@YAHPEBD@Z");
    Bind(exe, g_fs.lastError, "?PHYSFS_getLastError@@YAPEBDXZ");
  }
  why = g_whyNot.empty() ? "" : "the game doesn't export " + g_whyNot;
  return g_whyNot.empty();
}

std::string BuildFingerprint(std::vector<HonestPlugin> plugins) {
  std::sort(plugins.begin(), plugins.end(), [](const HonestPlugin& a, const HonestPlugin& b) { return a.id < b.id; });
  std::string text = "# Orion honest-mode fingerprint: the plugins that change how the game plays. Written by Orion, not part of any mod.\n";
  for (const HonestPlugin& p : plugins) {
    text += "plugin = { id = " + JsonQuote(p.id) + " version = " + JsonQuote(p.version);
    for (const HonestFile& f : p.files) text += " file = { path = " + JsonQuote(f.path) + " sha256 = \"" + f.sha256 + "\" }";
    text += " }\n";
  }
  return text;
}

std::string MakeZip(const std::vector<ZipEntry>& entries) {
  std::string out, central;
  for (const ZipEntry& e : entries) {
    const bool dir = !e.name.empty() && e.name.back() == '/';
    const uint32_t offset = static_cast<uint32_t>(out.size()), crc = dir ? 0 : Crc32(e.data), size = dir ? 0 : static_cast<uint32_t>(e.data.size());
    Put32(out, 0x04034b50); Put16(out, 20); Put16(out, 0); Put16(out, 0); Put16(out, 0); Put16(out, 0x21);
    Put32(out, crc); Put32(out, size); Put32(out, size); Put16(out, static_cast<uint16_t>(e.name.size())); Put16(out, 0);
    out += e.name;
    if (!dir) out += e.data;
    Put32(central, 0x02014b50); Put16(central, 20); Put16(central, 20); Put16(central, 0); Put16(central, 0); Put16(central, 0); Put16(central, 0x21);
    Put32(central, crc); Put32(central, size); Put32(central, size); Put16(central, static_cast<uint16_t>(e.name.size())); Put16(central, 0); Put16(central, 0);
    Put16(central, 0); Put16(central, 0); Put32(central, dir ? 0x10 : 0); Put32(central, offset);
    central += e.name;
  }
  const uint32_t centralAt = static_cast<uint32_t>(out.size());
  out += central;
  Put32(out, 0x06054b50); Put16(out, 0); Put16(out, 0); Put16(out, static_cast<uint16_t>(entries.size())); Put16(out, static_cast<uint16_t>(entries.size()));
  Put32(out, static_cast<uint32_t>(central.size())); Put32(out, centralAt); Put16(out, 0);
  return out;
}

std::string NormalisePath(std::string path) {
  for (char& c : path) c = c == '\\' ? '/' : static_cast<char>(tolower(static_cast<unsigned char>(c)));
  while (path.size() > 1 && path.back() == '/') path.pop_back();
  return path;
}

void StartHonest(Log& log, std::string fingerprint, std::string waitFor, HonestReport report) {
  std::string why;
  if (!HonestAvailable(why)) { report("failed", "Orion couldn't add the plugins to the game's checksum: " + why + "."); return; }
  auto* job = new Job{&log, MakeZip({{"common/", ""}, {"common/modloader/", ""}, {kManifestPath, std::move(fingerprint)}}), std::move(waitFor), report, 8000, false};
  if (const std::string ms = Narrow(Env(L"ORION_HONEST_WAIT_MS")); !ms.empty()) job->waitMs = std::max(0, atoi(ms.c_str()));  // for tests
  if (HANDLE t = CreateThread(nullptr, 0, WatchThread, job, 0, nullptr)) CloseHandle(t);
  else report("failed", "Orion couldn't start its watcher thread.");
}

}  // namespace orion
