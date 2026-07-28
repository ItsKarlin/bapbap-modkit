# BAPBAP Mods

An in-game mod manager for [BAPBAP](https://store.steampowered.com/app/2226280/), plus a
third-person camera mod.

The point of the manager is that it doesn't know about your mods in advance. It reads whatever
MelonLoader actually loaded, so any mod shows up in the list, can be turned on and off, and — if
it writes a normal config file — has its settings editable from inside the game. That includes
mods written after this one.

## Install

Windows, in PowerShell:

```powershell
irm https://raw.githubusercontent.com/ItsKarlin/bapbap-mods/main/install/install.ps1 | iex
```

Linux or Steam Deck:

```bash
curl -fsSL https://raw.githubusercontent.com/ItsKarlin/bapbap-mods/main/install/install.sh | bash
```

It finds your BAPBAP folder, checks each DLL against the sha256 in
[`dist/manifest.json`](dist/manifest.json), and won't install anything that doesn't match. If
you'd rather do it by hand, the DLLs are in [`dist/`](dist/) and they go in `BAPBAP/Mods/`.

If you don't have MelonLoader, the installer offers to fetch it — `0.7.2-ci.2388`, verified by
sha256 like everything else. That's deliberately not the public 0.7.3 release: it's the CI build
the BAPHub launcher pins, and BAPHub's mods are built against it. If you already have a different
version the installer leaves it alone and just says so.

On Linux you also need this launch option in Steam, or Proton ignores the loader without telling
you anything:

```
WINEDLLOVERRIDES="version=n,b" %command%
```

MelonLoader only loads mods at startup, so restart the game after installing or toggling
anything.

## What you get

**BAPBAP Mods** is the manager. Press `F5` in the menus or click MODS in the top nav bar. There's
also a Mods tab in the settings menu if you prefer it there.

**BAPBAP Third Person** is a third-person camera on `F1`. It hides the crosshair while active and
gives you a mouse pointer for card picks and menus, since the game normally hides the cursor
during a match. FOV, sensitivity, camera height and pitch are configurable and reload live.

Both are client-side. Neither changes anything for other people in your lobby.

## Using it

The mod list is grouped by how far a mod's effect reaches:

- **Host** mods change the match for everyone when you're hosting. They're locked while a match
  is running.
- **Client** mods only affect your screen, so they're always safe to toggle.
- **Unrecognised** means there's no scope metadata for it. It's still listed and still
  toggleable, but assume it's host-side until you know better.

Config on any mod opens its settings. Disabling one moves its DLL to `Mods/disabled/`, which
takes effect next launch — MelonLoader can't unload an assembly mid-session.

### About hosting

BAPBAP is host-authoritative (Mirror over FizzySteamworks), so host-side mods change the match for
everyone in the lobby and guests don't need to install anything. They can join from a stock Steam
copy. That's genuinely useful, and it's also the reason to keep this to private lobbies with
people who know what they're getting into. Don't take host-side mods into public matchmaking.

There's no anti-cheat binary, but Unity Analytics and GameAnalytics are both live, so modded
sessions are visible server-side. Nothing here tries to hide that.

## If you write mods

Your mod shows up in the manager on its own; there's nothing to register. If it writes
`UserData/<YourAssembly>.ini` as plain `key=value` lines, the manager infers types and makes those
settings editable in-game without you doing anything.

If you want real labels, slider ranges, descriptions and host/client scope, ship a
`UserData/<YourAssembly>.settings.json` next to it. The format is in
[docs/settings-schema.md](docs/settings-schema.md).

## Building

You need the .NET SDK and a BAPBAP install that's been launched at least once with MelonLoader —
the projects reference `MelonLoader/net6/` and the `Il2CppAssemblies/` folder MelonLoader
generates on first run.

```bash
cd src/ModManager          # or src/ThirdPerson
dotnet build -c Release -o out
cp out/*.dll "<BAPBAP>/Mods/"
```

`GameDir` defaults to the usual Linux Steam path. Anywhere else, override it:

```bash
dotnet build -c Release -o out -p:GameDir="C:\Program Files (x86)\Steam\steamapps\common\BAPBAP"
```

`src/ModManager` and `src/ThirdPerson` are the sources, `dist/` holds the prebuilt DLLs and the
manifest the installer reads, `docs/` has the settings schema.

## Known issues

Windows hasn't been verified. All of this was built and tested on Linux under Proton
Experimental. The code doesn't do anything platform-specific and the installer handles Windows
paths, but nobody has actually run it on Windows yet.

If you installed Third Person before the camera settings existed, they won't show up in the
manager. The mod only writes its ini when the file is missing, so an older ini never gains the
newer keys. Delete `UserData/BAPBAPThirdPerson.ini` and relaunch to regenerate it, which resets
your values.

The MODS button is dimmer than the game's own nav tabs. The game's tab highlight lives in
internal SDF shader state with no handle to reach it — dumping two lit tabs side by side showed
every readable property identical. It's cosmetic and it's staying that way.

Whichever tab you had selected keeps its blue marker while the MODS page is open. Closing the
game's own page to clear it turns the lobby black, so the page just covers it instead.

There's an occasional frame spike, roughly one every couple of minutes, with the full BAPHub set
installed. It hasn't been pinned to a specific mod.

## Credits

The other BAPBAP mods — Hidden Dev Arguments, Pool Randomizer, HP Numbers, Arena Random Chars,
Asset Dumper, More Custom Settings — are BAPHub's work. This repo doesn't redistribute any of
them; the manager just discovers and configures whatever you installed yourself.

Third Person is a clean-room implementation written against the game's own API.

Not affiliated with or endorsed by the developers of BAPBAP.

MIT licensed, see [LICENSE](LICENSE).
