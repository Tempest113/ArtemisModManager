// Render throttle, as a plugin (fragile tier: it patches the first instruction of one game function).
//
// The game's simulation calls a "pump one whole frame now" helper from dozens of places between the steps of its own long updates; each call handles
// input, updates the interface and draws a frame whatever the frame-rate cap says. Fast-forwarding spends about a fifth of the main thread there. This
// plugin makes that helper do nothing when its last real run finished less than minIntervalMs ago. The normal frame loop is untouched, so the screen
// still updates; only the extra forced frames inside the simulation's own steps are thinned out. The helper is also where the game takes in clicks
// and keys during a long update, so a call that finds a key or mouse button waiting in the queue always runs, throttle or not.
//
// It needs from the build data (see orion/tools/add_address.py): the helper's address `render_frame`, and `render_frame_prologue` / `_len`, the first
// bytes of the function as a little-endian number and their count. The bytes must be whole position-independent instructions (no relative call, jump or
// address); they are copied to a trampoline and the function is overwritten with a 5-byte jmp. If the game's bytes differ from the data (a patch moved
// things), nothing is touched and the plugin shows as skipped.
//
// Settings: minIntervalMs (0..2000, default 250; 0 installs nothing), hotkey (virtual-key code pressed with Ctrl+Shift to switch the throttle off and on
// during play, to compare speeds; default 119 = F8; 0 = off; the same chord with Alt held, or the key before it (F7), steps the interval through 50, 100,
// 250 and 500 ms). Every 10 seconds in which the helper was called, the plugin's log says how many calls ran
// and how many were skipped.
//
// The patch is written from OrionPluginInit, which runs inside the stub's DllMain before any game code, so no other thread is executing the function.
#define _CRT_SECURE_NO_WARNINGS
#include <windows.h>

#include <stdarg.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

#include "orion_plugin.h"

#pragma comment(lib, "user32.lib")

