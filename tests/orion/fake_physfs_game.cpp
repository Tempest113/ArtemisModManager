// Stands in for stellaris.exe the way fake_game.c does, and also exports the PhysFS functions the honest-mode spike plugin looks for, under the
// same C++ names the real game exports them by. It plays out a start-up in miniature: touches a few files, initialises "PhysFS", mounts a game
// folder and then a mod folder, and waits a moment so a plugin watching from another thread can act. A mount of the plugin's archive is written to
// the file named by FAKE_MOUNT_OUT so the test can open it as a zip.
#include <windows.h>
#include <mmsystem.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include <string>
#include <vector>

#pragma comment(lib, "winmm.lib")

namespace {
volatile LONG g_init = 0;
CRITICAL_SECTION g_lock;
std::vector<std::string> g_path;
bool g_mounted = false;
}  // namespace

__declspec(dllexport) int PHYSFS_isInit() { return g_init; }

__declspec(dllexport) char** PHYSFS_getSearchPath() {
  EnterCriticalSection(&g_lock);
  char** list = static_cast<char**>(calloc(g_path.size() + 1, sizeof(char*)));
  for (size_t i = 0; i < g_path.size(); ++i) list[i] = _strdup(g_path[i].c_str());
  LeaveCriticalSection(&g_lock);
  return list;
}

__declspec(dllexport) void PHYSFS_freeList(void* p) {
  char** list = static_cast<char**>(p);
  if (!list) return;
  for (char** q = list; *q; ++q) free(*q);
  free(list);
}

__declspec(dllexport) int PHYSFS_mountMemory(const void* buf, unsigned long long len, void (*)(void*), const char* name, const char*, int) {
  if (const char* out = getenv("FAKE_MOUNT_OUT")) {
    if (FILE* f = fopen(out, "wb")) { fwrite(buf, 1, static_cast<size_t>(len), f); fclose(f); }
  }
  EnterCriticalSection(&g_lock);
  g_path.push_back(name);
  g_mounted = true;
  LeaveCriticalSection(&g_lock);
  return 1;
}

__declspec(dllexport) int PHYSFS_exists(const char* name) { return g_mounted && strcmp(name, "common/modloader/manifest.txt") == 0; }
__declspec(dllexport) const char* PHYSFS_getRealDir(const char*) { return g_mounted ? "orion-fingerprint.zip" : nullptr; }
__declspec(dllexport) const char* PHYSFS_getLastError() { return "no error"; }

__declspec(dllexport) char** PHYSFS_enumerateFiles(const char*) {
  char** list = static_cast<char**>(calloc(2, sizeof(char*)));
  if (g_mounted) list[0] = _strdup("manifest.txt");
  return list;
}

static void Mount(const char* dir) {
  EnterCriticalSection(&g_lock);
  g_path.push_back(dir);
  LeaveCriticalSection(&g_lock);
}

static void Touch(const wchar_t* path) {
  const HANDLE h = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);  // files that aren't there are fine
  if (h != INVALID_HANDLE_VALUE) CloseHandle(h);
}

// Takes the fingerprint archive out of the search path, as a game that rebuilds its file list would.
static void Drop(const char* name) {
  EnterCriticalSection(&g_lock);
  for (size_t i = 0; i < g_path.size(); ++i)
    if (g_path[i] == name) { g_path.erase(g_path.begin() + i); g_mounted = false; break; }
  LeaveCriticalSection(&g_lock);
}

// FAKE_MOUNTS: folders (separated by ';') to mount as the game's mods, in order, instead of the built-in one.
// FAKE_LINGER_MS: how long to stay alive at the end (default 400). FAKE_DROP_ARCHIVE=1: drop the fingerprint archive once it appears.
// A thread that reads from the exe's own common folder before the main thread does: it must not trigger a plugin's game-start callback.
static DWORD WINAPI Early(LPVOID) {
  Sleep(20);
  wchar_t exe[MAX_PATH];
  GetModuleFileNameW(nullptr, exe, MAX_PATH);
  std::wstring dir = exe;
  dir.resize(dir.find_last_of(L'\\') + 1);
  Touch((dir + L"common\\from-another-thread.txt").c_str());
  return 0;
}

int main() {
  InitializeCriticalSection(&g_lock);
  const DWORD t1 = timeGetTime();
  if (const HANDLE t = CreateThread(nullptr, 0, Early, nullptr, 0, nullptr)) CloseHandle(t);
  Touch(L"C:\\no-such-folder\\dlc_load.json");
  Touch(L"C:\\no-such-folder\\mod\\Something.mod");
  Sleep(60);
  g_init = 1;
  Mount("C:\\no-such-folder\\game");
  Touch(L"C:\\no-such-folder\\game\\common\\defines\\00_defines.txt");
  {
    wchar_t exe[MAX_PATH];
    GetModuleFileNameW(nullptr, exe, MAX_PATH);
    std::wstring dir = exe;
    dir.resize(dir.find_last_of(L'\\') + 1);
    Touch((dir + L"Common\\defines\\00_defines.txt").c_str());  // the game's own scripts, as the main thread reads them (case differs on purpose)
  }
  Sleep(60);
  if (const char* mounts = getenv("FAKE_MOUNTS")) {  // the game replaces its whole file list in one burst, mods in order
    std::string all = mounts;
    for (size_t start = 0; start <= all.size();) {
      const size_t end = all.find(';', start);
      const std::string one = all.substr(start, end == std::string::npos ? std::string::npos : end - start);
      if (!one.empty()) Mount(one.c_str());
      if (end == std::string::npos) break;
      start = end + 1;
    }
  } else {
    Mount("C:\\no-such-folder\\user\\mod\\Orion Honest Spike");
  }
  const char* linger = getenv("FAKE_LINGER_MS");
  const DWORD stayUntil = timeGetTime() + (linger ? static_cast<DWORD>(atoi(linger)) : 400);  // time for a watcher to see the search path and act
  bool dropped = getenv("FAKE_DROP_ARCHIVE") == nullptr;
  while (timeGetTime() < stayUntil) {
    if (!dropped && g_mounted) { Sleep(80); Drop("orion-fingerprint.zip"); dropped = true; }
    Sleep(5);
  }
  return timeGetTime() - t1 >= 10 ? 0 : 1;
}
