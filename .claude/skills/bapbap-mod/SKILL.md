---
name: bapbap-mod
description: Write, build, debug and publish a mod for BAPBAP (Steam appid 2226280) that works with the BAPBAP Modkit manager. Use when creating a new BAPBAP mod, adding settings to one, fixing a mod that will not load or renders nothing, or publishing a mod to the in-game downloader. Covers MelonLoader/IL2CPP setup, the game's UI traps, and the catalog publish flow.
---

# Writing a BAPBAP mod

Everything here was learned by measurement against the real game. The "do not" items are not
style preferences — each cost hours and several failed attempts.

Repo: `~/projects/bapbap-mods` (public as `ItsKarlin/bapbap-modkit`).

## First: work out what the mod actually is

**Do not start writing code from a one-line idea.** Two passes, in this order: pin the spec, then
push on the design. Pass 1 keeps you from building the wrong thing; pass 2 is what makes it worth
playing.

Keep both sets of answers — they become the mod's summary, its settings list, and its catalog
entry. This is the documentation.

### Pass 1 — Spec: what it is (ask every time)

These five come up for every mod, because each one decides something structural. Where the idea
already answers one, **state your assumption instead of asking** — do not read a checklist back at
someone who just told you the answer.

- **Who it affects.** Only this player, or everyone in the lobby? The most important answer:
  it decides `host` vs `client`, whether it locks mid-match, and whether guests need anything.
- **When it runs.** Always on, a keybind, only in a match, only in the lobby, only while hosting?
- **What it reads or changes.** An existing game value, a new overlay, a networked object?
  Touching something Mirror replicates is a completely different job from drawing on your own canvas.
- **What is configurable**, and what is fixed. Each configurable thing costs an ini key and a
  settings entry. Too many is worse than too few.
- **What happens at the edges** — mid-match join, host migration, a missing value, the mod being
  toggled off while a match is running.

### Pass 2 — Design: whether it is any good (never the same twice)

Pass 1 tells you what to build. It says nothing about whether it is fun. **These questions must be
invented fresh for the specific mod** — a damage-number mod and a round mutator have almost nothing
in common to ask about.

Useful things to push on, when they apply:

- **What is the moment?** The specific instant this is meant to create — the laugh, the panic, the
  "watch this". If nobody can name it, the mod probably has no point yet.
- **How does it change how people play**, not just what they see? A mod that changes numbers but not
  decisions is usually a settings toggle, not a mod.
- **When does it stop being fun?** Almost everything is funny for two rounds. What is the tenth
  round like? Does it need a cap, a cooldown, escalation, or randomness to stay alive?
- **Is there counterplay**, or does it just happen to you? Being on the receiving end with no
  response is where host-side mods turn sour.
- **Does it create stories** worth retelling afterwards?
- **What does it collide with?** Other mods, augments, specific maps or characters. This game has
  16 built-in `GM_*` modifiers and a dozen community mods; combinations are where things break.
- **What is the cheap version?** Often 20% of the idea gives 90% of the fun and can be tested
  tonight. Find it and propose it.

How different that looks in practice:

> *"damage numbers"* — Presentation. Does seeing exact numbers make people play differently or just
> feel informed? Do stacked hits read as one big number or a stream? Is a big hit worth celebrating
> visually, or is that noise after ten minutes?

> *"everyone is giant"* — Match rules. What actually changes — reach, hitboxes, movement, or only
> looks? Does everyone being giant just cancel out into normal, and is scaling *one* player the
> better joke? Does it get funnier or duller by round five?

> *"randomise the map each round"* — Pacing. Is the surprise the point, or is it the not having to
> agree on a map? Should players see it coming, or is the reveal the moment? Does repetition ruin it?

Notice none of the three share a question, and none of them are the pass 1 questions.

### Then agree before building

Parrot back in a few lines: scope, trigger, the settings you intend to expose, and every decision
you made yourself. Cheap to correct now, expensive later.

