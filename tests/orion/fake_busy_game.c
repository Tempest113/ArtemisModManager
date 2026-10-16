/* Stands in for stellaris.exe when the sampling profiler is tested: loads the stub through winmm, then spends a few seconds in a known call chain on its
 * main thread while a second thread sleeps, and prints where the functions are so the test can look for them in the report.
 *   HotParent -> HotLeaf  (has a stack frame, so the compiler gives it unwind data)
 *   HotParent -> FlatLeaf (uses registers only: a leaf function with no unwind data at all, which is what many small hot functions in a real game are) */
#include <windows.h>
#include <mmsystem.h>
#include <stdio.h>

#pragma comment(lib, "winmm.lib")

__declspec(noinline) unsigned HotLeaf(unsigned x) {
  volatile unsigned buf[16];
  int i;
  for (i = 0; i < 16; i++) buf[i] = x + i;
  for (i = 0; i < 1500; i++) buf[i & 15] = buf[(i + 1) & 15] * 1664525u + 1013904223u;
  return buf[3];
}

__declspec(noinline) unsigned FlatLeaf(unsigned x) {
  int i;
  for (i = 0; i < 3000; i++) x = x * 1103515245u + 12345u;
  return x;
}

__declspec(noinline) unsigned HotParent(unsigned x) { return HotLeaf(x) + FlatLeaf(x) + 1; }

static DWORD WINAPI Idle(LPVOID p) {
  (void)p;
  Sleep(10000);
  return 0;
}

int main(void) {
  unsigned x = 1;
  const DWORD end = GetTickCount() + 3500;
  timeGetTime();
  CloseHandle(CreateThread(NULL, 0, Idle, NULL, 0, NULL));
  while (GetTickCount() < end) x = HotParent(x);
  printf("base=%llx leaf=%llx flat=%llx parent=%llx x=%u\n", (unsigned long long)(size_t)GetModuleHandleW(NULL), (unsigned long long)(size_t)HotLeaf,
         (unsigned long long)(size_t)FlatLeaf, (unsigned long long)(size_t)HotParent, x);
  return 0;
}
