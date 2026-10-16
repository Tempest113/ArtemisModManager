#include "json.h"

#include <cstdio>
#include <cstdlib>

namespace orion {

const Json* Json::Get(const std::string& key) const {
  if (type != Object) return nullptr;
  for (const auto& m : members)
    if (m.first == key) return &m.second;
  return nullptr;
}

std::string Json::Text(const std::string& key) const {
  const Json* v = Get(key);
  return v && v->type == String ? v->s : std::string();
}

namespace {

constexpr int kMaxDepth = 64;

class Parser {
 public:
  explicit Parser(const std::string& t) : t_(t) {}

  bool Run(Json& out, std::string& error) {
    if (t_.compare(0, 3, "\xEF\xBB\xBF") == 0) p_ = 3;
    if (!Value(out, 0)) { error = error_ + " (at character " + std::to_string(p_) + ")"; return false; }
    Skip();
    if (p_ != t_.size()) { error = "unexpected text after the end (at character " + std::to_string(p_) + ")"; return false; }
    return true;
  }

 private:
  const std::string& t_;
  size_t p_ = 0;
  std::string error_;

  bool Fail(const char* what) { error_ = what; return false; }

  void Skip() {
    while (p_ < t_.size()) {
      const char c = t_[p_];
      if (c == ' ' || c == '\t' || c == '\r' || c == '\n') ++p_;
      else if (c == '/' && p_ + 1 < t_.size() && t_[p_ + 1] == '/') { while (p_ < t_.size() && t_[p_] != '\n') ++p_; }
      else if (c == '/' && p_ + 1 < t_.size() && t_[p_ + 1] == '*') {
        const size_t end = t_.find("*/", p_ + 2);
        p_ = end == std::string::npos ? t_.size() : end + 2;
      } else break;
    }
  }

  bool Literal(const char* word) {
    size_t n = 0;
    while (word[n]) ++n;
    if (t_.compare(p_, n, word) != 0) return false;
    p_ += n;
    return true;
  }

  static void Utf8(std::string& s, uint32_t cp) {
    if (cp < 0x80) s += static_cast<char>(cp);
    else if (cp < 0x800) { s += static_cast<char>(0xC0 | (cp >> 6)); s += static_cast<char>(0x80 | (cp & 0x3F)); }
    else if (cp < 0x10000) { s += static_cast<char>(0xE0 | (cp >> 12)); s += static_cast<char>(0x80 | ((cp >> 6) & 0x3F)); s += static_cast<char>(0x80 | (cp & 0x3F)); }
    else { s += static_cast<char>(0xF0 | (cp >> 18)); s += static_cast<char>(0x80 | ((cp >> 12) & 0x3F)); s += static_cast<char>(0x80 | ((cp >> 6) & 0x3F)); s += static_cast<char>(0x80 | (cp & 0x3F)); }
  }

  bool Hex4(uint32_t& v) {
    if (p_ + 4 > t_.size()) return false;
    v = 0;
    for (int k = 0; k < 4; ++k) {
      const char c = t_[p_++];
      v <<= 4;
      if (c >= '0' && c <= '9') v |= static_cast<uint32_t>(c - '0');
      else if (c >= 'a' && c <= 'f') v |= static_cast<uint32_t>(c - 'a' + 10);
      else if (c >= 'A' && c <= 'F') v |= static_cast<uint32_t>(c - 'A' + 10);
      else return false;
    }
    return true;
  }

  bool String(std::string& out) {
    ++p_;  // opening quote
    out.clear();
    while (p_ < t_.size()) {
      const char c = t_[p_++];
      if (c == '"') return true;
      if (c != '\\') { out += c; continue; }
      if (p_ >= t_.size()) break;
      const char e = t_[p_++];
      switch (e) {
        case '"': out += '"'; break;
        case '\\': out += '\\'; break;
        case '/': out += '/'; break;
        case 'b': out += '\b'; break;
        case 'f': out += '\f'; break;
        case 'n': out += '\n'; break;
        case 'r': out += '\r'; break;
        case 't': out += '\t'; break;
        case 'u': {
          uint32_t cp;
          if (!Hex4(cp)) return Fail("bad \\u escape");
          if (cp >= 0xD800 && cp < 0xDC00 && p_ + 1 < t_.size() && t_[p_] == '\\' && t_[p_ + 1] == 'u') {
            p_ += 2;
            uint32_t low;
            if (!Hex4(low) || low < 0xDC00 || low > 0xDFFF) return Fail("bad surrogate pair");
            cp = 0x10000 + ((cp - 0xD800) << 10) + (low - 0xDC00);
          }
          Utf8(out, cp);
          break;
        }
        default: return Fail("bad escape in a string");
      }
    }
    return Fail("a string is not closed");
  }

