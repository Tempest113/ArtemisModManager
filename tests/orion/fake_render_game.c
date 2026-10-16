/* Stands in for stellaris.exe when the render throttle is tested. FakeRenderFrame (fake_render_frame.asm) plays the game's frame-pump helper: this
 * program calls it in a tight loop for 1.5 seconds and says how many calls there were, how many actually ran, and what the last run saw.
 *   fake_render_game.exe probe   prints the helper's offset from the exe's base and its first 5 bytes (no winmm, no plugin) */
#include <windows.h>
#include <mmsystem.h>
#include <stdio.h>
#include <string.h>

#pragma comment(lib, "winmm.lib")

extern void FakeRenderFrame(unsigned long long a, unsigned long long b, unsigned long long c, unsigned long long d);
extern volatile unsigned long long g_ran, g_sum;

int main(int argc, char** argv) {
  unsigned long long calls = 0;
  if (argc > 1 && strcmp(argv[1], "probe") == 0) {
    const unsigned char* f = (const unsigned char*)FakeRenderFrame;
    printf("rva=%llx bytes=%02x%02x%02x%02x%02x\n", (unsigned long long)(f - (const unsigned char*)GetModuleHandleW(NULL)), f[0], f[1], f[2], f[3], f[4]);
    return 0;
  }
  timeGetTime();
  {
    const DWORD end = GetTickCount() + 1500;
    while (GetTickCount() < end) {
      FakeRenderFrame(1, 2, 3, 4);
      calls++;
    }
  }
  printf("calls=%llu ran=%llu sum=%llu\n", calls, g_ran, g_sum);
  return 0;
}
