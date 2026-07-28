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
- **The modkit ships the manager, not a mod bundle** — the one-line installer installs the manager and nothing else. Mods, including our own Third Person, are things you fetch from the in-game downloader. Bundling them would make the manager a package with favourites, which is the opposite of discovery. DECIDED (2026-07-28 07:26:00)

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
- ✅ **v1.0.1 — ini migration** — an ini from an older build now gains keys added since, values preserved. Verified offline against the real stale ini with a 15-check probe (migration, idempotency, edited-values-survive, up-to-date-file-untouched, absent-file-creates-default), then **confirmed in-game 07:42**: migrated exactly once, `PointerSize`/`PointerSortingOrder` intact, no reload spam, no rewrite loop, no errors. DONE (2026-07-28 07:30:00)

## ACT 4 · Distribution (IN PROGRESS)
- ✅ **Consolidate sources** — `~/game-mods/` working copies retired to `~/game-mods/retired-worktrees/` and replaced with symlinks into the repo. Both projects verified building from `src/`. DONE (2026-07-28 07:12:00)
- ✅ **GitHub repo** — named **bapbap-modkit**, MIT, sources + prebuilt DLLs + installers. Pushed **private** at the user's request; flip to public when ready. `HANDOFF.md` deliberately excluded (gitignored) — it contains personal notes and home paths. DONE (2026-07-28 07:20:00)
- ✅ **One-line installer** — `install/install.ps1` and `install/install.sh`. Finds the game via Steam's `appmanifest`/`libraryfolders.vdf` (not a guessed folder name), sha256-verifies every download against `dist/manifest.json`, and stages everything before touching `Mods/`. DONE (2026-07-28 07:18:00)
- ✅ **Installer offers MelonLoader** — if none is found, downloads the BAPHub-pinned `0.7.2-ci.2388` from `Sonic0810/BAPBAPLauncher`, sha256-verified, extracted into the game folder; prints the Proton `WINEDLLOVERRIDES` reminder afterwards. Existing installs of another version are left alone with a warning. DONE (2026-07-28 07:19:00)
- ✅ **Document the settings schema** — `docs/settings-schema.md`: ini inference, the `<ModDll>.settings.json` descriptor, every field, and the parser's limits. DONE (2026-07-28 07:15:00)
- ✅ **Flip the repo public** — live at https://github.com/ItsKarlin/bapbap-modkit. DONE (2026-07-28 07:58:00)
- ✅ **Linux one-liner verified end-to-end** — ran the real `curl | bash` against the live public repo: found the game, read the pinned loader version, fetched the manifest, verified sha256, correctly reported "already up to date" instead of rewriting. Exit 0, manager only. DONE (2026-07-28 08:00:00)
- ⬜ **Windows verification** — `install.ps1` has still never been executed anywhere (no `pwsh` here). The untested parts are the Steam registry read and the `libraryfolders.vdf` parse. Needs one friend to run it.
- ⬜ **A screenshot for the README** — the README currently describes the MODS page in prose.

