// A sampling profiler, as a plugin. It changes nothing in the game and needs no build data: a thread of its own pauses each game thread for a few
// microseconds every few milliseconds, writes down which function it was in and who called it (walking the stack with the exe's own unwind tables),
// and lets it go. Counting those samples over a minute or two says where the game spends its time.
//
// Settings (modloader.json): intervalMs (1..50, default 2), hotkey (a virtual-key code pressed together with Ctrl+Shift; default
// 120 = F9; 0 = off), autoSeconds (sample from the moment the plugin loads, for this many seconds, to profile start-up; default 0 = off).
// Press the hotkey once to start and again to stop; the report goes to Documents\...\Stellaris\modloader\profiles\profile-<time>.txt and its path to
// the plugin's log. tools\profile_report.py (next to Orion's tools) turns the report into names and shares.
//
// While a game thread is paused the sampler touches only memory it set up beforehand: no heap, no locks, no logging, no loader calls. That is what
// keeps the pause from deadlocking against a lock the paused thread holds.
#define _CRT_SECURE_NO_WARNINGS
#include <windows.h>
#include <initguid.h>
#include <KnownFolders.h>
#include <psapi.h>
#include <shlobj.h>
#include <tlhelp32.h>

#include <stdarg.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "orion_plugin.h"

#pragma comment(lib, "user32.lib")
#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "ole32.lib")

#ifndef CREATE_WAITABLE_TIMER_HIGH_RESOLUTION
#define CREATE_WAITABLE_TIMER_HIGH_RESOLUTION 0x00000002
#endif

namespace {

constexpr int kMaxThreads = 128;
constexpr int kMaxModules = 256;
constexpr int kMaxDepth = 192;                // frames kept per sample (innermost first); deeper stacks are counted as truncated
constexpr uint32_t kMaxNodes = 1u << 20;      // distinct call paths
constexpr uint32_t kSlots = 1u << 21;         // hash slots for them (a power of two, twice the nodes)
constexpr uint32_t kNoParent = 0xFFFFFFFFu;
constexpr DWORD kMaxRunMs = 20 * 60 * 1000;   // a forgotten hotkey run stops by itself

struct Module {
  uintptr_t base;
  uint32_t size;
  const RUNTIME_FUNCTION* fns;
  uint32_t count;
  char name[48];
};

struct Thread {
  DWORD tid;
  HANDLE h;
  bool live;
  uintptr_t stackHi;
  uint32_t root;
  uint32_t samples;
  uint32_t inExe;
  uint64_t cpu0, cpu1;
  char name[48];
};

struct Node {
  uint64_t fn;  // start of the function (absolute address); 0 = code outside every module
  uint32_t parent;
  uint32_t count;  // samples that ended exactly here
};

struct UnwindInfo {
  uint8_t verFlags, prologSize, codes, frame;
};

const OrionHost* g_host;
int g_interval = 2;
int g_vk = 0x78;
int g_autoSeconds = 0;

Module g_mod[kMaxModules];
int g_nmod;
int g_exe = -1;
Thread g_thr[kMaxThreads];
int g_nthr;
Node g_node[kMaxNodes];
uint32_t g_slot[kSlots];
uint32_t g_nnodes;
uint32_t g_dropped, g_truncated, g_cycles;
uint64_t g_cycleUs;

typedef LONG(NTAPI* NtQueryInformationThreadFn)(HANDLE, int, PVOID, ULONG, PULONG);
typedef HRESULT(WINAPI* GetThreadDescriptionFn)(HANDLE, PWSTR*);
NtQueryInformationThreadFn g_ntQuery;
GetThreadDescriptionFn g_getDesc;

void Say(int level, const char* fmt, ...) {
  char line[512];
  va_list ap;
  va_start(ap, fmt);
  vsnprintf(line, sizeof(line), fmt, ap);
  va_end(ap);
  g_host->log(g_host, level, line);
}

uint64_t Now100ns() {
  FILETIME f;
  GetSystemTimeAsFileTime(&f);
  return (static_cast<uint64_t>(f.dwHighDateTime) << 32) | f.dwLowDateTime;
}

uint64_t CpuMs(HANDLE h) {
  FILETIME c, e, k, u;
  if (!GetThreadTimes(h, &c, &e, &k, &u)) return 0;
  const uint64_t kk = (static_cast<uint64_t>(k.dwHighDateTime) << 32) | k.dwLowDateTime;
  const uint64_t uu = (static_cast<uint64_t>(u.dwHighDateTime) << 32) | u.dwLowDateTime;
  return (kk + uu) / 10000;
}

// ---- modules and their unwind tables (read while no game thread is paused) ----

void ReadUnwindTable(Module& m) {
  __try {
    const IMAGE_DOS_HEADER* dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(m.base);
    if (dos->e_magic != IMAGE_DOS_SIGNATURE) return;
    const IMAGE_NT_HEADERS64* nt = reinterpret_cast<const IMAGE_NT_HEADERS64*>(m.base + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE) return;
    const IMAGE_DATA_DIRECTORY& d = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_EXCEPTION];
    if (d.VirtualAddress && d.Size) {
      m.fns = reinterpret_cast<const RUNTIME_FUNCTION*>(m.base + d.VirtualAddress);
      m.count = d.Size / sizeof(RUNTIME_FUNCTION);
    }
  } __except (EXCEPTION_EXECUTE_HANDLER) {
    m.fns = nullptr;
    m.count = 0;
  }
}

