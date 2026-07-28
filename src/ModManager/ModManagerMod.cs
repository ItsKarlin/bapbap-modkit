// BAPBAP Mods — in-game mod manager.
//
// THIS IS THE F5 FALLBACK PANEL. The native in-menu tab (UILobbyTabGroup clone) and the
// arena-settings screen integration are a separate, later stage — this layer owns all the
// logic so that the native UI becomes purely presentational when it lands.
//
// Rules encoded here:
//   * Host-only mods cannot be toggled while a match is running (fail-safe locked).
//   * Client-side mods are always toggleable.
//   * BAPHub mods toggle by moving their DLL to/from Mods/disabled/ -> next launch.
//   * Local mods toggle live via the shared flag file.
//
// Flag: UserData/BAPBAPMods.flags.json -> EXPERIMENTS."mod-manager" (default true)
// Prune: delete Mods/BAPBAPModManager.dll

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;

[assembly: MelonInfo(typeof(BapbapMods.Manager.ModManagerMod), "BAPBAP Mods", "0.1.0", "ItsKarlin")]
[assembly: MelonGame(null, "BAPBAP")]

namespace BapbapMods.Manager
{
    public class ModManagerMod : MelonMod
    {
        private const string ExperimentId = "mod-manager";
        private const KeyCode ToggleKey = KeyCode.F5;

        /// Key-triggered network probe. Nothing runs unless it is pressed, so this costs
        /// nothing at idle. Exists to answer one question that cannot be tested offline:
        /// whether HttpClient works under Proton in this runtime.
        private const KeyCode CatalogProbeKey = KeyCode.F6;
        private bool _probeRunning;
        private bool _loggedSettingsOverlap;

        /// The Mods folder's parent. Every catalog write is confined below this.
        private static string GameRoot => Path.GetDirectoryName(MelonEnvironment.ModsDirectory);

        private bool _enabled;
        private bool _panelOpen;
        private FlagStore _flags;
        private List<ModEntry> _entries = new List<ModEntry>();
        private string _modsDir;
        private string _userDataDir;
        private string _status = "";
        private float _stateTimer;
        private float _injectTimer;
        private int _injectAttempts;
        private Vector2 _scroll;
        private readonly NativePage _nativePage = new NativePage();
        private readonly SettingsTab _settingsTab = new SettingsTab();
        private float _settingsTabTimer;

        public override void OnInitializeMelon()
        {
            _userDataDir = MelonEnvironment.UserDataDirectory;
            _modsDir = MelonEnvironment.ModsDirectory;
            _flags = new FlagStore(_userDataDir);

            _enabled = _flags.Get(ExperimentId, true);
            if (!_enabled)
            {
                LoggerInstance.Msg($"[{ExperimentId}] disabled by flag — doing nothing.");
                return;
            }

            Refresh();
            NativeTab.Init(LoggerInstance);
            _nativePage.Init(LoggerInstance);
            _nativePage.OnInstalledChanged = () =>
            {
                Refresh();
                _nativePage.SetEntries(_entries);
            };
            _nativePage.OnConfigRequested = m =>
                _status = $"{m.DisplayName}: open Settings > Mods to edit its options.";
            _settingsTab.Init(LoggerInstance, () => _entries);
            LoggerInstance.Msg($"[{ExperimentId}] ready. Press {ToggleKey} to open the Mods panel.");
            LoggerInstance.Msg($"[{ExperimentId}] {_entries.Count} mod(s) catalogued.");
        }