## ACT 5 · Mod downloader (WORKING, verified in-game)
- ✅ **Design the catalog format** — `docs/catalog-schema.md`, plus real data at `catalog/catalog.json` and a per-version manifest. Mirrors BAPHub's `channels/release/` layout so one parser reads both and our packages could publish through their launcher unchanged. Two levels: browse costs one request, install costs one more. DONE (2026-07-28 08:06:00)
- ✅ **Boss Rush question answered** — BAPHub packages already carry `requirements[]` with `type`/`text`/`severity`. Render it generically and show those mods with a warning rather than hiding them. No per-mod hardcoding. DONE (2026-07-28 08:06:00)
- ✅ **Uninstall question answered** — remove only the files in `version.json`; keep `UserData/<Mod>.ini` by default so reinstalling restores settings. Deleting settings is a separate explicit action. DONE (2026-07-28 08:06:00)
- ✅ **JSON parsing solved without writing a parser** — MelonLoader already ships Newtonsoft.Json 13.0.4 in the same net6 runtime the mod runs in. Referenced it instead of hand-rolling a tokenizer. DONE (2026-07-28 08:12:00)
- ✅ **`Catalog.cs` — parse, merge, path safety** — reads both our catalog and BAPHub's from one code path; sources described by data in `catalog/sources.json`, nothing per-source in code. Verified by a 34-check offline probe against **live BAPHub data**: all 12 packages parsed, nested `authors[]` never mistaken for the package id, template-built version URLs, merge collisions, 8 traversal attempts rejected, malformed input never throws. DONE (2026-07-28 08:14:00)
- ⚠️ **BAPHub browse entries have no author** — `packages.json` omits it; authors live in each package's `package.json`. Either show the source name in the list or lazily fetch on detail view. Decide when building the UI.
- ✅ **Fetch layer** — `CatalogFetcher.cs`. **HttpClient, not UnityWebRequest**: MelonLoader mods run a real .NET 6 runtime, so no coroutines, no Il2Cpp interop, no main-thread pumping. Off-thread work, results marshalled back through a dispatcher `OnUpdate` drains in one volatile read when idle. Downloads refuse to run unhashed, verify after writing, delete partials on failure. **Confirmed in-game 08:15 via the F6 probe: 265ms for sources → 2 sources → 13 packages merged → version manifest fetched and gated. HttpClient and TLS both work under Proton.** DONE (2026-07-28 08:16:00)
- ✅ **Install/uninstall execution** — `ModInstaller.cs`. Stages and hash-verifies every file before touching the game folder; a move that fails halfway rolls back. Uninstall reads an install receipt, not the network, so it works offline, and keeps the mod's ini unless explicitly told not to. 27-check offline probe. DONE (2026-07-28 08:30:00)
- ✅ **Browse UI** — a tab on the existing MODS page, so it inherits the palette and layout. `BrowseTab` owns state, `NativePage` renders it. Install is two steps: clicking Install fetches the manifest and shows version, scope, requirement warnings and the exact files to be written before anything downloads. DONE (2026-07-28 08:22:00)
- ✅ **Verified in-game end to end** — installed SpeedrunTimer from BAPHub; the file on disk hashed identically to BAPHub's declared sha256, staging cleaned up, receipt written. HP Numbers installed over a hand-installed copy, exercising backup-and-replace. Remove confirmed on both. DONE (2026-07-28 08:50:00)
- ✅ **Manager v0.2.0 promoted to `dist/`** — the public one-liner now ships the downloader. DONE (2026-07-28 09:15:00)

### Bugs found and fixed while testing ACT 5
- ✅ **Install 404** — `sourcePath` in `version.json` is relative to the *version folder*, not the catalog root, so payload URLs resolved to `/release/files/X.dll`. The manifest now records its own folder. DONE (2026-07-28 08:32:00)
- ✅ **Hand-installed mods showed as missing** — browse consulted only install receipts, which exist only for its own installs. It now also matches what MelonLoader loaded, comparing names loosely ("BAPBAP More Custom Settings" vs assembly "MoreCustomSettings"). DONE (2026-07-28 08:35:00)
- ✅ **Could not remove a loaded mod** — Windows will not delete a DLL mapped into the process and MelonLoader cannot unload it. It will still *move*, which is what matters: locked files go to a pending-delete folder and are swept at startup. DONE (2026-07-28 08:58:00)
- ✅ **Downloaded mods missing from INSTALLED** — a mod installed this session is in `Mods/` but was in neither `RegisteredMelons` nor `disabled/`, so it vanished until relaunch and read as a failed install. `ModCatalog` now lists unloaded DLLs too. DONE (2026-07-28 08:55:00)
- ✅ **Manager could disable itself** — parking its own DLL in `disabled/` left no UI to undo it. Refused, matched on assembly name. DONE (2026-07-28 09:02:00)
- ✅ **Settings "Mods" panel appeared empty** — took four attempts and the F7 probe twice. Cause: the game's tab controller does not know our tab exists, so clicking it never runs the controller's switch-panel logic and the previously selected panel stayed active at alpha 1, sharing the content area. Fixed by fading it the way the game does. Two self-inflicted regressions followed: the fade was only restored on close (so a reactivated panel came back invisible), and then the restore check used *active* as its signal — but we deliberately leave faded panels active, so it fired instantly and closed the tab on open. The correct signal is the game writing a non-zero alpha back over ours. DONE (2026-07-28 09:12:00)
- ⬜ **Don't conflate safety with compatibility** — `IsInstallable` means "files are hashed and land somewhere allowed", NOT "works on your game build". Build compatibility lives in `requirements[]` and must be surfaced separately, or someone installs a Boss Rush mod onto the wrong build and it looks like our bug.
- ⬜ **Browse available mods in-game** — merged BAPHub + our own catalog, showing Installed / Install / Update.
- ⬜ **Install from GitHub** — download, verify sha256 from the manifest, write to `Mods/`, prompt restart.
- ⬜ **Uninstall from in-game** — remove the DLL, with an option to keep or delete its settings.
- ⬜ **Decide Boss Rush handling** — those 4 mods need a different game build; show with a warning or hide.

