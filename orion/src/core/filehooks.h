// A read-only watch on the game's file calls, through the exe's own import table (nothing in the game's code changes), used for one thing: telling a plugin
// when the game starts reading its own scripts, which is when script keywords must be registered (the same moment the first proof of concept used).
#pragma once

#include <string>

#include "util.h"

namespace orion {

using GameStartFn = void (*)(void* user);

// Schedules `callback` to run once, on the calling thread (the game's main thread, since plugins start on it), the first time that thread touches a file under the
// folder of the exe's own "common". The import patches are made on the first call. False if they can't be made. `owner` names the plugin for the log.
bool AddGameStartCallback(Log& log, GameStartFn callback, void* user, const std::string& owner);

}  // namespace orion