Then name it: the `MelonInfo` name (stable forever — the manager matches on it), the assembly name
(the ini and settings files are named after it), and a reverse-DNS catalog id.

## The environment, verified

- Unity **2022.3.38f1**, **IL2CPP**, Windows build (under Proton on Linux)
- **MelonLoader `0.7.2-ci.2388`** — BAPHub's pin, *not* the public 0.7.3. Mods built against
  0.7.3 may misbehave alongside BAPHub mods.
- Mods run on a real **.NET 6** runtime. `HttpClient`, `Task`, `System.Security.Cryptography`
  and **Newtonsoft.Json** (shipped in `MelonLoader/net6/`) all work directly. You do not need
  `UnityWebRequest` or coroutines for network or JSON work.
- **Mirror + FizzySteamworks**, host-authoritative. Host-side mods change the match for everyone;
  guests need nothing installed.
- On Linux the game needs this Steam launch option or **MelonLoader is ignored silently**:
  `WINEDLLOVERRIDES="version=n,b" %command%`

## Project setup

Target `net6.0`, reference the loader and the generated Il2Cpp assemblies. `GameDir` must point
at an install that has been launched **once** with MelonLoader — `Il2CppAssemblies/` is generated
on first run.

```xml
<PropertyGroup>
  <TargetFramework>net6.0</TargetFramework>
  <AssemblyName>MyMod</AssemblyName>
  <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  <GameDir Condition="'$(GameDir)'==''">$(HOME)/.local/share/Steam/steamapps/common/BAPBAP</GameDir>
  <MLDir>$(GameDir)/MelonLoader/net6</MLDir>
  <Il2CppDir>$(GameDir)/MelonLoader/Il2CppAssemblies</Il2CppDir>
</PropertyGroup>
```

Reference from `$(MLDir)`: `MelonLoader.dll`, `0Harmony.dll`, `Il2CppInterop.Runtime.dll`,
`Newtonsoft.Json.dll`. From `$(Il2CppDir)`: `Il2Cppmscorlib.dll`, `UnityEngine*.dll`,
`Assembly-CSharp.dll`, `Unity.TextMeshPro.dll`, `Il2CppMirror.dll`. All with `<Private>false</Private>`.

Copy the existing `src/ModManager/BAPBAPModManager.csproj` rather than writing one from scratch.
If it uses `EnableDefaultCompileItems=false`, **every new .cs file must be added explicitly** or
it silently is not compiled.

```csharp
[assembly: MelonInfo(typeof(MyNamespace.MyMod), "BAPBAP My Mod", "1.0.0", "YourName")]
[assembly: MelonGame(null, "BAPBAP")]
```

The `MelonInfo` name is the mod's identity everywhere — the manager matches catalog entries
against it. Keep it stable across versions.

```bash
export DOTNET_ROOT=$HOME/.dotnet
dotnet build -c Release -o out && cp out/*.dll "<BAPBAP>/Mods/"
```

**Gate the copy on the build succeeding.** A `grep ... && cp` chain has deployed stale DLLs here
more than once because grep succeeded while printing errors. MelonLoader only loads at startup —
always restart.

## DO NOT — each of these was measured