void RefreshModules() {
  HMODULE list[kMaxModules];
  DWORD need = 0;
  HANDLE self = GetCurrentProcess();
  if (!EnumProcessModules(self, list, sizeof(list), &need)) return;
  int n = static_cast<int>(need / sizeof(HMODULE));
  if (n > kMaxModules) n = kMaxModules;
  const uintptr_t exeBase = reinterpret_cast<uintptr_t>(GetModuleHandleW(nullptr));
  static Module next[kMaxModules];  // only the sampler thread uses these
  int count = 0, exe = -1;
  for (int i = 0; i < n; ++i) {
    MODULEINFO mi;
    if (!GetModuleInformation(self, list[i], &mi, sizeof(mi))) continue;
    Module& m = next[count];
    memset(&m, 0, sizeof(m));
    m.base = reinterpret_cast<uintptr_t>(mi.lpBaseOfDll);
    m.size = mi.SizeOfImage;
    GetModuleBaseNameA(self, list[i], m.name, sizeof(m.name));
    ReadUnwindTable(m);
    if (m.base == exeBase) exe = count;
    ++count;
  }
  memcpy(g_mod, next, sizeof(Module) * count);
  g_nmod = count;
  g_exe = exe;
}

const Module* ModuleOf(uint64_t addr) {
  if (g_exe >= 0 && addr - g_mod[g_exe].base < g_mod[g_exe].size) return &g_mod[g_exe];
  for (int i = 0; i < g_nmod; ++i)
    if (addr - g_mod[i].base < g_mod[i].size) return &g_mod[i];
  return nullptr;
}

const RUNTIME_FUNCTION* FindFunction(const Module& m, uint32_t rva) {
  uint32_t lo = 0, hi = m.count;
  while (lo < hi) {
    const uint32_t mid = (lo + hi) / 2;
    const RUNTIME_FUNCTION& f = m.fns[mid];
    if (rva < f.BeginAddress) hi = mid;
    else if (rva >= f.EndAddress) lo = mid + 1;
    else return &f;
  }
  return nullptr;
}

// A function split into pieces has several unwind entries chained to the first; the first one names the function.
uint32_t PrimaryBegin(const Module& m, const RUNTIME_FUNCTION* f) {
  for (int i = 0; i < 8; ++i) {
    const uint32_t u = f->UnwindData;
    if (u & RUNTIME_FUNCTION_INDIRECT) {
      f = reinterpret_cast<const RUNTIME_FUNCTION*>(m.base + (u & ~static_cast<uint32_t>(RUNTIME_FUNCTION_INDIRECT)));
      continue;
    }
    const UnwindInfo* ui = reinterpret_cast<const UnwindInfo*>(m.base + u);
    if (!((ui->verFlags >> 3) & UNW_FLAG_CHAININFO)) break;
    f = reinterpret_cast<const RUNTIME_FUNCTION*>(reinterpret_cast<const uint8_t*>(ui) + 4 + ((ui->codes + 1u) & ~1u) * 2);
  }
  return f->BeginAddress;
}

