// Honest mode (D22, D53): a plugin that changes how the game plays has to show up in the game's own checksum, so multiplayer mismatches are caught and
// achievements turn off exactly as they do for a script mod. Orion does it by adding one small virtual file, common/modloader/manifest.txt, to the game's
// file system through the PhysFS functions the game exports, after the game has mounted its mods. Nothing here hooks the game or spoofs a checksum.
#pragma once

#include <string>
#include <vector>

#include "util.h"

namespace orion {

struct HonestFile {
  std::string path, sha256;
};

struct HonestPlugin {
  std::string id, version;
  std::vector<HonestFile> files;
};

// Looks up the exports of the game's file system (once). False, with the reason, when one is missing; then no plugin that counts can load.
bool HonestAvailable(std::string& why);

// The text of common/modloader/manifest.txt: sorted by plugin id, so the same plugins give the same bytes on every machine.
std::string BuildFingerprint(std::vector<HonestPlugin> plugins);

struct ZipEntry {
  std::string name, data;  // a name ending in '/' is a folder
};

// A zip with stored (uncompressed) entries, which is all the game's zip reader needs.
std::string MakeZip(const std::vector<ZipEntry>& entries);

// Makes a path comparable with the game's search path: lower case, forward slashes, no trailing slash.
std::string NormalisePath(std::string path);

// Called from the watcher thread: "mounted" or "failed", and one plain sentence.
using HonestReport = void (*)(const std::string& state, const std::string& detail);

// Starts a thread that waits for the game to mount `waitFor` (the last mod in its list; normalised, "" if there is none), then mounts the fingerprint, checks
// that the game can see it, and puts it back if the game drops it. After ORION_HONEST_WAIT_MS (default 8000) it mounts anyway.
void StartHonest(Log& log, std::string fingerprint, std::string waitFor, HonestReport report);

}  // namespace orion
