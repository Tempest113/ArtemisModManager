// Orion, the core (D12, D14, D48). Started by the stub inside stellaris.exe, before the game's own code runs. It reads the files
// Artemis wrote, decides plugin by plugin whether to load it, loads the ones that pass, and writes down what happened.
// It installs no hooks and ships no plugins; with nothing to load, the game runs as vanilla.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <initguid.h>
#include <KnownFolders.h>
#include <shlobj.h>

#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <map>
#include <set>
#include <string>
#include <vector>

#include "filehooks.h"
#include "honest.h"
#include "json.h"
#include "manifest.h"
#include "orion_plugin.h"
#include "util.h"

namespace orion {
namespace {

constexpr const char* kOrionVersion = "0.1.0";

// True from just before a plugin's DLL is loaded until its init has returned. A crash in that window must reach Windows, so the game
// dies and loading.json is left behind (D24); a crash anywhere else is Orion's own bug, and the game carries on as vanilla.
volatile bool g_inPlugin = false;

struct PluginResult {
  std::string mod, id, state, reason, detail;
};

// What a plugin is handed. `pub` is first so the pointer a plugin passes back can be turned into this again.
struct PluginHost {
  OrionHost pub;
  std::string mod;
  Log log;
  const Json* values = nullptr;
};

struct State {
  std::wstring dataDir, userDir, orionDir, modloaderDir;
  Log log;
  std::string buildId, gameVersion, startedUtc;
  bool buildKnown = false;
  uintptr_t base = 0;
  std::map<std::string, uint64_t> addresses;
  std::map<std::string, int64_t> values;  // the build data's numbers: struct offsets, token ids, flags
  Json trust, settings;
  bool pluginsOff = false;  // ORION_PLUGINS=off
  std::vector<PluginResult> results;
  std::set<std::string> loadedIds;
  std::map<std::string, std::vector<std::string>> loadedIncompatible;
  // honest mode (D53): the loaded plugins that count for the checksum, and how putting them there went
  std::vector<HonestPlugin> counted;
  std::string honestState = "notNeeded", honestDetail = "No loaded plugin counts for the game's checksum.";
  SRWLOCK statusLock = SRWLOCK_INIT;
};

// Never freed: plugins keep pointers into it for the life of the process.
State* g = nullptr;

// ---- host functions the plugins call ----

PluginHost* HostOf(const OrionHost* h) { return reinterpret_cast<PluginHost*>(const_cast<OrionHost*>(h)); }

const Json* ValueOf(const OrionHost* h, const char* id) {
  if (!h || !id) return nullptr;
  const PluginHost* ph = HostOf(h);
  return ph->values ? ph->values->Get(id) : nullptr;
}

void* HostAlloc(size_t bytes) { return malloc(bytes); }
void HostFree(void* p) { free(p); }

void HostLog(const OrionHost* h, int level, const char* text) {
  static const char* const names[] = {"debug", "info", "warn", "error"};
  if (h && text) HostOf(h)->log.Write(names[level < 0 ? 0 : level > 3 ? 3 : level], text);
}

int HostGetBool(const OrionHost* h, const char* id, int* out) {
  const Json* v = ValueOf(h, id);
  if (!v || v->type != Json::Bool || !out) return 0;
  *out = v->b ? 1 : 0;
  return 1;
}

int HostGetInt(const OrionHost* h, const char* id, int64_t* out) {
  const Json* v = ValueOf(h, id);
  if (!v || v->type != Json::Number || !v->isInt || !out) return 0;
  *out = v->i;
  return 1;
}

int HostGetFloat(const OrionHost* h, const char* id, double* out) {
  const Json* v = ValueOf(h, id);
  if (!v || v->type != Json::Number || !out) return 0;
  *out = v->d;
  return 1;
}

char* HostGetString(const OrionHost* h, const char* id) {
  const Json* v = ValueOf(h, id);
  if (!v || v->type != Json::String) return nullptr;
  char* copy = static_cast<char*>(malloc(v->s.size() + 1));
  if (copy) memcpy(copy, v->s.c_str(), v->s.size() + 1);
  return copy;
}

int HostGetBuildValue(const OrionHost*, const char* name, int64_t* out) {
  if (!name || !out) return 0;
  const auto it = g->values.find(name);
  if (it == g->values.end()) return 0;
  *out = it->second;
  return 1;
}

int HostOnGameStart(const OrionHost* h, void (*callback)(void*), void* user) {
  return h && AddGameStartCallback(g->log, callback, user, HostOf(h)->mod) ? 1 : 0;
}

void* HostResolve(const OrionHost*, const char* name) {
  if (!name) return nullptr;
  const auto it = g->addresses.find(name);
  return it == g->addresses.end() ? nullptr : reinterpret_cast<void*>(g->base + static_cast<uintptr_t>(it->second));
}

// ---- reading what Artemis and the game wrote ----

std::wstring DocumentsStellarisDir() {
  if (std::wstring over = Env(L"ORION_USER_DIR"); !over.empty()) return over;  // for tests; Artemis never sets it
  PWSTR docs = nullptr;
  if (FAILED(SHGetKnownFolderPath(FOLDERID_Documents, 0, nullptr, &docs)) || !docs) return {};
  std::wstring dir = Join(Join(Join(docs, L"Paradox Interactive"), L"Stellaris"), L"");
  CoTaskMemFree(docs);
  while (!dir.empty() && dir.back() == L'\\') dir.pop_back();
  return dir;
}

std::wstring DataDir() {
  if (std::wstring v = Env(L"ARTEMIS_DATA_DIR"); !v.empty()) return v;
  const std::wstring appdata = Env(L"APPDATA");
  return appdata.empty() ? std::wstring() : Join(appdata, L"Artemis");
}

// Reads and parses a JSON file; says why in the Orion log if it can't.
bool ReadJsonFile(const std::wstring& file, Json& out, const char* what) {
  std::string text, err;
  if (!ReadAll(file, text)) { g->log.Write("info", std::string(what) + ": not found (" + Narrow(file) + ")"); return false; }
  if (!ParseJson(text, out, err)) { g->log.Write("warn", std::string(what) + " can't be read: " + err); return false; }
  return true;
}

// The build id is the exe's PE timestamp and image size, taken from the headers already in memory (D15).
std::string BuildIdOfExe(uintptr_t& base) {
  const auto* exe = reinterpret_cast<const BYTE*>(GetModuleHandleW(nullptr));
  base = reinterpret_cast<uintptr_t>(exe);
  const auto* dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(exe);
  if (!exe || dos->e_magic != IMAGE_DOS_SIGNATURE) return "unknown";
  const auto* nt = reinterpret_cast<const IMAGE_NT_HEADERS*>(exe + dos->e_lfanew);
  if (nt->Signature != IMAGE_NT_SIGNATURE) return "unknown";
  char buf[40];
  snprintf(buf, sizeof(buf), "%08x-%x", nt->FileHeader.TimeDateStamp, nt->OptionalHeader.SizeOfImage);
  return buf;
}

// <data>\orion\builds\<build id>.json: { "build": "<id>", "game": "v4.5.2", "addresses": { "<name>": <offset from the exe's base> } }.
// Without a file for this exact build, no plugin loads (D15).
void LoadBuildData() {
  Json j;
  const std::wstring file = Join(Join(g->orionDir, L"builds"), Widen(g->buildId) + L".json");
  if (!ReadJsonFile(file, j, "Build data")) return;
  if (j.Text("build") != g->buildId) { g->log.Write("warn", "Build data is for another build and is ignored."); return; }
  g->gameVersion = j.Text("game");
  if (g->gameVersion.empty()) { g->log.Write("warn", "Build data has no \"game\" version and is ignored."); return; }
  if (const Json* a = j.Get("addresses"); a && a->type == Json::Object) {
    for (const auto& m : a->members) {
      if (m.second.type == Json::Number && m.second.isInt && m.second.i >= 0) g->addresses[m.first] = static_cast<uint64_t>(m.second.i);
      else if (m.second.type == Json::String) g->addresses[m.first] = strtoull(m.second.s.c_str(), nullptr, 0);
    }
  }
  if (const Json* v = j.Get("values"); v && v->type == Json::Object)
    for (const auto& m : v->members)
      if (m.second.type == Json::Number && m.second.isInt) g->values[m.first] = m.second.i;
  g->buildKnown = true;
  g->log.Write("info", "Build data found: game " + g->gameVersion + ", " + std::to_string(g->addresses.size()) + " addresses, " + std::to_string(g->values.size()) + " values.");
}

// The folder a mod's .mod file points at, or false (zip mods and mods without a path can't carry a plugin).
bool ResolveModFolder(const std::string& entry, std::wstring& folder) {
  auto rooted = [](const std::wstring& p) { return (p.size() >= 2 && p[1] == L':') || (p.size() >= 2 && p[0] == L'\\' && p[1] == L'\\'); };
  std::wstring outer = WithBackslashes(Widen(entry));
  if (!rooted(outer)) outer = Join(g->userDir, outer);
  std::string text;
  if (!ReadAll(outer, text)) return false;
  if (text.compare(0, 3, "\xEF\xBB\xBF") == 0) text.erase(0, 3);
  std::string path;
  for (size_t pos = 0; pos < text.size() && path.empty();) {
    size_t eol = text.find('\n', pos);
    if (eol == std::string::npos) eol = text.size();
    const std::string line = text.substr(pos, eol - pos);
    pos = eol + 1;
    size_t i = line.find_first_not_of(" \t");
    if (i == std::string::npos || line.compare(i, 4, "path") != 0) continue;
    i = line.find_first_not_of(" \t", i + 4);
    if (i == std::string::npos || line[i] != '=') continue;
    const size_t open = line.find('"', i);
    const size_t close = open == std::string::npos ? open : line.find('"', open + 1);
    if (close != std::string::npos) path = line.substr(open + 1, close - open - 1);
  }
  if (path.empty()) return false;
  folder = WithBackslashes(Widen(path));
  if (!rooted(folder)) folder = Join(g->userDir, folder);
  return true;
}

std::string StemOf(const std::string& entry) {
  const size_t slash = entry.find_last_of("/\\");
  std::string name = slash == std::string::npos ? entry : entry.substr(slash + 1);
  const size_t dot = name.find_last_of('.');
  return dot == std::string::npos ? name : name.substr(0, dot);
}

// A mod key is the stem of a .mod file name, so it is nearly always a valid file name already (and often has spaces); this only guards the rest.
std::string SafeFileName(const std::string& key) {
  std::string out;
  for (const char c : key) out += (static_cast<unsigned char>(c) < 0x20 || strchr("\\/:*?\"<>|", c)) ? '_' : c;
  while (!out.empty() && (out.back() == '.' || out.back() == ' ')) out.pop_back();
  return out.empty() ? "_" : out;
}

// ---- writing down what happened ----

void WriteStatus() {
  AcquireSRWLockExclusive(&g->statusLock);  // the honest-mode watcher writes it too, from its own thread
  std::string j = "{\n  \"version\": 1,\n  \"orion\": " + JsonQuote(kOrionVersion) + ",\n  \"build\": " + JsonQuote(g->buildId) +
                  ",\n  \"game\": " + JsonQuote(g->gameVersion) + ",\n  \"buildSupported\": " + (g->buildKnown ? "true" : "false") +
                  ",\n  \"startedUtc\": " + JsonQuote(g->startedUtc) + ",\n  \"honest\": { \"state\": " + JsonQuote(g->honestState) + ", \"detail\": " +
                  JsonQuote(g->honestDetail) + ", \"plugins\": " + std::to_string(g->counted.size()) + " },\n  \"plugins\": [";
  for (size_t i = 0; i < g->results.size(); ++i) {
    const PluginResult& r = g->results[i];
    j += std::string(i ? ",\n    " : "\n    ") + "{ \"mod\": " + JsonQuote(r.mod) + ", \"id\": " + JsonQuote(r.id) + ", \"state\": " + JsonQuote(r.state) +
         ", \"reason\": " + JsonQuote(r.reason) + ", \"detail\": " + JsonQuote(r.detail) + " }";
  }
  j += g->results.empty() ? "]\n}\n" : "\n  ]\n}\n";
  WriteAtomic(Join(g->modloaderDir, L"status.json"), j);
  ReleaseSRWLockExclusive(&g->statusLock);
}

void Record(const std::string& mod, const std::string& id, const char* state, const char* reason, const std::string& detail) {
  g->results.push_back({mod, id, state, reason, detail});
  std::string line = mod + " (" + id + "): " + state;
  if (*reason) line += std::string(", ") + reason;
  if (!detail.empty()) line += ": " + detail;
  g->log.Write(std::string(state) == "failed" ? "error" : "info", line);
  WriteStatus();
}

void WriteMarker(const std::string& mod, const std::string& id, const std::string& file) {
  WriteAtomic(Join(g->modloaderDir, L"loading.json"),
              "{ \"mod\": " + JsonQuote(mod) + ", \"id\": " + JsonQuote(id) + ", \"file\": " + JsonQuote(file) + " }\n");
}

void RemoveMarker() { DeleteFileW(Join(g->modloaderDir, L"loading.json").c_str()); }

// ---- loading ----

// Opens the content-addressed copy of the plugin file with others kept from writing to it, checks that it holds exactly the approved
// bytes, loads it while that handle is still held, and calls its init. Returns "" or what went wrong.
std::string LoadFile(PluginHost* host, const std::string& file, const std::string& bytes, const std::string& hash, const char*& reason) {
  reason = "error";
  const std::wstring cacheDir = Join(g->orionDir, L"cache");
  if (!EnsureDir(cacheDir)) return "couldn't create the plugin cache folder";
  const std::wstring copy = Join(cacheDir, Widen(hash) + L".dll");

  HANDLE held = INVALID_HANDLE_VALUE;
  for (int attempt = 0; attempt < 2; ++attempt) {
    if ((attempt == 1 || !FileExists(copy)) && !WriteAtomic(copy, bytes)) return "couldn't write the plugin's private copy";
    held = CreateFileW(copy.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (held == INVALID_HANDLE_VALUE) return "couldn't open the plugin's private copy";
    std::string now;
    if (ReadHandle(held, now) && Sha256Hex(now) == hash) break;
    CloseHandle(held);
    held = INVALID_HANDLE_VALUE;
  }
  if (held == INVALID_HANDLE_VALUE) { reason = "hashChanged"; return "the plugin's private copy didn't match the approved file"; }

  std::string problem;
  g_inPlugin = true;
  const HMODULE module = LoadLibraryExW(copy.c_str(), nullptr, 0);
  if (!module) {
    problem = "Windows couldn't load " + file + " (error " + std::to_string(GetLastError()) + ")";
  } else if (const auto init = reinterpret_cast<OrionPluginInitFn>(GetProcAddress(module, "OrionPluginInit")); !init) {
    problem = file + " has no OrionPluginInit export";
  } else {
    int rc = 0;
    try { rc = init(&host->pub); }
    catch (...) { problem = file + " threw an exception from OrionPluginInit"; }
    if (problem.empty() && rc == ORION_INIT_UNSUPPORTED) { reason = "buildUnsupported"; problem = "The plugin says it can't run on this game build (its log says why)."; }
    else if (problem.empty() && rc != 0) problem = "OrionPluginInit in " + file + " returned " + std::to_string(rc);
  }
  g_inPlugin = false;
  CloseHandle(held);
  return problem;
}

PluginHost* NewHost(const std::string& mod) {
  auto* h = new PluginHost;
  h->pub.size = sizeof(OrionHost);
  h->pub.apiVersion = ORION_API_VERSION;
  h->pub.orionVersion = kOrionVersion;
  h->pub.gameBuild = g->buildId.c_str();
  h->pub.gameVersion = g->gameVersion.c_str();
  h->pub.gameBase = g->base;
  h->pub.alloc = HostAlloc;
  h->pub.free = HostFree;
  h->pub.log = HostLog;
  h->pub.getBool = HostGetBool;
  h->pub.getInt = HostGetInt;
  h->pub.getFloat = HostGetFloat;
  h->pub.getString = HostGetString;
  h->pub.resolve = HostResolve;
  h->pub.getBuildValue = HostGetBuildValue;
  h->pub.onGameStart = HostOnGameStart;
  h->mod = mod;
  h->log.SetPath(Join(Join(g->modloaderDir, L"plugins"), Widen(SafeFileName(mod)) + L".log"));
  return h;
}

// Decides one mod's plugin: every reason not to load it, in the order that is cheapest to check, then the load itself.
void Process(const std::string& key, const std::wstring& folder, const std::string& manifestText) {
  Manifest m;
  std::string err;
  if (!ParseManifest(manifestText, m, err)) { Record(key, "", "failed", "error", err); return; }

  if (g->pluginsOff) { Record(key, m.id, "skipped", "disabled", "Plugins are switched off for this run (ORION_PLUGINS=off)."); return; }
  const Json* pluginsEnabled = g->settings.Get("pluginsEnabled");
  if (!pluginsEnabled || pluginsEnabled->type != Json::Bool || !pluginsEnabled->b) {
    Record(key, m.id, "skipped", "disabled", "Plugins are switched off for this playset, or Artemis hasn't written plugin-settings.json yet.");
    return;
  }
  const Json* mods = g->settings.Get("mods");
  const Json* mine = mods ? mods->Get(key) : nullptr;
  const Json* enabled = mine ? mine->Get("enabled") : nullptr;
  if (!mine) { Record(key, m.id, "skipped", "disabled", "Artemis didn't list this plugin in plugin-settings.json."); return; }
  if (!enabled || enabled->type != Json::Bool || !enabled->b) { Record(key, m.id, "skipped", "disabled", "Switched off in this playset."); return; }

  if (m.apiVersion > ORION_API_VERSION) {
    Record(key, m.id, "skipped", "apiUnsupported", "Needs plugin API " + std::to_string(m.apiVersion) + "; this Orion offers " + std::to_string(ORION_API_VERSION) + ".");
    return;
  }
  if (!g->buildKnown) {
    Record(key, m.id, "skipped", "buildUnsupported", "Orion has no verified data for this game build (" + g->buildId + "), so it loads no plugins.");
    return;
  }
  // A plugin that changes how the game plays must show up in the game's checksum (D22, D53). The manifest's "cosmetic" is the author's claim; the
  // manager may send the user's choice as "countsForChecksum", which wins.
  const Json* countsFlag = mine->Get("countsForChecksum");
  const bool counts = countsFlag && countsFlag->type == Json::Bool ? countsFlag->b : !m.cosmetic;
  if (counts) {
    std::string why;
    if (!HonestAvailable(why)) {
      g->log.Write("warn", "Honest mode isn't available: " + why);
      Record(key, m.id, "skipped", "buildUnsupported", "It counts for the game's checksum, and Orion can't add it to the checksum on this game build (the game's file system functions weren't found), so it can't load.");
      return;
    }
  }
  if (!m.builds.empty()) {
    bool fits = false;
    std::string list;
    for (const std::string& b : m.builds) { fits = fits || VersionMatches(b, g->gameVersion); list += (list.empty() ? "" : ", ") + b; }
    if (!fits) { Record(key, m.id, "skipped", "buildUnsupported", "Supports game " + list + "; this game is " + g->gameVersion + "."); return; }
  }
  for (const std::string& dep : m.dependencies)
    if (!g->loadedIds.count(dep)) {
      Record(key, m.id, "skipped", "dependencyMissing", "Needs the plugin \"" + dep + "\", which is not loaded before it.");
      return;
    }
  for (const std::string& other : m.incompatible)
    if (g->loadedIds.count(other)) { Record(key, m.id, "skipped", "incompatible", "Can't run together with the plugin \"" + other + "\"."); return; }
  for (const auto& loaded : g->loadedIncompatible)
    for (const std::string& theirs : loaded.second)
      if (theirs == m.id) { Record(key, m.id, "skipped", "incompatible", "Can't run together with the plugin \"" + loaded.first + "\"."); return; }

  // Read every file once; what is hashed here is what gets copied and loaded.
  std::vector<std::string> bytes(m.files.size()), hashes(m.files.size());
  for (size_t i = 0; i < m.files.size(); ++i) {
    if (!ReadAll(Join(folder, WithBackslashes(Widen(m.files[i]))), bytes[i])) {
      Record(key, m.id, "skipped", "fileMissing", m.files[i] + " is missing from the mod folder.");
      return;
    }
    hashes[i] = Sha256Hex(bytes[i]);
  }
  const Json* plugins = g->trust.Get("plugins");
  const Json* approved = plugins ? plugins->Get(key) : nullptr;
  const Json* approvedFiles = approved ? approved->Get("files") : nullptr;
  if (!approvedFiles) { Record(key, m.id, "skipped", "untrusted", "Not approved yet. Approve it on the Plugins page in Artemis."); return; }
  for (size_t i = 0; i < m.files.size(); ++i) {
    const Json* want = approvedFiles->Get(m.files[i]);
    if (!want || want->type != Json::String) { Record(key, m.id, "skipped", "untrusted", m.files[i] + " isn't part of what was approved."); return; }
    if (Lower(want->s) != hashes[i]) { Record(key, m.id, "skipped", "hashChanged", m.files[i] + " has changed since it was approved."); return; }
  }

  PluginHost* host = NewHost(key);
  const Json* values = mine->Get("values");
  host->values = values && values->type == Json::Object ? values : nullptr;
  for (size_t i = 0; i < m.files.size(); ++i) {
    WriteMarker(key, m.id, m.files[i]);
    const char* reason = "error";
    const std::string problem = LoadFile(host, m.files[i], bytes[i], hashes[i], reason);
    if (!problem.empty()) { Record(key, m.id, std::strcmp(reason, "buildUnsupported") == 0 ? "skipped" : "failed", reason, problem); return; }
  }
  g->loadedIds.insert(m.id);
  g->loadedIncompatible[m.id] = m.incompatible;
  if (counts) {
    HonestPlugin p{m.id, m.version, {}};
    for (size_t i = 0; i < m.files.size(); ++i) p.files.push_back({m.files[i], hashes[i]});
    g->counted.push_back(std::move(p));
  }
  Record(key, m.id, "loaded", "", "");
}

// Called by the honest-mode watcher once it has mounted the fingerprint, or has given up.
void OnHonest(const std::string& state, const std::string& detail) {
  const size_t n = g->counted.size();
  AcquireSRWLockExclusive(&g->statusLock);
  g->honestState = state;
  g->honestDetail = state == "mounted" ? std::to_string(n) + (n == 1 ? " plugin counts" : " plugins count") + " for the game's checksum and " + (n == 1 ? "is" : "are") + " in the fingerprint." : detail;
  ReleaseSRWLockExclusive(&g->statusLock);
  g->log.Write(state == "failed" ? "error" : "info", "Honest mode: " + g->honestDetail);
  WriteStatus();
}

void Run() {
  g = new State;
  g->dataDir = DataDir();
  g->userDir = DocumentsStellarisDir();
  if (g->dataDir.empty() || g->userDir.empty()) return;
  g->orionDir = Join(g->dataDir, L"orion");
  g->modloaderDir = Join(g->userDir, L"modloader");
  if (!EnsureDir(Join(g->modloaderDir, L"plugins"))) return;
  g->log.SetPath(Join(g->modloaderDir, L"orion.log"));
  g->startedUtc = NowIsoUtc();
  g->log.Write("info", std::string("Orion ") + kOrionVersion + " started. Data folder: " + Narrow(g->dataDir) + "; game data folder: " + Narrow(g->userDir));

  g->buildId = BuildIdOfExe(g->base);
  g->log.Write("info", "Game build " + g->buildId);
  LoadBuildData();
  if (!g->buildKnown) g->log.Write("info", "No verified data for this build: the game runs as vanilla and no plugin loads.");
  g->pluginsOff = EqualNoCase(Narrow(Env(L"ORION_PLUGINS")), "off");
  WriteStatus();  // replaces the last session's list at once, so a start that dies leaves no stale one

  ReadJsonFile(Join(g->dataDir, L"plugin-settings.json"), g->settings, "plugin-settings.json");
  ReadJsonFile(Join(g->dataDir, L"trust.json"), g->trust, "trust.json");

  Json list;
  std::set<std::string> seen;
  std::string lastMod;  // the last mod in the list that has a folder: when the game has mounted it, it has mounted them all
  if (ReadJsonFile(Join(g->userDir, L"dlc_load.json"), list, "dlc_load.json")) {
    const Json* entries = list.Get("enabled_mods");
    for (size_t i = 0; entries && entries->type == Json::Array && i < entries->items.size(); ++i) {
      if (entries->items[i].type != Json::String) continue;
      const std::string& entry = entries->items[i].s;
      const std::string key = StemOf(entry);
      std::wstring folder;
      if (key.empty() || !seen.insert(Lower(key)).second || !ResolveModFolder(entry, folder)) continue;
      lastMod = NormalisePath(Narrow(folder));
      std::string manifest;
      if (!ReadAll(Join(folder, L"modloader.json"), manifest)) continue;  // not a plugin mod
      Process(key, folder, manifest);
    }
  }
  RemoveMarker();
  size_t loaded = 0;
  for (const PluginResult& r : g->results) loaded += r.state == "loaded";
  g->log.Write("info", "Start-up done: " + std::to_string(loaded) + " of " + std::to_string(g->results.size()) + " plugins loaded.");
  if (!g->counted.empty()) {
    g->honestState = "pending";
    g->honestDetail = "Waiting for the game to mount its mods.";
    WriteStatus();
    StartHonest(g->log, BuildFingerprint(g->counted), lastMod, OnHonest);
  }
}

LONG Filter() { return g_inPlugin ? EXCEPTION_CONTINUE_SEARCH : EXCEPTION_EXECUTE_HANDLER; }

void OnInternalError(unsigned long code) {
  if (!g) return;
  char hex[16];
  snprintf(hex, sizeof(hex), "%08lx", code);
  g->log.Write("error", std::string("Orion stopped on an internal error (0x") + hex + "); the game continues.");
  RemoveMarker();
}

}  // namespace
}  // namespace orion

extern "C" __declspec(dllexport) void OrionCoreStart() {
  __try {
    orion::Run();
  } __except (orion::Filter()) {
    orion::OnInternalError(GetExceptionCode());
  }
}
