/* Stands in for stellaris.exe (and, copied under another name, for the Launcher's dowser.exe). It reaches winmm through its import
 * table, as the game does, and exits 0 only if the calls behave the way the real winmm's do. If the stub forwarded wrongly this
 * either crashes or returns something else. */
#include <windows.h>
#include <mmsystem.h>
#include <stdio.h>

#pragma comment(lib, "winmm.lib")

int main(void) {
  const DWORD t1 = timeGetTime();
  Sleep(30);
  const DWORD t2 = timeGetTime();
  TIMECAPS caps = {0, 0};
  const MMRESULT capsResult = timeGetDevCaps(&caps, sizeof(caps));
  const MMRESULT begin = timeBeginPeriod(1);
  const MMRESULT end = timeEndPeriod(1);
  /* device counts are whatever the machine has; calling them with plain and pointer arguments checks the thunks keep both intact */
  const UINT waves = waveOutGetNumDevs();
  const UINT midis = midiOutGetNumDevs();
  const UINT mixers = mixerGetNumDevs();
  const UINT joys = joyGetNumDevs();
  wchar_t text[128] = L"";
  mciGetErrorStringW(MCIERR_INVALID_DEVICE_ID, text, 128);
  const int ok = t2 - t1 >= 10 && capsResult == TIMERR_NOERROR && caps.wPeriodMin >= 1 && begin == TIMERR_NOERROR && end == TIMERR_NOERROR &&
                 joys > 0 && text[0] != 0;
  printf("time %lu->%lu caps %u..%u waves %u midis %u mixers %u joys %u mci \"%ls\" -> %s\n", (unsigned long)t1, (unsigned long)t2,
         caps.wPeriodMin, caps.wPeriodMax, waves, midis, mixers, joys, text, ok ? "ok" : "WRONG");
  return ok ? 0 : 1;
}
