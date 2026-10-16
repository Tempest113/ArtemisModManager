// Spike for the honest-mode question (D22, D25 step 5): can Orion put one more file into the game's checksummed file set, so that a plugin
// changes the multiplayer checksum and turns achievements off the way a script mod does?
//
// The game exports its whole file system (PhysFS, under C++ names), including PHYSFS_mountMemory. This plugin builds a tiny zip in memory that
// holds common/modloader/manifest.txt and mounts it. Nothing here hooks engine code; the only hook is a read-only watch on four kernel32 file
// calls (the import-table patch the first proof of concept used) to put a timeline in the log.
//
// Settings (set in Artemis, so no rebuild between runs):
//   mode        "observe" only watches and logs; "mount" also mounts the fingerprint archive.
//   mountAfter  mount once the search path has an entry containing this text (the mod's own folder, so every mod is mounted first). Empty means
//               as soon as PhysFS is initialised.
// Everything it learns goes to its own log (Documents\Paradox Interactive\Stellaris\modloader\plugins\<mod>.log).
#include <windows.h>

#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <wchar.h>
#include <string.h>

#include <set>
#include <string>
#include <vector>

#include "orion_plugin.h"

namespace {

const OrionHost* g_host = nullptr;
LARGE_INTEGER g_t0, g_freq;
std::string g_mode = "observe", g_after;
volatile LONG g_calls[4] = {};
volatile LONG g_mounted = 0;
std::string g_zip;  // PhysFS keeps pointing into this, so it lives for the whole process

void Say(int level, const char* fmt, ...) {
  LARGE_INTEGER now;
  QueryPerformanceCounter(&now);
  char buf[700];
  int n = snprintf(buf, sizeof(buf), "[+%8.1f ms] ", (now.QuadPart - g_t0.QuadPart) * 1000.0 / g_freq.QuadPart);
  va_list ap;
  va_start(ap, fmt);
  vsnprintf(buf + n, sizeof(buf) - n, fmt, ap);
  va_end(ap);
  g_host->log(g_host, level, buf);
}

std::string Utf8(const wchar_t* w, int max = 220) {
  char out[700];
  const int n = WideCharToMultiByte(CP_UTF8, 0, w, -1, out, sizeof(out) - 1, nullptr, nullptr);
  std::string s = n > 0 ? std::string(out, n - 1) : std::string();
  if (static_cast<int>(s.size()) > max) s = s.substr(0, max) + "...";
  return s;
}

std::string Lower(std::string s) {
  for (char& c : s) c = static_cast<char>(tolower(static_cast<unsigned char>(c)));
  return s;
}

// ---- the engine's PhysFS, by its exported (C++) names ----

using IsInitFn = int (*)();
using GetSearchPathFn = char** (*)();
using FreeListFn = void (*)(void*);
using MountMemoryFn = int (*)(const void*, unsigned long long, void (*)(void*), const char*, const char*, int);
using ExistsFn = int (*)(const char*);
using GetRealDirFn = const char* (*)(const char*);
using EnumerateFilesFn = char** (*)(const char*);
using LastErrorFn = const char* (*)();

struct PhysFs {
  IsInitFn isInit;
  GetSearchPathFn getSearchPath;
  FreeListFn freeList;
  MountMemoryFn mountMemory;
  ExistsFn exists;
  GetRealDirFn getRealDir;
  EnumerateFilesFn enumerateFiles;
  LastErrorFn lastError;
} g_fs = {};

template <typename T>
bool Bind(HMODULE exe, T& out, const char* mangled) {
  out = reinterpret_cast<T>(GetProcAddress(exe, mangled));
  if (!out) Say(ORION_LOG_WARN, "the game doesn't export %s", mangled);
  return out != nullptr;
}

bool BindPhysFs(HMODULE exe) {
  bool ok = true;
  ok &= Bind(exe, g_fs.isInit, "?PHYSFS_isInit@@YAHXZ");
  ok &= Bind(exe, g_fs.getSearchPath, "?PHYSFS_getSearchPath@@YAPEAPEADXZ");
  ok &= Bind(exe, g_fs.freeList, "?PHYSFS_freeList@@YAXPEAX@Z");
  ok &= Bind(exe, g_fs.mountMemory, "?PHYSFS_mountMemory@@YAHPEBX_KP6AXPEAX@ZPEBD4H@Z");
  ok &= Bind(exe, g_fs.exists, "?PHYSFS_exists@@YAHPEBD@Z");
  ok &= Bind(exe, g_fs.getRealDir, "?PHYSFS_getRealDir@@YAPEBDPEBD@Z");
  ok &= Bind(exe, g_fs.enumerateFiles, "?PHYSFS_enumerateFiles@@YAPEAPEADPEBD@Z");
  ok &= Bind(exe, g_fs.lastError, "?PHYSFS_getLastError@@YAPEBDXZ");
  return ok;
}

// ---- a zip in memory: stored entries only, which is all PhysFS's zip reader needs ----

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

struct ZipEntry {
  std::string name, data;
  bool dir;
};

std::string MakeZip(const std::vector<ZipEntry>& entries) {
  std::string out, central;
  for (const ZipEntry& e : entries) {
    const uint32_t offset = static_cast<uint32_t>(out.size()), crc = e.dir ? 0 : Crc32(e.data), size = e.dir ? 0 : static_cast<uint32_t>(e.data.size());
    Put32(out, 0x04034b50); Put16(out, 20); Put16(out, 0); Put16(out, 0); Put16(out, 0); Put16(out, 0x21);
    Put32(out, crc); Put32(out, size); Put32(out, size); Put16(out, static_cast<uint16_t>(e.name.size())); Put16(out, 0);
    out += e.name;
    if (!e.dir) out += e.data;
    Put32(central, 0x02014b50); Put16(central, 20); Put16(central, 20); Put16(central, 0); Put16(central, 0); Put16(central, 0); Put16(central, 0x21);
    Put32(central, crc); Put32(central, size); Put32(central, size); Put16(central, static_cast<uint16_t>(e.name.size())); Put16(central, 0); Put16(central, 0);
    Put16(central, 0); Put16(central, 0); Put32(central, e.dir ? 0x10 : 0); Put32(central, offset);
    central += e.name;
  }
  const uint32_t centralAt = static_cast<uint32_t>(out.size());
  out += central;
  Put32(out, 0x06054b50); Put16(out, 0); Put16(out, 0); Put16(out, static_cast<uint16_t>(entries.size())); Put16(out, static_cast<uint16_t>(entries.size()));
  Put32(out, static_cast<uint32_t>(central.size())); Put32(out, centralAt); Put16(out, 0);
  return out;
}

const char kArchiveName[] = "orion-fingerprint.zip";
const char kManifestPath[] = "common/modloader/manifest.txt";

std::string Fingerprint() {
  return "# Orion honest-mode fingerprint (spike): the plugins loaded in this session.\n"
         "orion_plugin = { id = \"artemis.honest-spike\" version = \"0.1.0\" }\n";
}

// ---- a read-only watch on the game's file calls, to see when things happen ----

enum Api { kCreateFile, kFindFirstEx, kFindFirst, kGetAttributes, kApiCount };
const char* const kApiNames[kApiCount] = {"CreateFileW", "FindFirstFileExW", "FindFirstFileW", "GetFileAttributesExW"};

decltype(&CreateFileW) o_CreateFileW;
decltype(&FindFirstFileExW) o_FindFirstFileExW;
decltype(&FindFirstFileW) o_FindFirstFileW;
decltype(&GetFileAttributesExW) o_GetFileAttributesExW;

struct Category {
  const wchar_t* needle;
  volatile LONG seen[kApiCount];
};
Category g_categories[] = {{L"dlc_load.json"}, {L"\\mod\\"}, {L"workshop"}, {L"\\common\\"}, {L"\\events\\"}, {L"\\map\\"},
                           {L"\\localisation\\"}, {L".mod"}, {L"checksum"}, {L"\\gfx\\"}, {L"\\interface\\"}, {L"save games"}};

// Logs the first time each kind of path is touched through each call, with how many calls came before it.
void Note(Api api, const wchar_t* path) {
  const long n = InterlockedIncrement(&g_calls[api]);
  if (!path) return;
  wchar_t low[300];
  int len = 0;
  for (; path[len] && len < 299; ++len) low[len] = path[len] == L'/' ? L'\\' : static_cast<wchar_t>(towlower(path[len]));
  low[len] = 0;
  for (Category& c : g_categories) {
    if (c.seen[api] || !wcsstr(low, c.needle)) continue;
    if (InterlockedCompareExchange(&c.seen[api], 1, 0) == 0)
      Say(ORION_LOG_INFO, "first %s touching \"%ls\" (call #%ld): %s", kApiNames[api], c.needle, n, Utf8(path).c_str());
  }
}

HANDLE WINAPI Hk_CreateFileW(LPCWSTR p, DWORD a, DWORD s, LPSECURITY_ATTRIBUTES sa, DWORD d, DWORD f, HANDLE t) {
  Note(kCreateFile, p);
  return o_CreateFileW(p, a, s, sa, d, f, t);
}
HANDLE WINAPI Hk_FindFirstFileExW(LPCWSTR p, FINDEX_INFO_LEVELS l, LPVOID data, FINDEX_SEARCH_OPS op, LPVOID filter, DWORD flags) {
  Note(kFindFirstEx, p);
  return o_FindFirstFileExW(p, l, data, op, filter, flags);
}
HANDLE WINAPI Hk_FindFirstFileW(LPCWSTR p, LPWIN32_FIND_DATAW data) {
  Note(kFindFirst, p);
  return o_FindFirstFileW(p, data);
}
BOOL WINAPI Hk_GetFileAttributesExW(LPCWSTR p, GET_FILEEX_INFO_LEVELS l, LPVOID info) {
  Note(kGetAttributes, p);
  return o_GetFileAttributesExW(p, l, info);
}

// Points one kernel32 import of the exe at `detour`. The original comes from kernel32 itself, so the slot need not be bound yet.
bool PatchImport(HMODULE exe, const char* name, void* detour, void** original) {
  auto* base = reinterpret_cast<BYTE*>(exe);
  auto* nt = reinterpret_cast<IMAGE_NT_HEADERS*>(base + reinterpret_cast<IMAGE_DOS_HEADER*>(base)->e_lfanew);
  const auto& dir = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
  if (!dir.VirtualAddress) return false;
  *original = reinterpret_cast<void*>(GetProcAddress(GetModuleHandleW(L"kernel32.dll"), name));
  if (!*original) return false;
  for (auto* imp = reinterpret_cast<IMAGE_IMPORT_DESCRIPTOR*>(base + dir.VirtualAddress); imp->Name; ++imp) {
    if (_stricmp(reinterpret_cast<char*>(base + imp->Name), "kernel32.dll") != 0 || !imp->OriginalFirstThunk) continue;
    auto* names = reinterpret_cast<IMAGE_THUNK_DATA*>(base + imp->OriginalFirstThunk);
    auto* slots = reinterpret_cast<IMAGE_THUNK_DATA*>(base + imp->FirstThunk);
    for (; names->u1.AddressOfData; ++names, ++slots) {
      if (IMAGE_SNAP_BY_ORDINAL(names->u1.Ordinal)) continue;
      if (strcmp(reinterpret_cast<IMAGE_IMPORT_BY_NAME*>(base + names->u1.AddressOfData)->Name, name) != 0) continue;
      DWORD old;
      if (!VirtualProtect(&slots->u1.Function, sizeof(ULONG_PTR), PAGE_READWRITE, &old)) return false;
      slots->u1.Function = reinterpret_cast<ULONG_PTR>(detour);
      VirtualProtect(&slots->u1.Function, sizeof(ULONG_PTR), old, &old);
      return true;
    }
  }
  return false;
}

// ---- the poller: watches PhysFS from its own thread, so nothing here runs inside the game's file calls ----

std::set<std::string> SearchPath() {
  std::set<std::string> now;
  if (char** list = g_fs.getSearchPath()) {
    for (char** p = list; *p; ++p) now.insert(*p);
    g_fs.freeList(list);
  }
  return now;
}

bool Has(const std::set<std::string>& paths, const std::string& needle) {
  const std::string want = Lower(needle);
  for (const std::string& p : paths)
    if (Lower(p).find(want) != std::string::npos) return true;
  return false;
}

void Mount() {
  g_zip = MakeZip({{"common/", "", true}, {"common/modloader/", "", true}, {kManifestPath, Fingerprint(), false}});
  const int ok = g_fs.mountMemory(g_zip.data(), g_zip.size(), nullptr, kArchiveName, "/", 1);
  Say(ORION_LOG_INFO, "PHYSFS_mountMemory(%s, %zu bytes, mount point \"/\", append) returned %d%s%s", kArchiveName, g_zip.size(), ok,
      ok ? "" : ": ", ok ? "" : g_fs.lastError());
  Say(ORION_LOG_INFO, "after mounting: exists(%s) = %d, real dir = %s", kManifestPath, g_fs.exists(kManifestPath), g_fs.getRealDir(kManifestPath) ? g_fs.getRealDir(kManifestPath) : "(none)");
  if (char** files = g_fs.enumerateFiles("common/modloader")) {
    for (char** p = files; *p; ++p) Say(ORION_LOG_INFO, "enumerate(common/modloader): %s", *p);
    g_fs.freeList(files);
  }
  InterlockedExchange(&g_mounted, 1);
}

DWORD WINAPI Poller(LPVOID) {
  const bool wantMount = g_mode == "mount";
  bool sawInit = false, mountedSeen = false;
  std::set<std::string> prev;
  const double summaries[] = {10, 30, 60, 120};
  size_t nextSummary = 0;
  for (;;) {
    LARGE_INTEGER now;
    QueryPerformanceCounter(&now);
    const double ms = (now.QuadPart - g_t0.QuadPart) * 1000.0 / g_freq.QuadPart;
    if (ms > 125000) break;
    if (!sawInit && g_fs.isInit()) {
      sawInit = true;
      Say(ORION_LOG_INFO, "PhysFS is initialised (file calls so far: CreateFileW %ld, FindFirstFileExW %ld, FindFirstFileW %ld, GetFileAttributesExW %ld)", g_calls[0],
          g_calls[1], g_calls[2], g_calls[3]);
    }
    if (sawInit) {
      const std::set<std::string> cur = SearchPath();
      if (cur != prev) {
        for (const std::string& p : cur)
          if (!prev.count(p)) Say(ORION_LOG_INFO, "search path +%s (now %zu entries; CreateFileW calls so far %ld)", p.c_str(), cur.size(), g_calls[0]);
        for (const std::string& p : prev)
          if (!cur.count(p)) Say(ORION_LOG_INFO, "search path -%s (now %zu entries)", p.c_str(), cur.size());
        prev = cur;
      }
      if (wantMount && !g_mounted && (g_after.empty() || Has(cur, g_after))) {
        Say(ORION_LOG_INFO, "mounting the fingerprint now (trigger: %s)", g_after.empty() ? "PhysFS initialised" : ("search path has \"" + g_after + "\"").c_str());
        Mount();
      }
      if (g_mounted) {
        const bool here = Has(cur, kArchiveName);
        if (here != mountedSeen) {
          Say(here ? ORION_LOG_INFO : ORION_LOG_WARN, "the fingerprint archive %s the search path", here ? "is in" : "has LEFT");
          mountedSeen = here;
        }
      }
    }
    if (nextSummary < 4 && ms >= summaries[nextSummary] * 1000) {
      Say(ORION_LOG_INFO, "summary at %.0f s: PhysFS init %s; fingerprint %s; exists(%s) = %d; CreateFileW %ld, FindFirstFileExW %ld, FindFirstFileW %ld, GetFileAttributesExW %ld",
          summaries[nextSummary], sawInit ? "yes" : "NO", g_mounted ? (mountedSeen ? "mounted and present" : "mounted, gone") : "not mounted", kManifestPath,
          sawInit ? g_fs.exists(kManifestPath) : -1, g_calls[0], g_calls[1], g_calls[2], g_calls[3]);
      ++nextSummary;
    }
    Sleep(1);
  }
  Say(ORION_LOG_INFO, "poller stops");
  return 0;
}

}  // namespace