        /// Fetches the real catalog from every configured source and reports what came back.
        /// Answers, in one keypress: does HttpClient work here, does TLS work under Proton,
        /// does the merge produce sane data, and does a callback land on the main thread.
        private void RunCatalogProbe()
        {
            if (_probeRunning) { LoggerInstance.Msg($"[{ExperimentId}] probe already running."); return; }
            _probeRunning = true;

            LoggerInstance.Msg($"[{ExperimentId}] catalog probe: starting.");
            var started = DateTime.UtcNow;

            Task.Run(async () =>
            {
                var problems = new List<string>();
                try
                {
                    // Shipped with the manager, so this needs no network and works offline.
                    var sources = Catalog.LoadSources(_userDataDir);
                    Report($"sources ok: {sources.Count} configured (no fetch needed)", started);

                    var catalog = await CatalogFetcher.FetchCatalogAsync(sources, problems).ConfigureAwait(false);
                    if (!catalog.Ok)
                    {
                        Report($"catalog fetch FAILED: {catalog.Error}", started);
                        return;
                    }

                    Report($"catalog ok: {catalog.Value.Count} package(s) merged", started);
                    foreach (string problem in problems) Report($"  note: {problem}", started);

                    // Then one version manifest, which is what an Install would need next.
                    if (catalog.Value.Count > 0)
                    {
                        var first = catalog.Value[0];
                        var version = await CatalogFetcher.FetchVersionAsync(first).ConfigureAwait(false);
                        if (!version.Ok)
                            Report($"version fetch FAILED for {first.Id}: {version.Error}", started);
                        else
                        {
                            string safe = Catalog.IsInstallable(GameRoot, version.Value, out var why)
                                ? "installable" : $"NOT installable ({why})";
                            Report($"version ok: {first.Id} {version.Value.Version}, " +
                                   $"{version.Value.Files.Count} file(s), {safe}", started);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Report($"probe THREW: {ex.GetType().Name}: {ex.Message}", started);
                }
                finally
                {
                    MainThread.Post(() => _probeRunning = false);
                }
            });
        }

        /// Log from the main thread, so this also proves the dispatcher works.
        private void Report(string message, DateTime started)
        {
            double ms = (DateTime.UtcNow - started).TotalMilliseconds;
            MainThread.Post(() => LoggerInstance.Msg($"[{ExperimentId}] probe (+{ms:F0}ms) {message}"));
        }

        public override void OnUpdate()
        {
            if (!_enabled) return;

            if (Input.GetKeyDown(ToggleKey))
            {
                _panelOpen = !_panelOpen;
                if (_panelOpen) Refresh();
            }

            // Returns on a single volatile read when no network callback is waiting.
            MainThread.Drain(ex => LoggerInstance.Error($"[{ExperimentId}] main-thread callback failed: {ex}"));

            if (Input.GetKeyDown(CatalogProbeKey)) RunCatalogProbe();

            // F7: dump the settings window's real state. Key-triggered, so it costs nothing
            // until asked.
            if (Input.GetKeyDown(KeyCode.F7))
            {
                _settingsTab.DumpState(LoggerInstance);
                LoggerInstance.Msg($"[{ExperimentId}] our page: built={_nativePage.Built} " +
                                   $"visible={_nativePage.Visible} panelOpen={_panelOpen}");
            }

            // Our page covers the lobby, so it must never share the screen with the game's
            // settings window — the two overlap and the settings panel reads as broken.
            if (_panelOpen && _settingsTab.IsWindowOpen())
            {
                _panelOpen = false;
                _nativePage.Show(false);
                if (!_loggedSettingsOverlap)
                {
                    _loggedSettingsOverlap = true;
                    LoggerInstance.Msg($"[{ExperimentId}] settings window opened - hid the mods page.");
                }
            }

            // Re-evaluate match state on a timer rather than every frame — the check walks
            // object lists and does not need 60Hz resolution.
            // Match state only needs to be responsive while the UI is on screen. Off screen
            // it just feeds the lock, so a slow tick is plenty and keeps the scan cost low.
            bool uiVisible = _panelOpen || _nativePage.Visible;
            _stateTimer += Time.unscaledDeltaTime;
            if (_stateTimer >= (uiVisible ? 1f : 5f))
            {
                _stateTimer = 0f;
                MatchState.Evaluate();
                if (_nativePage.Visible) _nativePage.RefreshRows();
            }

            // Injection retries used to run once a second FOREVER. In a match the lobby UI
            // does not exist, so they could never succeed — meaning a full object scan every
            // second for the entire match. Now: never retry during a match, back off between
            // attempts, and give up after a bounded number of tries until the next scene.
            if (!NativeTab.Injected || !_nativePage.Built)
            {
                _injectTimer += Time.unscaledDeltaTime;

                bool inMatch = MatchState.Current == MatchStatus.InMatch;
                float backoff = Mathf.Min(2f + _injectAttempts * 2f, 15f);

                if (!inMatch && _injectAttempts < 12 && _injectTimer >= backoff)
                {
                    _injectTimer = 0f;
                    _injectAttempts++;

                    if (!NativeTab.Injected) NativeTab.TryInject();
                    if (NativeTab.Injected && !_nativePage.Built)
                        _nativePage.TryBuild(_entries, HandleNativeToggle);
                }
            }

            // The settings menu does not exist until opened, so retry slowly until it does.
            if (!_settingsTab.Built)
            {
                _settingsTabTimer += Time.unscaledDeltaTime;
                if (_settingsTabTimer >= 5f)
                {
                    _settingsTabTimer = 0f;
                    _settingsTab.TryBuild();
                }
            }
            else
            {
                _settingsTab.Poll();
            }

            _nativePage.Tick(Time.unscaledDeltaTime);

            if (NativeTab.ClickPending)
            {
                NativeTab.ClickPending = false;

                // Prefer the real in-game page; fall back to the overlay only if it failed.
                if (_nativePage.Built)
                {
                    bool show = !_nativePage.Visible;
                    Refresh();
                    _nativePage.Show(show);
                    _panelOpen = false;
                }
                else
                {
                    _panelOpen = !_panelOpen;
                    if (_panelOpen) Refresh();
                }
            }
        }

        /// Tab highlighting runs in LateUpdate, after the game's own tab controller has run
        /// its Update. Writing it during OnUpdate meant their controller re-selected its tab
        /// immediately afterwards, so the previously selected tab stayed lit.
        public override void OnLateUpdate()
        {
            if (!_enabled) return;
            _nativePage.DriveHighlightLate();
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            // The cloned button and page die with the old scene; allow rebuilding.
            NativeTab.Reset();
            _nativePage.Reset();
            _settingsTab.Reset();
            _injectAttempts = 0;
            _injectTimer = 0f;
        }

        // Returns false when the change is refused (e.g. host-locked mid-match) so the
        // native switch can snap back to its previous state.
        private bool HandleNativeToggle(ModEntry entry, bool value)
        {
            // Option A: unrecognised mods stay toggleable; only confirmed host mods lock.
            if (entry.Category == ModCategory.HostOnly && MatchState.HostEditingLocked)
            {
                _status = "Host settings are locked while a match is running.";
                LoggerInstance.Msg($"[{ExperimentId}] refused {entry.Id} -> {value} (match running)");
                return false;
            }

            if (!entry.Installed) return false;

            ApplyToggle(entry, value);
            return true;
        }

        private void Refresh()
        {
            _flags.Load();
            _entries = ModCatalog.Build(_modsDir, _flags);
            MatchState.Evaluate();
        }

        public override void OnGUI()
        {
            if (!_enabled || !_panelOpen) return;

            const int w = 620, h = 560;
            var rect = new Rect((Screen.width - w) / 2f, (Screen.height - h) / 2f, w, h);

            GUI.Box(rect, "");
            GUILayout.BeginArea(new Rect(rect.x + 14, rect.y + 12, rect.width - 28, rect.height - 24));

            GUILayout.Label("<b>MODS</b>");
            DrawMatchStateBanner();
            GUILayout.Space(6);

            _scroll = GUILayout.BeginScrollView(_scroll);

            DrawSection("HOST-ONLY  —  affects everyone in your lobby", ModCategory.HostOnly);
            GUILayout.Space(10);
            DrawSection("CLIENT-SIDE  —  affects only you", ModCategory.ClientSide);
            GUILayout.Space(10);
            DrawSection("UNRECOGNISED  —  scope unknown", ModCategory.Unknown);

            GUILayout.EndScrollView();

            GUILayout.Space(6);
            DrawFooter();

            GUILayout.EndArea();
        }

        private void DrawMatchStateBanner()
        {
            string label;
            switch (MatchState.Current)
            {
                case MatchStatus.InMenu:
                    label = "In menu — host settings unlocked";
                    break;
                case MatchStatus.InMatch:
                    label = "MATCH RUNNING — host settings locked";
                    break;
                default:
                    label = "State unknown — host settings locked (fail-safe)";
                    break;
            }
            GUILayout.Label($"{label}   ({MatchState.LastReason})");
        }

        private void DrawSection(string header, ModCategory category)
        {
            GUILayout.Label($"<b>{header}</b>");

            bool locked = category == ModCategory.HostOnly && MatchState.HostEditingLocked;

            foreach (var e in _entries)
            {
                if (e.Category != category) continue;

                GUILayout.BeginHorizontal();

                GUI.enabled = e.Installed && !locked;

                bool now = GUILayout.Toggle(e.Enabled, "", GUILayout.Width(20));
                if (now != e.Enabled) ApplyToggle(e, now);

                GUI.enabled = true;

                string restartTag = e.RequiresRestart ? "  [restart]" : "";
                string missing = e.Installed ? "" : "  [not installed]";
                GUILayout.Label($"{e.DisplayName}   <i>({e.SourceLabel})</i>{restartTag}{missing}");

                GUILayout.EndHorizontal();
                GUILayout.Label($"      {e.Description}");
            }

            if (locked)
                GUILayout.Label("      Locked while a match is running. Return to the menu to change these.");
        }

        private void DrawFooter()
        {
            GUILayout.BeginHorizontal();

            if (GUILayout.Button("Refresh", GUILayout.Width(90))) Refresh();

            if (GUILayout.Button("Restart game", GUILayout.Width(130)))
                RequestRestart();

            if (GUILayout.Button("Close", GUILayout.Width(80))) _panelOpen = false;

            GUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(_status)) GUILayout.Label(_status);

            GUILayout.Label("Mods apply on the next launch unless stated otherwise.");
        }

        private void ApplyToggle(ModEntry e, bool value)
        {
            try
            {
                if (e.Source == ModSource.Local)
                {
                    _flags.Set(e.Id, value);
                    e.Enabled = value;
                    _status = $"{e.DisplayName}: {(value ? "enabled" : "disabled")} (live).";
                    LoggerInstance.Msg($"[{ExperimentId}] {e.Id} -> {value} (live flag)");
                    return;
                }

                // BapHub: park the DLL rather than delete it, so nothing is ever lost.
                string disabledDir = Path.Combine(_modsDir, "disabled");
                Directory.CreateDirectory(disabledDir);

                string enabledPath = Path.Combine(_modsDir, e.DllName);
                string parkedPath = Path.Combine(disabledDir, e.DllName);

                if (value && File.Exists(parkedPath))
                {
                    File.Move(parkedPath, enabledPath);
                }
                else if (!value && File.Exists(enabledPath))
                {
                    File.Move(enabledPath, parkedPath);
                }

                e.Enabled = value;
                _status = $"{e.DisplayName}: {(value ? "enabled" : "disabled")} — takes effect after a restart.";
                LoggerInstance.Msg($"[{ExperimentId}] {e.Id} -> {value} (DLL moved, pending restart)");
            }
            catch (Exception ex)
            {
                _status = $"Could not toggle {e.DisplayName}: {ex.Message}";
                LoggerInstance.Error($"[{ExperimentId}] toggle failed for {e.Id}: {ex}");
            }
        }

        // Writes a marker the launch wrapper watches for, then quits. Relaunching from
        // inside a Proton prefix is unreliable, so an external wrapper does the actual
        // restart — see restart-wrapper.sh.
        private void RequestRestart()
        {
            try
            {
                if (MatchState.Current == MatchStatus.InMatch)
                {
                    _status = "WARNING: you are in a match. Restarting will drop the lobby for everyone. " +
                              "Press again to confirm.";
                    if (!_restartArmed) { _restartArmed = true; return; }
                }

                string marker = Path.Combine(_userDataDir, "BAPBAPMods.restart-request");
                File.WriteAllText(marker, DateTime.UtcNow.ToString("o"));
                LoggerInstance.Msg($"[{ExperimentId}] restart requested — marker written, quitting.");
                Application.Quit();
            }
            catch (Exception ex)
            {
                _status = $"Restart failed: {ex.Message}";
                LoggerInstance.Error($"[{ExperimentId}] restart failed: {ex}");
            }
        }

        private bool _restartArmed;
    }
}