namespace {

typedef void (*FrameFn)(uint64_t, uint64_t, uint64_t, uint64_t);

const OrionHost* g_host;
FrameFn g_orig;
LONG64 g_wantTicks;                 // the configured gap, in QueryPerformanceCounter ticks
volatile LONG64 g_minTicks;         // the gap in force (0 while switched off)
volatile LONG64 g_lastEnd;          // when the last real run finished
volatile LONG64 g_nextInputCheck;   // the earliest time the input queue is looked at again
volatile LONG64 g_ran, g_skipped;   // plain counters: they are only read for the log
volatile LONG64 g_forInput;         // calls that ran inside the gap only because input was waiting
bool g_forceInput;                  // tests only
LONG64 g_freq;                      // QueryPerformanceCounter ticks per second
int g_ms;                           // the gap in force when the throttle is on, in ms
int g_hotkey = 0x77;

void Say(int level, const char* fmt, ...) {
  char line[400];
  va_list ap;
  va_start(ap, fmt);
  vsnprintf(line, sizeof(line), fmt, ap);
  va_end(ap);
  g_host->log(g_host, level, line);
}

// A key or mouse button is waiting in this thread's queue. The helper is also where the game takes in input while it works through a long update, so
// a pump that has a click to take is never skipped: the click is handled as it always was. Mouse movement alone doesn't count. (Windows only raises
// these flags for real hardware input, so the tests use ORION_THROTTLE_INPUT_WAITING=1 to stand in for it.)
bool InputWaiting() {
  return g_forceInput || (HIWORD(GetQueueStatus(QS_KEY | QS_MOUSEBUTTON)) & (QS_KEY | QS_MOUSEBUTTON)) != 0;
}

// Called in place of the game's helper, with the same four register arguments.
void Detour(uint64_t a, uint64_t b, uint64_t c, uint64_t d) {
  const LONG64 gap = g_minTicks;
  LARGE_INTEGER t;
  if (gap) {
    QueryPerformanceCounter(&t);
    if (t.QuadPart - g_lastEnd < gap) {
      // The helper is called hundreds of thousands of times a second, and asking Windows about the queue is a system call, so ask at most once a millisecond:
      // a click is taken in within about a millisecond of arriving, which is as good as the game's own pump timing.
      if (t.QuadPart < g_nextInputCheck || (g_nextInputCheck = t.QuadPart + g_freq / 1000, !InputWaiting())) {
        ++g_skipped;
        return;
      }
      ++g_forInput;
    }
  }
  g_orig(a, b, c, d);
  ++g_ran;
  if (gap) {
    QueryPerformanceCounter(&t);
    g_lastEnd = t.QuadPart;
  }
}

// Memory for the trampoline within +-2 GB of the target, so a 5-byte jmp reaches it.
uint8_t* AllocNear(uint8_t* target, size_t size) {
  SYSTEM_INFO si;
  GetSystemInfo(&si);
  const uintptr_t gran = si.dwAllocationGranularity, at = reinterpret_cast<uintptr_t>(target), reach = 0x70000000;
  const uintptr_t lo = at > reach ? at - reach : 0x10000, hi = at + reach;
  for (uintptr_t a = (at + gran - 1) / gran * gran; a < hi;) {
    MEMORY_BASIC_INFORMATION mbi;
    if (!VirtualQuery(reinterpret_cast<void*>(a), &mbi, sizeof(mbi))) break;
    if (mbi.State == MEM_FREE)
      if (void* p = VirtualAlloc(reinterpret_cast<void*>(a), size, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE)) return static_cast<uint8_t*>(p);
    a = (reinterpret_cast<uintptr_t>(mbi.BaseAddress) + mbi.RegionSize + gran - 1) / gran * gran;
  }
  for (uintptr_t a = at / gran * gran; a > lo;) {
    a -= gran;
    MEMORY_BASIC_INFORMATION mbi;
    if (!VirtualQuery(reinterpret_cast<void*>(a), &mbi, sizeof(mbi))) break;
    if (mbi.State == MEM_FREE) {
      if (void* p = VirtualAlloc(reinterpret_cast<void*>(a), size, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE)) return static_cast<uint8_t*>(p);
    } else if (mbi.AllocationBase) {
      a = reinterpret_cast<uintptr_t>(mbi.AllocationBase);
    }
  }
  return nullptr;
}

void AbsoluteJump(uint8_t* at, const void* to) {  // jmp [rip+0] followed by the 8-byte address
  at[0] = 0xFF;
  at[1] = 0x25;
  memset(at + 2, 0, 4);
  memcpy(at + 6, &to, 8);
}

// Overwrites the first `len` bytes of `target` with a jmp to `detour`, after checking they are `expect`. On success *orig runs the original function.
bool Install(uint8_t* target, const uint8_t* expect, size_t len, void* detour, void** orig, const char** why) {
  if (len < 5 || len > 15) return *why = "the stolen byte count must be 5 to 15", false;
  MEMORY_BASIC_INFORMATION mbi;
  if (!VirtualQuery(target, &mbi, sizeof(mbi)) || mbi.State != MEM_COMMIT || !(mbi.Protect & (PAGE_EXECUTE | PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY)))
    return *why = "the address isn't executable code", false;
  if (memcmp(target, expect, len) != 0) return *why = "the function's first bytes differ from the build data (the game was patched?)", false;
  uint8_t* page = AllocNear(target, 4096);
  if (!page) return *why = "no memory within reach of the function for the trampoline", false;
  memcpy(page, target, len);                 // the stolen instructions, then back to the rest of the function
  AbsoluteJump(page + len, target + len);
  AbsoluteJump(page + 64, detour);           // a stepping stone: the detour may be farther than 2 GB from the function
  DWORD old;
  VirtualProtect(page, 4096, PAGE_EXECUTE_READ, &old);
  FlushInstructionCache(GetCurrentProcess(), page, 128);
  const int64_t rel = (page + 64) - (target + 5);
  if (rel > INT32_MAX || rel < INT32_MIN) return *why = "the trampoline is out of reach", false;
  DWORD prot;
  if (!VirtualProtect(target, len, PAGE_EXECUTE_READWRITE, &prot)) return *why = "can't make the function writable", false;
  target[0] = 0xE9;
  const int32_t rel32 = static_cast<int32_t>(rel);
  memcpy(target + 1, &rel32, 4);
  for (size_t i = 5; i < len; ++i) target[i] = 0x90;
  VirtualProtect(target, len, prot, &old);
  FlushInstructionCache(GetCurrentProcess(), target, len);
  *orig = page;
  return true;
}

bool Chord(int vk) {
  return (GetAsyncKeyState(vk) & 0x8000) && (GetAsyncKeyState(VK_CONTROL) & 0x8000) && (GetAsyncKeyState(VK_SHIFT) & 0x8000);
}

void Toggle() {
  g_minTicks = g_minTicks ? 0 : g_wantTicks;
  Say(ORION_LOG_INFO, "throttle %s", g_minTicks ? "ON" : "OFF");
  Beep(g_minTicks ? 1200 : 600, 70);  // so the player hears that the chord was taken
}

void Step() {  // 50, 100, 250, 500 ms, then round again
  static const int steps[] = {50, 100, 250, 500};
  int ms = steps[0];
  for (int s : steps)
    if (s > g_ms) {
      ms = s;
      break;
    }
  g_ms = ms;
  g_wantTicks = ms * g_freq / 1000;
  if (g_minTicks) g_minTicks = g_wantTicks;
  Say(ORION_LOG_INFO, "interval now %d ms (throttle %s)", ms, g_minTicks ? "on" : "off");
  for (int i = 0; i < (ms == 50 ? 1 : ms == 100 ? 2 : ms == 250 ? 3 : 4); ++i) Beep(900, 60), Sleep(60);  // one beep for 50 ms ... four for 500 ms
}

DWORD WINAPI Helper(LPVOID) {
  bool down = false, downNext = false, seen[13] = {};
  ULONGLONG lastLog = GetTickCount64();
  LONG64 lastRan = 0, lastSkipped = 0, lastInput = 0;
  for (;;) {
    Sleep(10);  // short enough to catch a quick tap of the chord
    if (g_hotkey) {
      // Ctrl+Shift+hotkey toggles; with Alt also held it steps the interval (so does Ctrl+Shift+ the key before it, where that key gets through).
      const bool now = Chord(g_hotkey);
      if (now && !down) {
        if (GetAsyncKeyState(VK_MENU) & 0x8000) Step();
        else Toggle();
      }
      down = now;
      const bool next = g_hotkey > 1 && Chord(g_hotkey - 1);
      if (next && !downNext) Step();
      downNext = next;
      // Which Ctrl+Shift+F-keys reach the game at all (something else on the machine can take a chord first): each first press is logged.
      const bool mods = (GetAsyncKeyState(VK_CONTROL) & 0x8000) && (GetAsyncKeyState(VK_SHIFT) & 0x8000);
      for (int k = 1; k <= 12; ++k) {
        const bool d = mods && (GetAsyncKeyState(0x6F + k) & 0x8000);
        if (d && !seen[k]) Say(ORION_LOG_INFO, "Ctrl+Shift+F%d seen", k);
        seen[k] = d;
      }
    }
    const ULONGLONG t = GetTickCount64();
    if (t - lastLog >= 10000) {
      const LONG64 ran = g_ran, skipped = g_skipped, input = g_forInput;
      if (ran != lastRan || skipped != lastSkipped)
        Say(ORION_LOG_INFO, "last %llu s: %lld pumps ran (%lld of them only because a click or key was waiting), %lld skipped (throttle %s)",
            static_cast<unsigned long long>((t - lastLog) / 1000), static_cast<long long>(ran - lastRan), static_cast<long long>(input - lastInput),
            static_cast<long long>(skipped - lastSkipped), g_minTicks ? "on" : "off");
      lastRan = ran;
      lastSkipped = skipped;
      lastInput = input;
      lastLog = t;
    }
  }
}

}  // namespace

