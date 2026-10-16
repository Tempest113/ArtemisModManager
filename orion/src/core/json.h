// A small JSON reader for the files Artemis and mods write. It accepts what Artemis's own reader accepts (comments and trailing commas),
// refuses nesting deeper than 64 levels, and never throws.
#pragma once

#include <cstdint>
#include <string>
#include <utility>
#include <vector>

namespace orion {

struct Json {
  enum Type { Null, Bool, Number, String, Array, Object } type = Null;
  bool b = false;
  double d = 0;
  int64_t i = 0;
  bool isInt = false;  // a number written without fraction or exponent that fits 64 bits
  std::string s;
  std::vector<Json> items;
  std::vector<std::pair<std::string, Json>> members;

  // The member of an object by name, or null (also when this is not an object).
  const Json* Get(const std::string& key) const;
  // The text of a string member, or "" if it is missing or not text.
  std::string Text(const std::string& key) const;
  bool IsString() const { return type == String; }
};

// On failure returns false and says what and where in `error`.
bool ParseJson(const std::string& text, Json& out, std::string& error);

// The text as a JSON string literal, quotes included.
std::string JsonQuote(const std::string& utf8);

}  // namespace orion