// Code with no unwind entry is a leaf function (one that never moves the stack pointer). Its start sits right after the previous function that has an
// entry, rounded up to the 16-byte alignment; far from any entry the address is rounded down instead. Two leaf functions in a row share a start.
uint32_t LeafStart(const Module& m, uint32_t rva) {
  uint32_t lo = 0, hi = m.count;  // the first entry that begins after rva
  while (lo < hi) {
    const uint32_t mid = (lo + hi) / 2;
    if (m.fns[mid].BeginAddress <= rva) lo = mid + 1;
    else hi = mid;
  }
  if (lo > 0) {
    const uint32_t prevEnd = m.fns[lo - 1].EndAddress;
    if (rva >= prevEnd && rva - prevEnd < 4096) return (prevEnd + 15) & ~15u;
  }
  return rva & ~15u;
}

// Walks the paused thread's stack, innermost frame first. Touches nothing but static memory and the thread's own stack and code.
int Walk(CONTEXT& c, uintptr_t stackHi, uint64_t* fns, bool* topInExe) {
  int n = 0;
  *topInExe = false;
  __try {
    uint64_t lastRsp = 0;
    for (int depth = 0; depth < kMaxDepth + 1; ++depth) {
      if (depth == kMaxDepth) {
        ++g_truncated;
        break;
      }
      if (c.Rsp & 7 || c.Rsp <= lastRsp) break;
      if (stackHi && c.Rsp >= stackHi) break;
      lastRsp = c.Rsp;
      const uint64_t rip = c.Rip;
      const Module* m = ModuleOf(rip);
      if (!m) {
        if (n == 0) fns[n++] = 0;
        break;
      }
      const uint32_t at = static_cast<uint32_t>(rip - m->base - (depth ? 1 : 0));  // a return address is looked up one byte back, inside the call
      const RUNTIME_FUNCTION* f = FindFunction(*m, at);
      if (n == 0) *topInExe = g_exe >= 0 && m == &g_mod[g_exe];
      if (!f) {  // no unwind data: a leaf function, whose return address is on top of the stack
        fns[n++] = m->base + LeafStart(*m, at);
        c.Rip = *reinterpret_cast<const uint64_t*>(c.Rsp);
        c.Rsp += 8;
        continue;
      }
      fns[n++] = m->base + PrimaryBegin(*m, f);
      PVOID handlerData = nullptr;
      ULONG64 frame = 0;
      RtlVirtualUnwind(UNW_FLAG_NHANDLER, m->base, rip, const_cast<PRUNTIME_FUNCTION>(f), &c, &handlerData, &frame, nullptr);
      if (c.Rip == 0) break;
    }
  } __except (EXCEPTION_EXECUTE_HANDLER) {
  }
  return n;
}

// ---- the tree of call paths ----

uint32_t NewRoot() {
  if (g_nnodes >= kMaxNodes) return kNoParent;
  const uint32_t i = g_nnodes++;
  g_node[i].fn = 0;
  g_node[i].parent = kNoParent;
  g_node[i].count = 0;
  return i;
}

uint32_t ChildOf(uint32_t parent, uint64_t fn) {
  const uint32_t h = static_cast<uint32_t>((fn * 0x9E3779B97F4A7C15ull) >> 32) ^ (parent * 2654435761u);
  for (uint32_t i = 0;; ++i) {
    const uint32_t s = (h + i) & (kSlots - 1);
    const uint32_t v = g_slot[s];
    if (!v) {
      if (g_nnodes >= kMaxNodes) {
        ++g_dropped;
        return parent;
      }
      const uint32_t idx = g_nnodes++;
      g_node[idx].fn = fn;
      g_node[idx].parent = parent;
      g_node[idx].count = 0;
      g_slot[s] = idx + 1;
      return idx;
    }
    const Node& nd = g_node[v - 1];
    if (nd.parent == parent && nd.fn == fn) return v - 1;
  }
}

void Record(Thread& t, const uint64_t* fns, int n, bool topInExe) {
  uint32_t cur = t.root;
  if (cur == kNoParent) return;
  for (int i = n - 1; i >= 0; --i) cur = ChildOf(cur, fns[i]);
  ++g_node[cur].count;
  ++t.samples;
  if (topInExe) ++t.inExe;
}

// ---- threads ----

void NameThread(Thread& t) {
  if (!g_getDesc) return;
  PWSTR w = nullptr;
  if (SUCCEEDED(g_getDesc(t.h, &w)) && w) {
    WideCharToMultiByte(CP_UTF8, 0, w, -1, t.name, sizeof(t.name), nullptr, nullptr);
    t.name[sizeof(t.name) - 1] = 0;
    for (char* p = t.name; *p; ++p)
      if (*p <= ' ') *p = '_';
    LocalFree(w);
  }
}

