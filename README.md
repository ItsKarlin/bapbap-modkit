# BAPBAP Modkit

An in-game mod manager for [BAPBAP](https://store.steampowered.com/app/2226280/). It lists
whatever MelonLoader loaded, toggles mods on and off, and edits their settings — including mods
it's never heard of.

## Install

**Windows** — in PowerShell:

```powershell
irm https://raw.githubusercontent.com/ItsKarlin/bapbap-modkit/main/install/install.ps1 | iex
```

**Linux / Steam Deck**:

```bash
curl -fsSL https://raw.githubusercontent.com/ItsKarlin/bapbap-modkit/main/install/install.sh | bash
```

It needs MelonLoader and will offer to install it if you don't have it. Everything it downloads
is sha256-checked against [`dist/manifest.json`](dist/manifest.json). By hand: drop
[`dist/BAPBAPModManager.dll`](dist/) into `BAPBAP/Mods/`.

On Linux, set this Steam launch option or Proton ignores MelonLoader with no error:

```
WINEDLLOVERRIDES="version=n,b" %command%
```

## Using it

`F5`, or the MODS button in the nav bar. Also under Settings → Mods.

Mods are grouped by who they affect:

| | |
|---|---|
| **Host** | Changes the match for everyone when you host. Locked mid-match. |
| **Client** | Your screen only. Always safe. |
| **Unrecognised** | No scope info — assume host until you know better. |

**Config** opens a mod's settings. Toggling a mod takes effect on the next launch, since
MelonLoader can't unload an assembly mid-session.

Host mods change the game for everyone in the lobby, and guests need nothing installed to be
affected — so keep them to private lobbies with people who agreed to it.

## Writing mods

Your mod appears in the manager with no work from you. If it writes
`UserData/<YourAssembly>.ini` as plain `key=value` lines, its settings become editable in-game
automatically — on/off switches, sliders for numbers, and rebindable keys.

The one case inference can't handle is a setting that's one of a few fixed words, like
`Quality=Aggressive`. Add an `options` list for it and the manager renders a tap-to-cycle picker
instead of read-only text. Same file also gets you proper labels, slider ranges, and host/client
scope so the manager knows what's safe to change mid-match.

**→ [docs/settings-schema.md](docs/settings-schema.md)**

## Building

Needs the .NET SDK and a BAPBAP install that's been launched once with MelonLoader.

```bash
cd src/ModManager
dotnet build -c Release -o out
cp out/*.dll "<BAPBAP>/Mods/"
```

Outside the default Linux Steam path, pass `-p:GameDir="C:\...\common\BAPBAP"`.

## Notes

- The MODS button is dimmer than the game's own tabs, and the previously selected tab keeps its
  marker while the page is open. Both cosmetic.
- Occasional frame spike with the full BAPHub mod set installed.

## Credits

Hidden Dev Arguments, Pool Randomizer, HP Numbers, Arena Random Chars, Asset Dumper and More
Custom Settings are [BAPHub's](https://github.com/Sonic0810/BAPBAPLauncher) work — this repo
doesn't redistribute them, it just detects and configures what you install yourself.

Not affiliated with the developers of BAPBAP. MIT, see [LICENSE](LICENSE).
