# BAPBAP Mods — Roadmap (living log)

END GOAL: A public, self-serve BAPBAP mod platform — an in-game manager that discovers any installed mod, edits its settings natively, and downloads/uninstalls mods straight from GitHub, plus our own mods shipped alongside it.

> **This is an APPEND-ONLY log.** Never delete items. When something is completed, mark it
> ✅ DONE (YYYY-MM-DD HH:MM:SS) and KEEP it — this is a record to look back on, not a to-do that gets pruned.
> The visual view is roadmap.html (regenerate with the /roadmap skill after updates).

## Hard principles / decisions (do NOT violate)
- **Nothing hardcoded per-mod** — the manager discovers mods and their settings; a mod nobody wrote support for must still appear and be configurable.
- **Private lobbies with consenting friends only** — host-side mods change the match for everyone; never public matchmaking.
- **Friends install nothing** — host-only mods run on the host; guests join from a stock Steam copy.
- **Use the game's own API before manipulating its objects** — every time we reached past it (ClosePage, panel activation, tab visuals) something broke.
- **Diagnostics must be idle-cost-zero** — the first probe scanned every second and became a stutter source itself.
- **Measure before fixing** — the cursor bug took 4 failed guesses, the tab highlight 10+; both fell in one launch once instrumented.
- **No redistribution of others' unlicensed work** — our Third Person mod is a clean-room build against the game's API.

## ACT 0 · Recon & foundation (COMPLETE)
- ✅ **Identify the game stack** — Unity 2022.3.38f1, IL2CPP, Mirror + FizzySteamworks (host-authoritative), no anti-cheat. DONE (2026-07-27 16:52:00)
- ✅ **MelonLoader working under Proton** — pinned 0.7.2-ci.2388, `WINEDLLOVERRIDES="version=n,b" %command%`. DONE (2026-07-27 17:58:00)
- ✅ **Static IL2CPP dump** — Cpp2IL, 82 MB of readable C#, no install required. DONE (2026-07-27 17:30:00)
- ✅ **Map the BAPHub ecosystem** — 12 mods, launcher-only distribution, invisible to mod portals. DONE (2026-07-27 18:00:00)
- ✅ **Install the BAPHub catalog mods** — 7 installed, sha256-verified against the manifest. DONE (2026-07-28 02:00:00)

## ACT 1 · Mod manager (COMPLETE)
- ✅ **In-game MODS page** — nav-bar button + F5, full-bleed page with fade. DONE (2026-07-27 19:49:00)
- ✅ **Discovery-based catalog** — enumerates loaded mods via MelonInfo (name/version/author); no hardcoded list. DONE (2026-07-28 03:20:00)
- ✅ **Host / Client / Unrecognised sections** — scope metadata overlay; unknown mods shown and toggleable, flagged. DONE (2026-07-28 03:25:00)
- ✅ **Match-state lock** — host-only mods cannot be toggled mid-match; fails safe when state is unknown. DONE (2026-07-27 19:45:00)
- ✅ **Enable/disable mods** — DLLs parked in `Mods/disabled/`, effective next launch. DONE (2026-07-27 19:45:00)
- ✅ **Game-matched palette** — deep navy surfaces, blue accents, yellow headers. DONE (2026-07-28 03:50:00)
- ✅ **Scrollable page** — list keeps working as mods are added. DONE (2026-07-28 03:50:00)

## ACT 2 · Settings system (COMPLETE)
- ✅ **Generic settings discovery** — parses any mod's `UserData/<ModDll>.ini`; types inferred; 60 settings found for Hidden Dev Arguments, 23 for Pool Randomizer, zero code written per mod. DONE (2026-07-28 03:35:00)
- ✅ **Author descriptor format** — optional `<ModDll>.settings.json` for proper labels, ranges, scope. DONE (2026-07-28 03:35:00)
- ✅ **"Mods" tab in the game's SETTINGS menu** — sits after Controls, list → detail → back. DONE (2026-07-28 03:45:00)
- ✅ **Per-mod Config button** on the MODS page — opens that mod's settings in place. DONE (2026-07-28 03:40:00)
- ✅ **Client vs Host scope** — client settings are local preferences; host settings are read only by the hosting machine, so the game's own networking carries the effect and nothing can desync. DONE (2026-07-28 03:30:00)

## ACT 3 · BAPBAP Third Person (COMPLETE)
- ✅ **Clean-room rewrite** — written against the game's API, MIT, publishable. DONE (2026-07-28 02:35:00)
- ✅ **Fix upstream stutter** — removed a per-frame `FindObjectOfType`; all lookups cached. DONE (2026-07-28 02:35:00)
- ✅ **Pointer for cards and menus** — own overlay canvas at sortingOrder 30000, above menus, never eats clicks. DONE (2026-07-28 01:20:00)
- ✅ **Crosshair hidden** during play. DONE (2026-07-28 01:05:00)
- ✅ **Camera settings** — FOV, sensitivity, height, pitch, live-reloaded from the ini. DONE (2026-07-28 03:10:00)

