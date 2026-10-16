// What Orion needs from a mod's modloader.json (D48, schema 1). Artemis reads the rest (settings, features, keywords) for its own pages;
// Orion only checks what decides whether and how a plugin loads, and is no stricter than Artemis about anything else.
#pragma once

#include <string>
#include <vector>

namespace orion {

struct Manifest {
  std::string id;
  std::string version;  // as the author wrote it; part of the honest-mode fingerprint (D22, D53)
  bool cosmetic = false;  // the author's claim that the plugin changes nothing about how the game plays; the user's choice can override it
  int apiVersion = 0;
  std::vector<std::string> files;  // forward slashes, inside the mod folder
  std::vector<std::string> builds;
  std::vector<std::string> dependencies;
  std::vector<std::string> incompatible;
};

// False with a plain-words reason in `error` if the text can't be used.
bool ParseManifest(const std::string& text, Manifest& out, std::string& error);

// A path inside the mod folder, forward slashes, naming a .dll, nothing that climbs out of the folder.
bool IsSafeDllPath(const std::string& path);

// Does a builds pattern like "v4.5.*" cover the game version "v4.5.2"? Same rules as a mod's supported_version.
bool VersionMatches(const std::string& pattern, const std::string& gameVersion);

}  // namespace orion