void RefreshThreads(bool sampling) {
  HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
  if (snap == INVALID_HANDLE_VALUE) return;
  const DWORD pid = GetCurrentProcessId(), self = GetCurrentThreadId();
  bool seen[kMaxThreads] = {};
  THREADENTRY32 te;
  te.dwSize = sizeof(te);
  for (BOOL ok = Thread32First(snap, &te); ok; ok = Thread32Next(snap, &te)) {
    if (te.th32OwnerProcessID != pid || te.th32ThreadID == self) continue;
    int at = -1;
    for (int i = 0; i < g_nthr; ++i)
      if (g_thr[i].tid == te.th32ThreadID) at = i;
    if (at < 0) {
      if (g_nthr >= kMaxThreads) continue;
      HANDLE h = OpenThread(THREAD_SUSPEND_RESUME | THREAD_GET_CONTEXT | THREAD_QUERY_INFORMATION, FALSE, te.th32ThreadID);
      if (!h) continue;
      at = g_nthr++;
      Thread& t = g_thr[at];
      memset(&t, 0, sizeof(t));
      t.tid = te.th32ThreadID;
      t.h = h;
      t.live = true;
      t.root = sampling ? NewRoot() : kNoParent;
      t.cpu0 = CpuMs(h);
      if (g_ntQuery) {
        struct {
          LONG exitStatus;
          PVOID teb;
          PVOID processId, threadId;
          ULONG_PTR affinity;
          LONG priority, basePriority;
        } info = {};
        if (g_ntQuery(h, 0, &info, sizeof(info), nullptr) >= 0 && info.teb) {
          __try {
            t.stackHi = *reinterpret_cast<const uintptr_t*>(reinterpret_cast<const uint8_t*>(info.teb) + 8);
          } __except (EXCEPTION_EXECUTE_HANDLER) {
          }
        }
      }
    }
    seen[at] = true;
    Thread& t = g_thr[at];
    if (t.live) {
      t.cpu1 = CpuMs(t.h);
      if (!t.name[0]) NameThread(t);
    }
  }
  CloseHandle(snap);
  for (int i = 0; i < g_nthr; ++i) {
    if (g_thr[i].live && !seen[i]) {
      g_thr[i].live = false;
      CloseHandle(g_thr[i].h);
      g_thr[i].h = nullptr;
    }
  }
}

void SampleOnce() {
  uint64_t fns[kMaxDepth + 1];
  LARGE_INTEGER q0, q1, f;
  QueryPerformanceFrequency(&f);
  QueryPerformanceCounter(&q0);
  for (int i = 0; i < g_nthr; ++i) {
    Thread& t = g_thr[i];
    if (!t.live) continue;
    if (SuspendThread(t.h) == static_cast<DWORD>(-1)) {
      t.live = false;
      CloseHandle(t.h);
      t.h = nullptr;
      continue;
    }
    alignas(16) CONTEXT c;
    memset(&c, 0, sizeof(c));
    c.ContextFlags = CONTEXT_CONTROL | CONTEXT_INTEGER;
    bool top = false;
    int n = 0;
    if (GetThreadContext(t.h, &c)) n = Walk(c, t.stackHi, fns, &top);
    ResumeThread(t.h);
    if (n > 0) Record(t, fns, n, top);
  }
  QueryPerformanceCounter(&q1);
  g_cycleUs += static_cast<uint64_t>((q1.QuadPart - q0.QuadPart) * 1000000 / f.QuadPart);
  ++g_cycles;
}

// ---- reports ----

bool ProfilesDir(wchar_t* out, size_t n) {
  wchar_t base[MAX_PATH];
  const DWORD k = GetEnvironmentVariableW(L"ORION_USER_DIR", base, MAX_PATH);  // tests only, as in Orion
  if (!(k > 0 && k < MAX_PATH)) {
    PWSTR docs = nullptr;
    if (FAILED(SHGetKnownFolderPath(FOLDERID_Documents, 0, nullptr, &docs)) || !docs) return false;
    _snwprintf(base, MAX_PATH, L"%ls\\Paradox Interactive\\Stellaris", docs);
    CoTaskMemFree(docs);
  }
  _snwprintf(out, n, L"%ls\\modloader\\profiles", base);
  return SHCreateDirectoryExW(nullptr, out, nullptr) == ERROR_SUCCESS || GetFileAttributesW(out) != INVALID_FILE_ATTRIBUTES;
}