extern "C" ORION_PLUGIN_EXPORT int OrionPluginInit(const OrionHost* host) {
  g_host = host;
  QueryPerformanceFrequency(&g_freq);
  QueryPerformanceCounter(&g_t0);
  if (char* mode = host->getString(host, "mode")) { g_mode = mode; host->free(mode); }
  if (char* after = host->getString(host, "mountAfter")) { g_after = after; host->free(after); }
  Say(ORION_LOG_INFO, "honest-mode spike: Orion %s, game %s, build %s; mode=%s, mountAfter=\"%s\"", host->orionVersion, host->gameVersion, host->gameBuild, g_mode.c_str(),
      g_after.c_str());

  const HMODULE exe = reinterpret_cast<HMODULE>(host->gameBase);
  if (!BindPhysFs(exe)) {
    Say(ORION_LOG_ERROR, "PhysFS isn't reachable by its exported names, so the spike can't do anything");
    return 2;
  }
  void* detours[] = {reinterpret_cast<void*>(&Hk_CreateFileW), reinterpret_cast<void*>(&Hk_FindFirstFileExW), reinterpret_cast<void*>(&Hk_FindFirstFileW),
                     reinterpret_cast<void*>(&Hk_GetFileAttributesExW)};
  void** originals[] = {reinterpret_cast<void**>(&o_CreateFileW), reinterpret_cast<void**>(&o_FindFirstFileExW), reinterpret_cast<void**>(&o_FindFirstFileW),
                        reinterpret_cast<void**>(&o_GetFileAttributesExW)};
  for (int i = 0; i < kApiCount; ++i)
    Say(ORION_LOG_INFO, "watching %s: %s", kApiNames[i], PatchImport(exe, kApiNames[i], detours[i], originals[i]) ? "yes" : "NOT possible");
  if (HANDLE t = CreateThread(nullptr, 0, Poller, nullptr, 0, nullptr)) CloseHandle(t);
  return 0;
}