  bool Number(Json& out) {
    const size_t start = p_;
    if (t_[p_] == '-') ++p_;
    bool plain = true;
    while (p_ < t_.size() && t_[p_] >= '0' && t_[p_] <= '9') ++p_;
    if (p_ < t_.size() && t_[p_] == '.') { plain = false; ++p_; while (p_ < t_.size() && t_[p_] >= '0' && t_[p_] <= '9') ++p_; }
    if (p_ < t_.size() && (t_[p_] == 'e' || t_[p_] == 'E')) {
      plain = false;
      ++p_;
      if (p_ < t_.size() && (t_[p_] == '+' || t_[p_] == '-')) ++p_;
      while (p_ < t_.size() && t_[p_] >= '0' && t_[p_] <= '9') ++p_;
    }
    const std::string text = t_.substr(start, p_ - start);
    if (text.empty() || text == "-") return Fail("bad number");
    char* end = nullptr;
    out.type = Json::Number;
    out.d = strtod(text.c_str(), &end);
    if (end != text.c_str() + text.size()) return Fail("bad number");
    if (plain && text.size() <= 18) { out.isInt = true; out.i = _strtoi64(text.c_str(), nullptr, 10); }
    return true;
  }

  bool Value(Json& out, int depth) {
    if (depth > kMaxDepth) return Fail("nested too deeply");
    Skip();
    if (p_ >= t_.size()) return Fail("unexpected end");
    const char c = t_[p_];
    if (c == '{') return Object(out, depth);
    if (c == '[') return Array(out, depth);
    if (c == '"') { out.type = Json::String; return String(out.s); }
    if (c == 't') { out.type = Json::Bool; out.b = true; return Literal("true") || Fail("bad value"); }
    if (c == 'f') { out.type = Json::Bool; out.b = false; return Literal("false") || Fail("bad value"); }
    if (c == 'n') { out.type = Json::Null; return Literal("null") || Fail("bad value"); }
    if (c == '-' || (c >= '0' && c <= '9')) return Number(out);
    return Fail("unexpected character");
  }

  bool Array(Json& out, int depth) {
    out.type = Json::Array;
    ++p_;
    for (;;) {
      Skip();
      if (p_ >= t_.size()) return Fail("a list is not closed");
      if (t_[p_] == ']') { ++p_; return true; }
      out.items.emplace_back();
      if (!Value(out.items.back(), depth + 1)) return false;
      Skip();
      if (p_ < t_.size() && t_[p_] == ',') ++p_;
      else if (p_ < t_.size() && t_[p_] != ']') return Fail("expected a comma or ]");
    }
  }

  bool Object(Json& out, int depth) {
    out.type = Json::Object;
    ++p_;
    for (;;) {
      Skip();
      if (p_ >= t_.size()) return Fail("an object is not closed");
      if (t_[p_] == '}') { ++p_; return true; }
      if (t_[p_] != '"') return Fail("expected a name in quotes");
      std::string key;
      if (!String(key)) return false;
      Skip();
      if (p_ >= t_.size() || t_[p_] != ':') return Fail("expected a colon");
      ++p_;
      out.members.emplace_back(std::move(key), Json());
      if (!Value(out.members.back().second, depth + 1)) return false;
      Skip();
      if (p_ < t_.size() && t_[p_] == ',') ++p_;
      else if (p_ < t_.size() && t_[p_] != '}') return Fail("expected a comma or }");
    }
  }
};

}  // namespace

bool ParseJson(const std::string& text, Json& out, std::string& error) {
  out = Json();
  return Parser(text).Run(out, error);
}

std::string JsonQuote(const std::string& s) {
  std::string out = "\"";
  for (const unsigned char c : s) {
    switch (c) {
      case '"': out += "\\\""; break;
      case '\\': out += "\\\\"; break;
      case '\n': out += "\\n"; break;
      case '\r': out += "\\r"; break;
      case '\t': out += "\\t"; break;
      default:
        if (c < 0x20) { char buf[8]; snprintf(buf, sizeof(buf), "\\u%04x", c); out += buf; }
        else out += static_cast<char>(c);
    }
  }
  return out + "\"";
}

}  // namespace orion