void WriteReport(uint64_t startedAt, uint64_t endedAt) {
  wchar_t dir[MAX_PATH];
  if (!ProfilesDir(dir, MAX_PATH)) {
    Say(ORION_LOG_ERROR, "can't find or create the profiles folder; the report is lost");
    return;
  }
  SYSTEMTIME st;
  GetLocalTime(&st);
  wchar_t path[MAX_PATH + 64];
  _snwprintf(path, MAX_PATH + 64, L"%ls\\profile-%04d%02d%02d-%02d%02d%02d.txt", dir, st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond);
  FILE* f = _wfopen(path, L"wb");
  if (!f) {
    Say(ORION_LOG_ERROR, "can't write the report");
    return;
  }
  uint32_t* sub = static_cast<uint32_t*>(calloc(g_nnodes ? g_nnodes : 1, sizeof(uint32_t)));
  for (uint32_t i = g_nnodes; i-- > 0;) {  // a child is always created after its parent, so one backwards pass sums every subtree
    if (sub) sub[i] += g_node[i].count;
    if (sub && g_node[i].parent != kNoParent) sub[g_node[i].parent] += sub[i];
  }
  const uint64_t ms = (endedAt - startedAt) / 10000;
  fprintf(f, "orion-profile 1\nbuild %s\ngame %s\nexe_base %llx\ninterval_ms %d\nduration_ms %llu\ncycles %u\navg_cycle_us %llu\ndropped %u\ntruncated %u\n", g_host->gameBuild,
          g_host->gameVersion, static_cast<unsigned long long>(g_host->gameBase), g_interval, static_cast<unsigned long long>(ms), g_cycles,
          static_cast<unsigned long long>(g_cycles ? g_cycleUs / g_cycles : 0), g_dropped, g_truncated);
  for (int i = 0; i < g_nmod; ++i) fprintf(f, "module %llx %x %s\n", static_cast<unsigned long long>(g_mod[i].base), g_mod[i].size, g_mod[i].name);
  uint64_t total = 0;
  for (int i = 0; i < g_nthr; ++i) {
    const Thread& t = g_thr[i];
    total += t.samples;
    fprintf(f, "thread %d %lu %u %u %llu %s\n", i, t.tid, t.samples, t.inExe, static_cast<unsigned long long>(t.cpu1 >= t.cpu0 ? t.cpu1 - t.cpu0 : 0),
            t.name[0] ? t.name : "-");
    fprintf(f, "root %u %d\n", t.root, i);
  }
  if (sub) {
    for (uint32_t i = 0; i < g_nnodes; ++i) {
      if (g_node[i].parent == kNoParent || sub[i] < 2) continue;  // a path seen once is noise; roots are written above
      fprintf(f, "n %u %u %llx %u\n", i, g_node[i].parent, static_cast<unsigned long long>(g_node[i].fn), g_node[i].count);
    }
  }
  fclose(f);
  free(sub);
  char shown[MAX_PATH * 3];
  WideCharToMultiByte(CP_UTF8, 0, path, -1, shown, sizeof(shown), nullptr, nullptr);
  Say(ORION_LOG_INFO, "sampling stopped: %llu samples over %llu ms (%u cycles, %llu us each), %u call paths, %u dropped, %u truncated", static_cast<unsigned long long>(total),
      static_cast<unsigned long long>(ms), g_cycles, static_cast<unsigned long long>(g_cycles ? g_cycleUs / g_cycles : 0), g_nnodes, g_dropped, g_truncated);
  Say(ORION_LOG_INFO, "report: %s", shown);
}

// ---- the sampler thread ----

// The chord is read from the keyboard state, whichever window has focus (a borderless or overlaid game window can't be relied on to be "in front").
bool HotkeyEdge(bool& wasDown) {
  if (!g_vk) return false;
  const bool down = (GetAsyncKeyState(g_vk) & 0x8000) && (GetAsyncKeyState(VK_CONTROL) & 0x8000) && (GetAsyncKeyState(VK_SHIFT) & 0x8000);
  const bool edge = down && !wasDown;
  wasDown = down;
  return edge;
}