extern "C" ORION_PLUGIN_EXPORT int OrionPluginInit(const OrionHost* host) {
  g_host = host;
  int64_t ms = 250, hk = g_hotkey;
  host->getInt(host, "minIntervalMs", &ms);
  host->getInt(host, "hotkey", &hk);
  ms = ms < 0 ? 0 : ms > 2000 ? 2000 : ms;
  char env[8];
  g_forceInput = GetEnvironmentVariableA("ORION_THROTTLE_INPUT_WAITING", env, sizeof(env)) > 0 && env[0] == '1';
  g_hotkey = static_cast<int>(hk < 0 ? 0 : hk > 255 ? 255 : hk);
  if (ms == 0) {
    Say(ORION_LOG_INFO, "minIntervalMs is 0: nothing installed");
    return ORION_INIT_OK;
  }
  int64_t prologue = 0, len = 0;
  void* target = host->size >= sizeof(OrionHost) ? host->resolve(host, "render_frame") : nullptr;
  if (!target || !host->getBuildValue(host, "render_frame_prologue", &prologue) || !host->getBuildValue(host, "render_frame_prologue_len", &len) || len < 5 || len > 8) {
    Say(ORION_LOG_WARN, "this game build has no render_frame data (address, render_frame_prologue, render_frame_prologue_len), so the throttle can't be installed");
    return ORION_INIT_UNSUPPORTED;
  }
  uint8_t expect[8];
  memcpy(expect, &prologue, 8);
  const char* why = "";
  if (!Install(static_cast<uint8_t*>(target), expect, static_cast<size_t>(len), reinterpret_cast<void*>(&Detour), reinterpret_cast<void**>(&g_orig), &why)) {
    Say(ORION_LOG_WARN, "not installed: %s", why);
    return ORION_INIT_UNSUPPORTED;
  }
  LARGE_INTEGER f;
  QueryPerformanceFrequency(&f);
  g_freq = f.QuadPart;
  g_ms = static_cast<int>(ms);
  g_wantTicks = g_minTicks = ms * g_freq / 1000;
  if (HANDLE t = CreateThread(nullptr, 0, Helper, nullptr, 0, nullptr)) CloseHandle(t);
  Say(ORION_LOG_INFO, "installed: the frame pump runs at most every %lld ms%s", static_cast<long long>(ms), g_hotkey ? "; Ctrl+Shift+key switches it off and on" : "");
  return ORION_INIT_OK;
}
