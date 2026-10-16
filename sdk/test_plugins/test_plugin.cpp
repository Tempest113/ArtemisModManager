// A plugin for testing Orion's host, not for playing with. One DLL serves every test: what it does comes from its settings.
//   mode   "ok" (default) | "fail" (init returns 3) | "crash" (access violation, so the game dies mid-load)
//   proof  a file to write what the plugin saw (build, settings, resolved address, memory round trip)
//   order  a file to append `who` to, one line per plugin, to show the order plugins were loaded in
#include <windows.h>

#include <stdio.h>
#include <string.h>

#include "orion_plugin.h"

// startProof: a file to write when the game-start callback runs (which thread it ran on, how many times), to test host->onGameStart.
static char g_startProof[512];
static DWORD g_initThread;
static volatile LONG g_startCalls;

static void OnStart(void*) {
  const LONG n = InterlockedIncrement(&g_startCalls);
  if (FILE* f = fopen(g_startProof, "wb")) {
    fprintf(f, "calls=%ld\nmain_thread=%d\n", n, GetCurrentThreadId() == g_initThread);
    fclose(f);
  }
}

extern "C" ORION_PLUGIN_EXPORT int OrionPluginInit(const OrionHost* host) {
  g_initThread = GetCurrentThreadId();
  host->log(host, ORION_LOG_INFO, "test plugin starting");
  char* mode = host->getString(host, "mode");
  char* proof = host->getString(host, "proof");
  char* order = host->getString(host, "order");
  char* who = host->getString(host, "who");

  // Say in its own log what it was handed, which is what a person trying Orion in the real game reads.
  {
    int64_t shown = -1;
    host->getInt(host, "count", &shown);
    char* text = host->getString(host, "title");
    char line[512];
    snprintf(line, sizeof(line), "Orion %s, game %s, build %s, API %u, exe base %s", host->orionVersion, host->gameVersion, host->gameBuild, host->apiVersion,
             host->gameBase == reinterpret_cast<uintptr_t>(GetModuleHandleW(nullptr)) ? "matches" : "DIFFERS");
    host->log(host, ORION_LOG_INFO, line);
    snprintf(line, sizeof(line), "settings: mode=%s, count=%lld, title=%s", mode ? mode : "(none)", static_cast<long long>(shown), text ? text : "(none)");
    host->log(host, ORION_LOG_INFO, line);
    if (text) host->free(text);
  }

  if (order && who) {
    if (FILE* f = fopen(order, "ab")) { fprintf(f, "%s\n", who); fclose(f); }
  }
  if (mode && strcmp(mode, "crash") == 0) {
    SetErrorMode(SEM_NOGPFAULTERRORBOX | SEM_FAILCRITICALERRORS);  // no "stopped working" box: the test just wants the process to die
    *reinterpret_cast<volatile int*>(0) = 1;
  }
  if (mode && strcmp(mode, "unsupported") == 0) {
    host->log(host, ORION_LOG_WARN, "this game build isn't one the plugin can run on (mode=unsupported)");
    return ORION_INIT_UNSUPPORTED;
  }
  if (char* startProof = host->getString(host, "startProof")) {
    snprintf(g_startProof, sizeof(g_startProof), "%s", startProof);
    host->free(startProof);
    const int scheduled = host->size >= sizeof(OrionHost) && host->onGameStart(host, OnStart, nullptr);
    char line[64];
    snprintf(line, sizeof(line), "onGameStart scheduled: %d", scheduled);
    host->log(host, ORION_LOG_INFO, line);
  }
  if (mode && strcmp(mode, "fail") == 0) {
    host->log(host, ORION_LOG_WARN, "failing on purpose (mode=fail)");
    return 3;
  }
  host->log(host, ORION_LOG_INFO, "test plugin started");

  if (proof) {
    int flag = -1;
    int64_t count = -1;
    double speed = -1;
    host->getBool(host, "flag", &flag);
    host->getInt(host, "count", &count);
    host->getFloat(host, "speed", &speed);
    char* title = host->getString(host, "title");
    char* copy = static_cast<char*>(host->alloc(16));  // allocated by the host, freed by the host
    if (copy) strcpy(copy, "round trip");
    void* found = host->resolve(host, "test.value");
    if (FILE* f = fopen(proof, "wb")) {
      fprintf(f, "size_ok=%d\n", host->size == sizeof(OrionHost));
      fprintf(f, "api=%u\n", host->apiVersion);
      fprintf(f, "orion=%s\n", host->orionVersion);
      fprintf(f, "build=%s\n", host->gameBuild);
      fprintf(f, "game=%s\n", host->gameVersion);
      fprintf(f, "base_ok=%d\n", host->gameBase == reinterpret_cast<uintptr_t>(GetModuleHandleW(nullptr)));
      fprintf(f, "flag=%d\ncount=%lld\nspeed=%.3f\n", flag, static_cast<long long>(count), speed);
      fprintf(f, "title=%s\n", title ? title : "(none)");
      fprintf(f, "memory=%s\n", copy ? copy : "(none)");
      fprintf(f, "address_offset=%llx\n", found ? static_cast<unsigned long long>(reinterpret_cast<uintptr_t>(found) - host->gameBase) : 0ull);
      fprintf(f, "missing_is_null=%d\n", host->resolve(host, "no.such.address") == nullptr);
      int64_t number = -1;
      const int haveNumber = host->getBuildValue(host, "test.number", &number);
      fprintf(f, "build_value=%d:%lld\nbuild_value_missing=%d\n", haveNumber, static_cast<long long>(number), host->getBuildValue(host, "no.such.value", &number));
      fprintf(f, "missing_setting=%d\n", host->getBool(host, "no.such.setting", &flag));
      fclose(f);
    }
    if (title) host->free(title);
    if (copy) host->free(copy);
  }
  if (mode) host->free(mode);
  if (proof) host->free(proof);
  if (order) host->free(order);
  if (who) host->free(who);
  return 0;
}