1. **Do not `SetActive(false)` the game's UI panels.** They hide by **fading** (`CanvasGroup.alpha
   = 0`), not deactivating. Deactivating leaves their controller believing its tab is selected and
   the window renders blank. Fade instead, and give the alpha back.
2. **Do not use `activeInHierarchy` to test whether one of the game's panels is showing.** Faded
   panels stay active. Read `CanvasGroup.alpha`. This has caused three separate bugs.
3. **Do not call the game's `ClosePage()`** to hide the lobby page — the tab controller desyncs and
   the lobby goes black. Cover it instead.
4. **Do not clone a game panel expecting it to be visible.** The clone inherits alpha 0 *and* the
   `UIAlphaFade` drivers that reset it. Build panels fresh.
5. **Do not create `TextMeshProUGUI` on a bare GameObject** — no font asset, renders nothing.
   `Instantiate` one of the game's labels to inherit font and material, then clear its children.
6. **Do not try to show the OS cursor during a match.** The game re-hides `Cursor.visible` every
   frame. Own a UI element on your own canvas instead (sortingOrder ~30000).
7. **Do not call `Resources.FindObjectsOfTypeAll` + `TryCast` on a timer.** In IL2CPP every call
   allocates an array and every cast allocates a wrapper — this is a stutter machine. Cache
   references; retry only when null, on a long cooldown.
8. **Values the game re-asserts every frame must be written in `LateUpdate`**, or its `Update`
   overwrites yours.
9. `RectOffset` has no 4-arg constructor in Il2Cpp. Use `var p = new RectOffset(); p.left = …`.

## Diagnostics

**Idle cost must be zero.** Key-triggered only — a probe that scanned every second once became the
very stutter it was written to find.

**Measure before fixing.** Every hard bug in this project fell in one launch once instrumented,
after multiple failed guesses. If something resists two attempts, stop and dump state.

The manager has `F6` (catalog network probe) and `F7` (settings window state dump) as examples.

Logs: `<BAPBAP>/MelonLoader/Latest.log`, previous runs in `MelonLoader/Logs/`. Every mod prefixes
lines with `[Mod_Name]`, so grep that.

## Settings — free, if you follow the convention

Write `UserData/<YourAssemblyName>.ini` as plain `key=value`. The manager discovers it, infers
types (`True`/`False` → toggle, numeric → slider, else read-only text) and makes them editable
in-game with **no work from you**.

For real labels, ranges and scope, ship `UserData/<YourAssemblyName>.settings.json` — see
`docs/settings-schema.md`. Its `scope` field is per-setting: `host` settings are locked while a
match is running.

**Migrate your own ini.** If you only write it when absent, users who installed an earlier build
never get keys you add later, and those settings stay invisible in the manager. Track which known
keys you saw while parsing and rewrite once if any were missing.

## Scope: host or client

- **client** — affects only that machine. Always safe to toggle.
- **host** — read by whoever is hosting, so it changes the match for everyone. The manager locks
  these mid-match and fails safe when match state is unknown.

Mark anything that changes gameplay for others as `host`. Getting this wrong is the one way a mod
ruins someone else's game.

Scope is **not** in the DLL — the manager keeps a small overlay in `ModCatalog.Known`. Unlisted
mods still appear, flagged "Unrecognised". Catalog entries can declare `scope` directly.

## Publishing to the in-game downloader

From the modkit repo:

```bash
tools/add-mod.sh out/MyMod.dll \
  --id yourname.bapbap.my-mod \
  --name "BAPBAP My Mod" \
  --version 1.0.0 \
  --summary "One line." --scope client --tags ui,qol
git add dist/ catalog/ && git commit -m "Publish My Mod 1.0.0" && git push
```

`--name` **must** match your `MelonInfo` name or the manager cannot tell the mod is installed.

That is the whole publish step. The catalog is fetched live, so it appears in everyone's browse
tab — no manager update, nothing for users to do. Re-run with a new `--version` to ship an update;
they get an UPDATE button.

Never add a mod to `dist/manifest.json` — that file is strictly the installer payload (the manager
itself). Mods belong in the catalog.

## When a mod will not load

1. Is it in `Mods/`, and was the game restarted?
2. On Linux, is `WINEDLLOVERRIDES="version=n,b" %command%` set? Without it, nothing loads and
   nothing says why.
3. Does `Latest.log` list the assembly? If loaded but silent, the failure is in `OnInitializeMelon`
   — wrap it and log.
4. `TypeLoadException` on `UnityEngine.CoreModule` in Harmony output is normal noise from another
   mod, not your bug.
