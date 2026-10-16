# Asking an AI to make you a plugin

You don't need to write C++. You need to tell Claude Code (or any coding AI that can read files and run a build) clearly
enough what you want, and let it do the rest. This is how.

## Before you start

- Clone this repo and open it in Claude Code. It reads the code itself, so it already knows what a plugin is.
- Tell it to read [DESIGN.md](DESIGN.md) and `sdk/include/orion_plugin.h` first. That header is the whole plugin
  interface; `sdk/test_plugins/` has working examples.
- Needs Visual Studio's C++ tools to build (`orion/build.bat`). If you don't have them, say so and ask it what to install.

## What to put in the request

Four things, in plain words:

1. **What you want to happen in the game.** One thing, not a wish list. "Raise the councillor cap to 8" is good.
   "Make the game better" isn't.
2. **What you already know.** Game version, a mod that does something close, a log line, a screenshot of the
   problem. Anything you'd tell a friend who was helping.
3. **What it must not do.** "Don't change the checksum", "singleplayer only", "no new dependencies".
4. **How you'll know it worked.** "I'll see 8 slots on the councillors screen." The AI can only test what it can check,
   so say what a pass looks like.

## An example

> Read docs/DESIGN.md and sdk/include/orion_plugin.h, then look at the plugins in sdk/test_plugins. I want a plugin
> that changes X. I'm on Stellaris v4.5.2. I don't know where the code for X lives in the game, so tell me what you'd
> need to find out before you start. Keep it as small as you can, and don't touch the manager or Orion itself. Add a
> setting so I can switch it off. When it builds, tell me how to try it.

## Let it ask questions

A good first answer is often a question, or "this needs an address I don't have". That's normal. Plugins that change
engine behaviour need per-build address data, and that data isn't published here (D15). If it can't be done from
script and the header alone, you'll have to find the address yourself or settle for a smaller version.

Plugins that only add script keywords or cosmetic changes are far easier than ones that patch game code. Ask for those
first.

## Keep it honest

- Ask it to say what it **didn't** test. "Builds" is not "works in the game".
- Ask it for the smallest version first. You can add to a working plugin; untangling a big broken one is slow.
- Read what it tells you about the risks. A plugin is a program with your full permissions, and a game patch can break
  it.
- Try it in a sandbox playset first, with a save you don't mind losing.

## When it breaks

Give it the plugin's own log (Documents `Paradox Interactive\Stellaris\modloader\plugins\<mod>.log`) and
`error.log`, and say what you did right before. Don't paraphrase the error; paste it.

## Sharing it

A plugin is a normal mod folder with a `modloader.json` and the DLL. Anyone who installs it has to approve its hash
before it loads. Say what it does in the description, and say which game versions you tried it on.
