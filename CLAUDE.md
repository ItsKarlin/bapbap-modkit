> 📍 **Living roadmap:** see `ROADMAP.md` (append-only log; visual `roadmap.html`). Keep it updated as you
> build / complete / decide — never delete items, mark them ✅ DONE (YYYY-MM-DD HH:MM:SS). Managed by the /roadmap skill.

# BAPBAP Mods

In-game mod manager and mods for BAPBAP (Steam appid 2226280).

**Read `HANDOFF.md` first** — it has the current state, the traps, and what to do next.

## Layout

```
src/ModManager/     mod manager sources
src/ThirdPerson/    BAPBAP Third Person sources
dist/               built DLLs (what users install)
docs/               settings schema and notes
```

## Build

```
export DOTNET_ROOT=$HOME/.dotnet
$HOME/.dotnet/dotnet build -c Release -o out    # from either src/ folder
```

Then copy the DLL into `<BAPBAP>/Mods/` and relaunch — MelonLoader only loads mods at startup.

## Non-negotiables

- Nothing hardcoded per-mod. Discovery, always.
- Use the game's own API before manipulating its objects.
- Diagnostics must cost nothing when idle.
- Measure before fixing.
