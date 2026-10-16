#include "manifest.h"

#include "json.h"
#include "util.h"

namespace orion {

namespace {

bool ValidId(const std::string& id) {
  if (id.empty() || id.size() > 64) return false;
  for (size_t i = 0; i < id.size(); ++i) {
    const char c = id[i];
    const bool alnum = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
    if (!alnum && (i == 0 || (c != '.' && c != '_' && c != '-'))) return false;
  }
  return true;
}

std::string Trim(const std::string& s) {
  size_t a = 0, b = s.size();
  while (a < b && (s[a] == ' ' || s[a] == '\t' || s[a] == '\r' || s[a] == '\n')) ++a;
  while (b > a && (s[b - 1] == ' ' || s[b - 1] == '\t' || s[b - 1] == '\r' || s[b - 1] == '\n')) --b;
  return s.substr(a, b - a);
}

// A list of text; false if the member exists but is not a list of text.
bool Strings(const Json& root, const char* name, std::vector<std::string>& out) {
  const Json* v = root.Get(name);
  if (!v) return true;
  if (v->type != Json::Array) return false;
  for (const Json& x : v->items) {
    if (x.type != Json::String) return false;
    if (std::string t = Trim(x.s); !t.empty()) out.push_back(std::move(t));
  }
  return true;
}

std::vector<std::string> Parts(const std::string& version) {
  std::string v = Trim(version);
  while (!v.empty() && v.front() == '"') v.erase(v.begin());
  while (!v.empty() && v.back() == '"') v.pop_back();
  while (!v.empty() && (v.front() == 'v' || v.front() == 'V')) v.erase(v.begin());
  std::vector<std::string> parts;
  if (v.empty()) return parts;
  size_t start = 0;
  for (;;) {
    const size_t dot = v.find('.', start);
    parts.push_back(v.substr(start, dot == std::string::npos ? std::string::npos : dot - start));
    if (dot == std::string::npos) break;
    start = dot + 1;
  }
  return parts;
}

}  // namespace

bool IsSafeDllPath(const std::string& path) {
  if (path.size() < 5 || !EqualNoCase(path.substr(path.size() - 4), ".dll")) return false;
  if (path[0] == '/' || path.find(':') != std::string::npos) return false;
  size_t start = 0;
  for (;;) {
    const size_t slash = path.find('/', start);
    const std::string part = path.substr(start, slash == std::string::npos ? std::string::npos : slash - start);
    if (part.empty() || part == "." || part == "..") return false;
    if (slash == std::string::npos) return true;
    start = slash + 1;
  }
}

bool VersionMatches(const std::string& pattern, const std::string& gameVersion) {
  const auto want = Parts(pattern), have = Parts(gameVersion);
  if (want.empty() || have.empty()) return false;
  for (size_t i = 0; i < want.size(); ++i) {
    if (want[i] == "*") continue;
    if (i >= have.size() || !EqualNoCase(want[i], have[i])) return false;
  }
  return true;
}

bool ParseManifest(const std::string& text, Manifest& out, std::string& error) {
  Json root;
  std::string why;
  if (!ParseJson(text, root, why)) { error = "modloader.json isn't valid JSON: " + why + "."; return false; }
  if (root.type != Json::Object) { error = "modloader.json must be a JSON object."; return false; }
  out = Manifest();
  if (const Json* schema = root.Get("schema"); schema && !(schema->type == Json::Number && schema->isInt && schema->i == 1)) {
    error = "modloader.json has a schema this Orion doesn't know.";
    return false;
  }
  out.id = Trim(root.Text("id"));
  out.version = Trim(root.Text("version"));
  if (const Json* c = root.Get("cosmetic")) out.cosmetic = c->type == Json::Bool && c->b;
  if (!ValidId(out.id)) { error = "\"id\" is missing or not 1 to 64 letters, digits, '.', '_' or '-'."; return false; }
  const Json* api = root.Get("apiVersion");
  if (!api || api->type != Json::Number || !api->isInt || api->i < 1 || api->i > 1000000) { error = "\"apiVersion\" must be a whole number, 1 or more."; return false; }
  out.apiVersion = static_cast<int>(api->i);
  if (!Strings(root, "files", out.files) || !Strings(root, "builds", out.builds) || !Strings(root, "dependencies", out.dependencies) ||
      !Strings(root, "incompatible", out.incompatible)) {
    error = "\"files\", \"builds\", \"dependencies\" and \"incompatible\" must be lists of text.";
    return false;
  }
  if (out.files.empty()) { error = "\"files\" must list at least one DLL."; return false; }
  for (std::string& f : out.files) {
    for (char& c : f)
      if (c == '\\') c = '/';
    if (!IsSafeDllPath(f)) { error = "\"" + f + "\" isn't a DLL inside the mod's own folder."; return false; }
  }
  return true;
}

}  // namespace orion