## ACT 4 · Distribution (IN PROGRESS)
- 🔄 **Public GitHub repo** — MIT, sources + prebuilt DLLs, assembled at `~/projects/bapbap-mods`, not yet committed.
- ⬜ **One-line installer** — PowerShell for Windows friends, bash for Linux; pulls the latest DLLs into `Mods/`.
- ⬜ **Document the settings schema** — `<ModDll>.settings.json` so anyone can expose settings without touching manager code.
- ⬜ **Windows verification** — the manager has only ever run under Proton; one friend testing closes every "untested" caveat.

## ACT 5 · Mod downloader (NOT STARTED)
- ⬜ **Browse available mods in-game** — merged BAPHub + our own manifest, showing Installed / Install / Update.
- ⬜ **Install from GitHub** — download, verify sha256 from the manifest, write to `Mods/`, prompt restart.
- ⬜ **Uninstall from in-game** — remove the DLL, with an option to keep or delete its settings.
- ⬜ **Decide Boss Rush handling** — those 4 mods need a different game build; show with a warning or hide.

## ACT 6 · Gameplay mods (DESIGNED, NOT BUILT)
- ⬜ **Round Mutators** — unlock the devs' 16 built-in `GM_*` modifiers (AllGigantic, MeteorShower, NightTime, XCOM…) in normal lobbies. IDs 0–15 already captured. The highest-value idea on the list.
- ⬜ **Escalating Chaos** — `augmentsPerRound` climbs each round.
- ⬜ **Item Roulette** — random allowed-item subset per round via `serializedSettings`.
- ⬜ **Shuffle** — auto-randomize map and gamemode between rounds.

## Known issues / accepted compromises
- ⚠️ **Nav-bar highlight is dimmer than the game's** — 10+ attempts. A/B dump of two lit tabs proved every readable property is identical (all UberSDF layers, colours, components); the difference is internal SDF shader state with no accessible handle. Accepted.
- ⚠️ **The previously selected tab keeps its blue marker** while the MODS page is open. Closing the game's page was verified in the log to NOT clear it, and risked a blank lobby, so it was reverted.
- ⚠️ **Residual stutter, roughly one spike every two minutes** — user-accepted. Arena Random Chars / Asset Dumper / More Custom Settings were never isolated individually; Hidden Dev Arguments is tunable via its ini.

## Claude's Roadmap (my ideas for the future)
- ⬜ **Mod profiles** — save/load whole sets of enabled mods and settings, e.g. "chaos night" vs "vanilla-ish".
- ⬜ **Publish the manager to BAPHub** — the author documents a package format; it would handle distribution and updates.
- ⬜ **Report the two BAPFPS bugs upstream** to Sonic0810 — both precisely diagnosed, easy write-up, fixes everyone.
- ⬜ **Settings search** — Hidden Dev Arguments alone exposes 60 keys; a filter box would help.

## Changelog
- **2026-07-28 04:28:06** — Roadmap initialized. Captures the full first build session: recon, manager, settings system, Third Person mod, and the open distribution/downloader work.
- **2026-07-28 04:26:00** — Nav-bar highlight investigation closed. A/B dump of two simultaneously lit tabs showed the only readable difference was label alpha; matched that and accepted the remaining visual gap.
- **2026-07-28 03:50:00** — Manager restyled to the game's palette; page made scrollable and full-bleed.
- **2026-07-28 03:35:00** — Settings became fully generic: parsed from any mod's own ini, plus an optional author descriptor format. Removed the hardcoded per-mod list.
- **2026-07-28 03:20:00** — Catalog rewritten to discover mods from MelonLoader instead of a hardcoded table.
- **2026-07-28 02:35:00** — BAPBAP Third Person v1.0.0: clean-room rewrite against the game's API, replacing the derivative build.
- **2026-07-28 01:20:00** — Third-person pointer fixed after 6 attempts; the game re-hides the OS cursor every frame, so the mod owns its own overlay canvas.
- **2026-07-27 20:00:00** — Stutter traced to GC pressure from per-second `FindObjectsOfTypeAll` + `TryCast`, including in our own code and diagnostics. All repeated scanning removed.
- **2026-07-27 18:58:00** — First custom mod ran successfully: recovered all 16 built-in GameModifier IDs.
- **2026-07-27 17:58:00** — MelonLoader verified working under Proton; 125 MB of Il2Cpp assemblies generated.
