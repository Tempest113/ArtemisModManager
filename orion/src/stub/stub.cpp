// The Artemis winmm.dll stub (D14, D45). It forwards every winmm export to the real System32 copy and, inside
// stellaris.exe and nowhere else, loads the Orion core and starts it. It installs no hooks, writes no files and
// starts no threads. If anything is missing or wrong it does nothing and the game runs as vanilla.
//
// The core is found through a pointer file: <data>\orion\current.txt names a version folder, and
// <data>\orion\<version>\orion.dll is the core. <data> is ARTEMIS_DATA_DIR (set by Artemis at every start it makes),
// else %APPDATA%\Artemis.

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>

#include <string.h>

#include "stub_exports.h"

extern "C" {
void* stub_targets[kStubExportCount] = {};
}

namespace {

// Loads System32's own winmm.dll by full path (a separate module although ours has the same name) and points every thunk at it.
bool ForwardToSystemWinmm() {
  wchar_t path[MAX_PATH];
  const UINT n = GetSystemDirectoryW(path, MAX_PATH - 16);
  if (!n || n >= MAX_PATH - 16) return false;
  lstrcpyW(path + n, L"\\winmm.dll");
  const HMODULE real = LoadLibraryW(path);
  if (!real) return false;
  for (int i = 0; i < kStubExportCount; ++i) {
    const StubExport& e = kStubExports[i];
    stub_targets[i] = reinterpret_cast<void*>(GetProcAddress(real, e.name ? e.name : MAKEINTRESOURCEA(e.ordinal)));
  }
  return true;
}

// The Paradox Launcher's dowser.exe sits in the same folder and loads this DLL too; it must be left alone (D45).
bool HostIsTheGame() {
  wchar_t path[MAX_PATH * 2];
  const DWORD n = GetModuleFileNameW(nullptr, path, MAX_PATH * 2);
  if (!n || n >= MAX_PATH * 2) return false;
  const wchar_t* name = path;
  for (const wchar_t* p = path; *p; ++p)
    if (*p == L'\\' || *p == L'/') name = p + 1;
  return lstrcmpiW(name, L"stellaris.exe") == 0;
}

// Appends text to out (a fixed buffer); false if it doesn't fit.
bool Append(wchar_t* out, size_t cap, size_t& len, const wchar_t* text) {
  const size_t n = lstrlenW(text);
  if (len + n + 1 > cap) return false;
  memcpy(out + len, text, n * sizeof(wchar_t));
  len += n;
  out[len] = 0;
  return true;
}

bool DataDir(wchar_t* out, size_t cap) {
  const DWORD n = GetEnvironmentVariableW(L"ARTEMIS_DATA_DIR", out, static_cast<DWORD>(cap));
  if (n > 0 && n < cap) return true;
  wchar_t appdata[MAX_PATH];
  const DWORD m = GetEnvironmentVariableW(L"APPDATA", appdata, MAX_PATH);
  if (!m || m >= MAX_PATH) return false;
  size_t len = 0;
  out[0] = 0;
  return Append(out, cap, len, appdata) && Append(out, cap, len, L"\\Artemis");
}

// The version folder named in current.txt: a short run of letters, digits, '.', '_' and '-', so it can't point elsewhere.
bool ReadVersion(const wchar_t* pointerFile, wchar_t* version, size_t cap) {
  const HANDLE h = CreateFileW(pointerFile, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
  if (h == INVALID_HANDLE_VALUE) return false;
  char text[64];
  DWORD got = 0;
  const BOOL ok = ReadFile(h, text, sizeof(text), &got, nullptr);
  CloseHandle(h);
  if (!ok) return false;
  size_t n = 0;
  while (n < got && ((text[n] >= '0' && text[n] <= '9') || (text[n] >= 'a' && text[n] <= 'z') || (text[n] >= 'A' && text[n] <= 'Z') ||
                     text[n] == '.' || text[n] == '_' || text[n] == '-'))
    ++n;
  if (n == 0 || n >= cap || (text[0] == '.' && (n == 1 || text[1] == '.'))) return false;
  for (size_t i = 0; i < n; ++i) version[i] = static_cast<wchar_t>(text[i]);
  version[n] = 0;
  return true;
}

void StartOrion() {
  wchar_t data[MAX_PATH * 2], file[MAX_PATH * 3], version[48];
  if (!DataDir(data, MAX_PATH * 2)) return;
  size_t len = 0;
  file[0] = 0;
  if (!Append(file, MAX_PATH * 3, len, data) || !Append(file, MAX_PATH * 3, len, L"\\orion\\current.txt")) return;
  if (!ReadVersion(file, version, 48)) return;
  len = 0;
  file[0] = 0;
  if (!Append(file, MAX_PATH * 3, len, data) || !Append(file, MAX_PATH * 3, len, L"\\orion\\") || !Append(file, MAX_PATH * 3, len, version) ||
      !Append(file, MAX_PATH * 3, len, L"\\orion.dll"))
    return;
  const HMODULE core = LoadLibraryExW(file, nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
  if (!core) return;
  using StartFn = void (*)();
  if (const auto start = reinterpret_cast<StartFn>(GetProcAddress(core, "OrionCoreStart"))) start();
}

}  // namespace

BOOL WINAPI DllMain(HINSTANCE self, DWORD reason, LPVOID) {
  if (reason != DLL_PROCESS_ATTACH) return TRUE;
  DisableThreadLibraryCalls(self);
  // Without the real winmm the exports would jump to nothing, and the game can't use sound or timers anyway: refuse to load.
  if (!ForwardToSystemWinmm()) return FALSE;
  if (HostIsTheGame()) StartOrion();
  return TRUE;
}
