#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include "filehooks.h"

#include <cstdio>
#include <cstring>
#include <cwctype>
#include <vector>

namespace orion {
namespace {

struct Pending {
  GameStartFn callback;
  void* user;
  std::string owner;
};

SRWLOCK g_lock = SRWLOCK_INIT;
std::vector<Pending> g_pending;
Log* g_log = nullptr;
DWORD g_mainThread = 0;
std::wstring g_common;  // lower case, backslashes: "<folder of the exe>\common"
volatile LONG g_state = 0;  // 0 not installed, 1 waiting for the game's first read of its scripts, 2 done
bool g_patched = false, g_patchOk = false;

decltype(&CreateFileW) o_CreateFileW = nullptr;
decltype(&FindFirstFileW) o_FindFirstFileW = nullptr;
decltype(&FindFirstFileExW) o_FindFirstFileExW = nullptr;

bool UnderCommon(const wchar_t* path) {
  const size_t n = g_common.size();
  size_t i = 0;
  for (; i < n; ++i) {
    if (!path[i]) return false;
    wchar_t c = path[i] == L'/' ? L'\\' : static_cast<wchar_t>(towlower(path[i]));
    if (c != g_common[i]) return false;
  }
  return path[i] == 0 || path[i] == L'\\' || path[i] == L'/';
}

bool CallGuarded(GameStartFn callback, void* user, unsigned long* code) {
  __try {
    callback(user);
    return true;
  } __except (*code = GetExceptionCode(), EXCEPTION_EXECUTE_HANDLER) {
    return false;
  }
}

void OnFile(const wchar_t* path) {
  if (g_state != 1 || !path || GetCurrentThreadId() != g_mainThread || !UnderCommon(path)) return;
  if (InterlockedCompareExchange(&g_state, 2, 1) != 1) return;
  g_log->Write("info", "File hooks: the game started reading its own scripts (" + Narrow(path) + "); running the plugins' game-start callbacks.");
  std::vector<Pending> run;
  AcquireSRWLockExclusive(&g_lock);
  run.swap(g_pending);
  ReleaseSRWLockExclusive(&g_lock);
  for (const Pending& p : run) {
    unsigned long code = 0;
    if (!CallGuarded(p.callback, p.user, &code)) {
      char hex[16];
      snprintf(hex, sizeof(hex), "%08lx", code);
      g_log->Write("error", p.owner + " crashed in its game-start callback (0x" + hex + "); the game continues.");
    }
  }
}

HANDLE WINAPI Hk_CreateFileW(LPCWSTR p, DWORD a, DWORD s, LPSECURITY_ATTRIBUTES sa, DWORD d, DWORD f, HANDLE t) {
  OnFile(p);
  return o_CreateFileW(p, a, s, sa, d, f, t);
}
HANDLE WINAPI Hk_FindFirstFileW(LPCWSTR p, LPWIN32_FIND_DATAW data) {
  OnFile(p);
  return o_FindFirstFileW(p, data);
}
HANDLE WINAPI Hk_FindFirstFileExW(LPCWSTR p, FINDEX_INFO_LEVELS l, LPVOID data, FINDEX_SEARCH_OPS op, LPVOID filter, DWORD flags) {
  OnFile(p);
  return o_FindFirstFileExW(p, l, data, op, filter, flags);
}

// Points one kernel32 import of the exe at `detour`. The original is what the slot holds now (so patches stack), or kernel32's own export if it isn't bound yet.
bool PatchImport(HMODULE exe, const char* name, void* detour, void** original) {
  auto* base = reinterpret_cast<BYTE*>(exe);
  auto* nt = reinterpret_cast<IMAGE_NT_HEADERS*>(base + reinterpret_cast<IMAGE_DOS_HEADER*>(base)->e_lfanew);
  const auto& dir = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
  if (!dir.VirtualAddress) return false;
  for (auto* imp = reinterpret_cast<IMAGE_IMPORT_DESCRIPTOR*>(base + dir.VirtualAddress); imp->Name; ++imp) {
    if (_stricmp(reinterpret_cast<char*>(base + imp->Name), "kernel32.dll") != 0 || !imp->OriginalFirstThunk) continue;
    auto* names = reinterpret_cast<IMAGE_THUNK_DATA*>(base + imp->OriginalFirstThunk);
    auto* slots = reinterpret_cast<IMAGE_THUNK_DATA*>(base + imp->FirstThunk);
    for (; names->u1.AddressOfData; ++names, ++slots) {
      if (IMAGE_SNAP_BY_ORDINAL(names->u1.Ordinal)) continue;
      if (strcmp(reinterpret_cast<IMAGE_IMPORT_BY_NAME*>(base + names->u1.AddressOfData)->Name, name) != 0) continue;
      *original = slots->u1.Function ? reinterpret_cast<void*>(slots->u1.Function) : reinterpret_cast<void*>(GetProcAddress(GetModuleHandleW(L"kernel32.dll"), name));
      if (!*original) return false;
      DWORD old;
      if (!VirtualProtect(&slots->u1.Function, sizeof(ULONG_PTR), PAGE_READWRITE, &old)) return false;
      slots->u1.Function = reinterpret_cast<ULONG_PTR>(detour);
      VirtualProtect(&slots->u1.Function, sizeof(ULONG_PTR), old, &old);
      return true;
    }
  }
  return false;
}

}  // namespace

bool AddGameStartCallback(Log& log, GameStartFn callback, void* user, const std::string& owner) {
  if (!callback) return false;
  if (!g_patched) {
    g_patched = true;
    g_log = &log;
    g_mainThread = GetCurrentThreadId();
    wchar_t exe[MAX_PATH * 2];
    const DWORD n = GetModuleFileNameW(nullptr, exe, MAX_PATH * 2);
    if (n && n < MAX_PATH * 2) {
      std::wstring dir(exe, n);
      dir.resize(dir.find_last_of(L"\\/") + 1);
      g_common = dir + L"common";
      for (wchar_t& c : g_common) c = c == L'/' ? L'\\' : static_cast<wchar_t>(towlower(c));
      const HMODULE host = GetModuleHandleW(nullptr);
      const bool a = PatchImport(host, "CreateFileW", reinterpret_cast<void*>(&Hk_CreateFileW), reinterpret_cast<void**>(&o_CreateFileW));
      const bool b = PatchImport(host, "FindFirstFileW", reinterpret_cast<void*>(&Hk_FindFirstFileW), reinterpret_cast<void**>(&o_FindFirstFileW));
      const bool c = PatchImport(host, "FindFirstFileExW", reinterpret_cast<void*>(&Hk_FindFirstFileExW), reinterpret_cast<void**>(&o_FindFirstFileExW));
      g_patchOk = a || b || c;  // any one is enough to see the first read
      log.Write("info", std::string("File hooks: watching CreateFileW ") + (a ? "yes" : "no") + ", FindFirstFileW " + (b ? "yes" : "no") + ", FindFirstFileExW " + (c ? "yes" : "no") + ".");
      if (g_patchOk) InterlockedExchange(&g_state, 1);
    }
  }
  if (!g_patchOk || g_state != 1) return false;  // too late: the game has already started reading its scripts
  AcquireSRWLockExclusive(&g_lock);
  g_pending.push_back({callback, user, owner});
  ReleaseSRWLockExclusive(&g_lock);
  return true;
}

}  // namespace orion