void Begin() {
  for (int i = 0; i < g_nthr; ++i)
    if (g_thr[i].h) CloseHandle(g_thr[i].h);
  g_nthr = 0;
  memset(g_slot, 0, sizeof(g_slot));
  g_nnodes = 0;
  g_dropped = g_truncated = g_cycles = 0;
  g_cycleUs = 0;
  RefreshModules();
  RefreshThreads(true);
  Say(ORION_LOG_INFO, "sampling started: %d threads, every %d ms", g_nthr, g_interval);
}

void End(uint64_t startedAt) {
  RefreshThreads(true);
  RefreshModules();
  const uint64_t endedAt = Now100ns();
  WriteReport(startedAt, endedAt);
  for (int i = 0; i < g_nthr; ++i) {
    if (g_thr[i].h) CloseHandle(g_thr[i].h);
    g_thr[i].h = nullptr;
    g_thr[i].live = false;
  }
}

DWORD WINAPI SamplerMain(LPVOID) {
  SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_ABOVE_NORMAL);
  HANDLE timer = CreateWaitableTimerExW(nullptr, nullptr, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
  bool sampling = false, autoRun = g_autoSeconds > 0, keyDown = false;
  uint64_t startedAt = 0;
  ULONGLONG startTick = 0, lastRefresh = 0, lastKey = 0, runMs = kMaxRunMs;
  for (;;) {
    if (!sampling) {
      if (autoRun) {  // the start-up run: once, as soon as the plugin is loaded
        autoRun = false;
        runMs = static_cast<ULONGLONG>(g_autoSeconds) * 1000;
      } else {
        Sleep(25);
        if (!HotkeyEdge(keyDown)) continue;
        runMs = kMaxRunMs;
      }
      Begin();
      sampling = true;
      startedAt = Now100ns();
      startTick = lastRefresh = GetTickCount64();
      continue;
    }
    if (timer) {
      LARGE_INTEGER due;
      due.QuadPart = -static_cast<LONGLONG>(g_interval) * 10000;
      SetWaitableTimer(timer, &due, 0, nullptr, nullptr, FALSE);
      WaitForSingleObject(timer, INFINITE);
    } else {
      Sleep(g_interval);
    }
    SampleOnce();
    const ULONGLONG now = GetTickCount64();
    if (now - lastRefresh >= 1000) {
      RefreshThreads(true);
      RefreshModules();
      lastRefresh = now;
    }
    bool stop = now - startTick >= runMs;
    if (now - lastKey >= 20) {
      lastKey = now;
      if (HotkeyEdge(keyDown)) stop = true;
    }
    if (stop) {
      End(startedAt);
      sampling = false;
    }
  }
}

}  // namespace

extern "C" ORION_PLUGIN_EXPORT int OrionPluginInit(const OrionHost* host) {
  g_host = host;
  int64_t v = 0;
  if (host->getInt(host, "intervalMs", &v)) g_interval = static_cast<int>(v < 1 ? 1 : v > 50 ? 50 : v);
  if (host->getInt(host, "hotkey", &v)) g_vk = static_cast<int>(v < 0 ? 0 : v > 255 ? 255 : v);
  if (host->getInt(host, "autoSeconds", &v)) g_autoSeconds = static_cast<int>(v < 0 ? 0 : v > 900 ? 900 : v);
  if (g_vk == 0 && g_autoSeconds == 0) {
    Say(ORION_LOG_WARN, "the hotkey is off and there is no automatic run, so there is nothing to do");
    return ORION_INIT_OK;
  }
  g_ntQuery = reinterpret_cast<NtQueryInformationThreadFn>(GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "NtQueryInformationThread"));
  g_getDesc = reinterpret_cast<GetThreadDescriptionFn>(GetProcAddress(GetModuleHandleW(L"kernel32.dll"), "GetThreadDescription"));
  if (HANDLE t = CreateThread(nullptr, 0, SamplerMain, nullptr, 0, nullptr)) {
    CloseHandle(t);
  } else {
    Say(ORION_LOG_ERROR, "couldn't start the sampler thread");
    return 2;
  }
  if (g_autoSeconds > 0) Say(ORION_LOG_INFO, "will sample for %d s from now", g_autoSeconds);
  if (g_vk) Say(ORION_LOG_INFO, "press Ctrl+Shift+(virtual key %d) to start and again to stop sampling; every %d ms", g_vk, g_interval);
  return ORION_INIT_OK;
}