## ACT 6 · Gameplay mods (DESIGNED, NOT BUILT)
- ⬜ **Round Mutators** — unlock the devs' 16 built-in `GM_*` modifiers (AllGigantic, MeteorShower, NightTime, XCOM…) in normal lobbies. IDs 0–15 already captured. The highest-value idea on the list.
- ⬜ **Escalating Chaos** — `augmentsPerRound` climbs each round.
- ⬜ **Item Roulette** — random allowed-item subset per round via `serializedSettings`.
- ⬜ **Shuffle** — auto-randomize map and gamemode between rounds.

## Known issues / accepted compromises
- ✅ **~~Third Person never migrates an existing ini~~** — `Save()` only ran when the file was missing, so an ini written before the camera settings existed never gained `FovMultiplier`, `Sensitivity`, `CameraHeight` or `CameraPitch`, and the manager (which reads the ini) could not show them. Anyone who installed before 2026-07-28 03:10 was affected, including this machine. Found 2026-07-28 07:14:00. FIXED in v1.0.1 — `Load()` now records which keys it saw and rewrites once if any are absent, keeping every parsed value. DONE (2026-07-28 07:30:00)
- ⚠️ **Nav-bar highlight is dimmer than the game's** — 10+ attempts. A/B dump of two lit tabs proved every readable property is identical (all UberSDF layers, colours, components); the difference is internal SDF shader state with no accessible handle. Accepted.
- ⚠️ **The previously selected tab keeps its blue marker** while the MODS page is open. Closing the game's page was verified in the log to NOT clear it, and risked a blank lobby, so it was reverted.
- ⚠️ **Residual stutter, roughly one spike every two minutes** — user-accepted. Arena Random Chars / Asset Dumper / More Custom Settings were never isolated individually; Hidden Dev Arguments is tunable via its ini.

## Claude's Roadmap (my ideas for the future)
- ⬜ **Mod profiles** — save/load whole sets of enabled mods and settings, e.g. "chaos night" vs "vanilla-ish".
- ⬜ **Publish the manager to BAPHub** — the author documents a package format; it would handle distribution and updates.
- ⬜ **Settings search** — Hidden Dev Arguments alone exposes 60 keys; a filter box would help.

## Changelog
- **2026-07-28 09:15:00** — ACT 5 working end to end and promoted: browse, confirm, download, sha256-verify, install, remove — all confirmed in the running game, not just offline. Manager v0.2.0 is now what the public installer ships. Six bugs found and fixed by testing, the worst being a settings panel that took four attempts and two probe dumps; the lesson each time was that the game's UI hides by *fading*, never by deactivating.
- **2026-07-28 08:00:00** — Repo is public and the Linux one-liner is verified end-to-end against it. README trimmed hard (146 → 80 lines) and stripped of everything about Third Person; the mod stays in the repo but is no longer featured anywhere user-facing. BAPFPS references removed entirely.
- **2026-07-28 07:30:00** — Third Person v1.0.1 fixes the ini-migration bug: `Load()` tracks which keys it saw and rewrites once if any known key is absent, preserving parsed values. Guarded against a rewrite loop (the 2s config watcher would have amplified it) by keeping `KnownKeys` exactly in sync with what `Save()` writes. Verified with an offline probe against the real stale ini, 15 checks, all passing.
- **2026-07-28 07:26:00** — Third Person unbundled from the installer. `dist/manifest.json` is now the installer payload only (the manager); the Third Person DLL stays hosted in `dist/` as the downloader's first real catalog entry. The modkit installs a manager, not a mod bundle.
- **2026-07-28 07:21:00** — Distribution act mostly closed. Sources consolidated into the repo (working copies retired, symlinked back), README/LICENSE/settings-schema written, both installers built with sha256 verification and optional MelonLoader install, and the repo pushed private as `ItsKarlin/bapbap-modkit`. Found and logged the Third Person ini-migration bug while checking the docs were truthful.
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
