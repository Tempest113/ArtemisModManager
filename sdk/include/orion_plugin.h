/* Orion plugin ABI, version 1 (D11).
 *
 * A plugin is a DLL listed in its mod's modloader.json. Orion loads it from a private copy, after checking that the
 * user approved exactly these bytes, and calls OrionPluginInit once.
 *
 * Rules that keep this ABI stable:
 *   - Plain C. One integer version (ORION_API_VERSION); within a version the host table only grows at its end, so
 *     check host->size before using a function added later.
 *   - Memory that crosses the boundary is allocated and freed through host->alloc and host->free, never with the
 *     plugin's own allocator.
 *   - OrionPluginInit runs while Windows is still starting the game, before the game's own code. Keep it short: do not
 *     wait for other threads and do not load libraries other than ones the game already uses.
 *   - There is no unload and no shutdown call: a native DLL can't be unloaded safely (D23).
 */
#ifndef ORION_PLUGIN_H
#define ORION_PLUGIN_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define ORION_API_VERSION 1

enum { ORION_LOG_DEBUG = 0, ORION_LOG_INFO = 1, ORION_LOG_WARN = 2, ORION_LOG_ERROR = 3 };

typedef struct OrionHost OrionHost;

struct OrionHost {
  uint32_t size;               /* sizeof(OrionHost) as the host was built */
  uint32_t apiVersion;         /* ORION_API_VERSION the host implements */
  const char* orionVersion;    /* e.g. "0.1.0" */
  const char* gameBuild;       /* build id: PE timestamp and image size, e.g. "66f1a2b3-5a4000" */
  const char* gameVersion;     /* e.g. "v4.5.2" (never empty: plugins only load on a verified build) */
  uintptr_t gameBase;          /* base address of the game's exe */

  void* (*alloc)(size_t bytes);
  void (*free)(void* p);

  /* Writes a line to this plugin's own log (Documents\...\Stellaris\modloader\plugins\<mod>.log). */
  void (*log)(const OrionHost* host, int level, const char* utf8);

  /* Settings and feature switches the user chose, by the id in modloader.json. Each getter returns 1 and fills
   * *out if the value exists with that type, otherwise 0 and leaves *out alone. A feature is a bool. */
  int (*getBool)(const OrionHost* host, const char* id, int* out);
  int (*getInt)(const OrionHost* host, const char* id, int64_t* out);
  int (*getFloat)(const OrionHost* host, const char* id, double* out);
  /* Text, enum and path settings: a copy made with host->alloc (free it with host->free), or NULL. */
  char* (*getString)(const OrionHost* host, const char* id);

  /* An address from this game build's verified data (gameBase + the stored offset), or NULL if it has none by that
   * name. Orion never hooks anything itself; a plugin that patches the game does it with these. */
  void* (*resolve)(const OrionHost* host, const char* name);

  /* Added after the first release of API 1; check host->size covers these before using them (the host fills them in order). */

  /* A number from this game build's verified data (a struct offset, a token id, a flag), by name: returns 1 and fills *out, or 0. */
  int (*getBuildValue)(const OrionHost* host, const char* name, int64_t* out);

  /* Asks for callback(user) to run once, on the game's main thread, the first time the game touches a file under its own common folder: the
   * game's databases exist by then and it has parsed no script yet, which is when script keywords have to be registered. Call it from
   * OrionPluginInit. Returns 1 if scheduled, 0 if it can't be done on this build. An exception escaping the callback is caught and logged. */
  int (*onGameStart)(const OrionHost* host, void (*callback)(void* user), void* user);
};

/* What OrionPluginInit returns. Anything else is shown as a failure. */
enum { ORION_INIT_OK = 0, ORION_INIT_UNSUPPORTED = 1 /* can't run on this game build: shown as skipped, not failed; say why in the log */ };

/* The one function every plugin file exports. Return 0 for success; anything else is reported as a failure and the
 * plugin is shown as failed in Artemis (the game still starts). */
typedef int (*OrionPluginInitFn)(const OrionHost* host);

#ifdef _WIN32
#define ORION_PLUGIN_EXPORT __declspec(dllexport)
#else
#define ORION_PLUGIN_EXPORT
#endif

/* Usage: ORION_PLUGIN_EXPORT int OrionPluginInit(const OrionHost* host) { ... return 0; } (extern "C" in C++) */

#ifdef __cplusplus
}
#endif

#endif
