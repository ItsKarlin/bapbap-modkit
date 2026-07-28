// A REAL in-game page for the Mods tab — not an overlay.
//
// Approach: clone one of the game's own UILobbyTabPage GameObjects, strip its script and
// contents, then rebuild the inside out of the game's own widgets (their TMP text objects
// and their "InputToggle" switch, which carries sprite-swap, hover, SFX and theme
// components). The result is a genuine page object living in the game's Pages container,
// drawn by the game's canvas, in the game's fonts and colours.
//
// Why not a true 7th tab in their system: UILobbyTabGroup's PageIndex is a fixed 6-value
// enum and its page fields are private, so their controller can never route to a new page.
// We therefore own the show/hide of this page ourselves — but the page itself is as native
// as any other.

using System;
using System.IO;
using System.Collections.Generic;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;
using UnityEngine.UI;
using Il2CppInterop.Runtime;
using Il2CppTMPro;

namespace BapbapMods.Manager
{
    public class NativePage
    {
        public const string PageName = "BAPBAPModsPage";

        public bool Built { get; private set; }
        public bool Visible { get; private set; }

        private GameObject _page;
        private Transform _contentRoot;
        private bool _browseMode;
        internal readonly BrowseTab Browse = new BrowseTab();

        /// Where rows are built. BrowseTab renders into this too.
        internal Transform ContentRoot => _contentRoot;
        private Transform _pagesContainer;

        private GameObject _toggleTemplate;
        private GameObject _textTemplate;

        private MelonLogger.Instance _log;
        private readonly List<Action> _refreshers = new List<Action>();

        // View state: null = mod list, otherwise that mod's settings.
        private ModEntry _viewMod;

        // Fade state — the game's pages fade in and out, so a hard pop reads as an overlay.
        private CanvasGroup _fade;
        private float _fadeTarget;
        private const float FadeSpeed = 8f;

        // The game's own tab group, used to close whatever page is open so ours replaces it
        // properly instead of covering it.
        private Il2CppBAPBAP.UI.UILobbyTabGroup _tabGroup;
        private float _lastTabGroupLookup = -999f;
        private List<ModEntry> _entries = new List<ModEntry>();
        private Func<ModEntry, bool, bool> _onToggle;

        public void Init(MelonLogger.Instance log)
        {
            _log = log;

            // The browse tab owns its own state; when that state changes the page redraws.
            Browse.Init(log, MelonEnvironment.UserDataDirectory,
                        Path.GetDirectoryName(MelonEnvironment.ModsDirectory));
            Browse.OnChanged = () => { if (Visible) Rebuild(); };
        }

        public void Reset()
        {
            Built = false;
            Visible = false;
            _page = null;
            _contentRoot = null;
            _fade = null;
            _tabGroup = null;
            _refreshers.Clear();
        }

        public bool TryBuild(List<ModEntry> entries, Func<ModEntry, bool, bool> onToggle)
        {
            if (Built && _page != null) return true;

            try
            {
                if (!LocateTemplates()) return false;

                var template = FindPageTemplate();
                if (template == null) return false;

                _page = UnityEngine.Object.Instantiate(template.gameObject, _pagesContainer, false);
                _page.name = PageName;
                _page.SetActive(false);

                StripPageScripts(_page);
                ClearChildren(_page.transform);

                var pageRt = _page.GetComponent<RectTransform>();
                if (pageRt != null)
                {
                    pageRt.anchorMin = Vector2.zero;
                    pageRt.anchorMax = Vector2.one;
                    pageRt.offsetMin = Vector2.zero;
                    pageRt.offsetMax = Vector2.zero;
                }

                _fade = _page.GetComponent<CanvasGroup>() ?? _page.AddComponent<CanvasGroup>();
                _fade.alpha = 0f;

                _contentRoot = BuildLayoutRoot(_page.transform);
                _entries = entries;
                _onToggle = onToggle;
                _viewMod = null;
                Populate(entries, onToggle);

                Built = true;
                _log?.Msg($"native page built from '{template.name}' " +
                          $"with {entries.Count} row(s).");
                return true;
            }
            catch (Exception ex)
            {
                _log?.Warning($"native page build failed: {ex.Message} — F5 overlay still available.");
                Built = false;
                return false;
            }
        }

        public void Show(bool visible)
        {
            if (!Built || _page == null) return;

            _fadeTarget = visible ? 1f : 0f;
            if (visible) _restoredAfterClose = false;

            if (visible)
            {
                _page.SetActive(true);

                // NOT closing the game's page. Verified by log: closing it ("closed 1 lobby
                // page(s)") did NOT clear the blue marker on their nav tab, so that marker
                // is not page-driven either. Closing bought nothing and only risked leaving
                // the lobby blank, so our page simply covers it.
            }

            if (visible)
            {
                // The donor page was CLOSED when we cloned it, so it carries alpha=0 and
                // blocksRaycasts=false from the game's fade system. Force it fully visible,
                // and neutralise the fade drivers so they cannot animate it back to zero.
                var cg = _page.GetComponent<CanvasGroup>();
                if (cg != null)
                {
                    _log?.Msg($"page CanvasGroup alpha was {cg.alpha} — forcing to 1.");
                    cg.alpha = 1f;
                    cg.interactable = true;
                    cg.blocksRaycasts = true;
                }

                DisableFadeDrivers();

                // Draw above whatever page is currently open.
                _page.transform.SetAsLastSibling();

                var rt = _page.GetComponent<RectTransform>();
                if (rt != null) rt.anchoredPosition = Vector2.zero;
            }

            Visible = visible;
            if (visible) foreach (var r in _refreshers) { try { r(); } catch { } }
        }

        /// Drives the fade. Called every frame by the host mod.
        public void Tick(float dt)
        {
            // Clicking any of the game's nav tabs closes our page. Waiting for the game to
            // change something does not work: if the same tab is re-selected it swaps
            // nothing, and our page would stay stuck on top.
            if (Visible && Input.GetMouseButtonDown(0) && PointerOverNavTab())
            {
                Show(false);
            }

            // Hold the highlight while our page is up — and just as importantly, keep it
            // CLEARED while it is not. Our clone keeps its UILobbyTabButton (that component
            // owns the selected look and the click sound), but it selects itself when
            // clicked and nothing in the game ever deselects it, since the game's tab
            // controller does not know our tab exists. Clearing once on close therefore
            // lost to the component re-asserting; this has to be held both ways.
            // Our highlight only. With UILobbyTabButton stripped from the clone, nothing
            // else drives this CanvasGroup, so a plain assignment holds — no fighting a
            // component, no inverted states. The game's own tabs are left completely alone:
            // their page really is still open underneath, so it is correct for their tab to
            // stay lit.
            // (diagnostic dump removed — the A/B is done, see DriveHighlightLate)

            if (_fade == null) return;

            float a = Mathf.MoveTowards(_fade.alpha, _fadeTarget, dt * FadeSpeed);
            if (Mathf.Approximately(a, _fade.alpha)) return;

            _fade.alpha = a;
            _fade.blocksRaycasts = a > 0.5f;
            _fade.interactable = a > 0.5f;

            // Only deactivate once fully faded out, so the animation is actually seen.
            if (a <= 0.001f && _page != null && _page.activeSelf)
            {
                _page.SetActive(false);
                RestoreGamePage();
            }
        }

        /// Asks the game to close its open lobby page, using its own ClosePage so its
        /// internal state stays correct and its close animation plays. Deactivating the
        /// object directly leaves the tab controller thinking the page is still open.
        /// Moves the nav-bar highlight onto MODS (and off PLAY) while our page is open.
        private bool _loggedNavState;
        private Il2CppBAPBAP.UI.UILobbyTabButton _ourTabButton;

        /// The highlight is UberSDF shader state driven by UILobbyTabButton — proven by a
        /// dump showing that component is the only difference between a lit tab and ours.
        /// ToggleSelected is therefore the only thing that can produce it, and it must be
        /// asserted every frame: the component self-selects on click and the game never
        /// deselects a tab it does not know about.
        public void DriveHighlightLate()
        {
            try
            {
                var ours = NativeTab.Button;
                if (ours == null) return;

                if (_ourTabButton == null)
                {
                    _ourTabButton = ours.GetComponent<Il2CppBAPBAP.UI.UILobbyTabButton>();
                    if (_ourTabButton == null) return;
                }

                // Drive ONLY our tab: lit while the page is open, cleared when it is not.
                // Asserted every frame because the component self-selects on click and the
                // game never deselects a tab it does not know about.
                //
                // The game's own tabs are deliberately untouched. Deselecting them cleared
                // the label but not the blue UberSDF shape (that state is written on select
                // and never reset), leaving a half-highlighted tab that looked worse than
                // vanilla. Closing their page did not clear it either — verified by log.
                _ourTabButton.ToggleSelected(Visible);

                // The A/B of two lit tabs showed exactly one readable difference: the game's
                // selected tab has its label visible (Text CanvasGroup alpha 1) while ours
                // sat at 0. Everything else — all three UberSDF layers, every colour, every
                // component — was identical, so the remaining difference in how the blue
                // shape renders is internal SDF state with no accessible handle.
                var label = FindChildDeep(ours.transform, "Text");
                if (label != null)
                {
                    var cg = label.GetComponent<CanvasGroup>();
                    if (cg != null)
                    {
                        float want = Visible ? 1f : 0f;
                        if (!Mathf.Approximately(cg.alpha, want)) cg.alpha = want;
                    }
                }
            }
            catch
            {
                _ourTabButton = null;
            }
        }
        private bool _dumpedComparison;

        /// One-shot: prints every child of a LIT game tab and of our tab, with the three
        /// things that could make a graphic visible — active state, CanvasGroup alpha, and
        /// the Graphic's own enabled flag / colour alpha. Whatever differs between them IS
        /// the highlight. Guessing at which object that is has failed repeatedly.
        private void DumpTabComparisonOnce()
        {
            if (_dumpedComparison) return;

            try
            {
                var row = NativeTab.GameTabRow;
                var ours = NativeTab.Button;
                if (row == null || ours == null) return;

                Transform lit = null;
                for (int i = 0; i < row.childCount; i++)
                {
                    var t = row.GetChild(i);
                    if (t.gameObject == ours || t.IsChildOf(ours.transform)) continue;
                    if (t.GetComponent<Il2CppBAPBAP.UI.UILobbyTabButton>() == null) continue;
                    if (LooksLit(t)) { lit = t; break; }   // both tabs are lit now: direct A/B
                }

                _dumpedComparison = true;
                _log?.Msg($"(sampled lit tab: {(lit == null ? "none" : lit.name)})");

                _log?.Msg("==== TAB COMPARISON ====");
                DescribeTab("LIT GAME TAB", lit);
                DescribeTab("OUR MODS TAB", ours.transform);
                _log?.Msg("==== END ====");
            }
            catch (Exception ex)
            {
                _log?.Warning($"tab comparison failed: {ex.Message}");
            }
        }

        /// The first dump proved the real tell: on an UNLIT tab the label is hidden
        /// (Text CanvasGroup alpha 0), which is why only the selected tab shows its name.
        /// Every tab has opaque hover graphics, so the previous heuristic matched the first
        /// tab it saw and sampled an unlit one.
        private bool LooksLit(Transform tab)
        {
            var text = FindChildDeep(tab, "Text");
            if (text == null) return false;

            var cg = text.GetComponent<CanvasGroup>();
            return cg != null && cg.alpha > 0.5f;
        }

        private void DescribeTab(string label, Transform tab)
        {
            if (tab == null) { _log?.Msg($"{label}: <none found>"); return; }

            _log?.Msg($"{label}: {tab.name}");

            // The tab's OWN graphic was never dumped — only its children. And only alpha was
            // recorded, not RGB, so a colour-driven highlight was invisible to the previous
            // comparison (which showed the two tabs as identical).
            var self = tab.GetComponent<Graphic>();
            _log?.Msg($"  [self] gfx={(self == null ? "-" : $"{(self.enabled ? "on" : "off")} rgba={Fmt(self.color)}")}");

            var comps = tab.GetComponents<Component>();
            var names = new System.Text.StringBuilder();
            for (int i = 0; i < comps.Length; i++)
                if (comps[i] != null) names.Append(comps[i].GetIl2CppType().Name).Append(' ');
            _log?.Msg($"  [self] components: {names}");

            DescribeChildren(tab, 1);
        }

        private static string Fmt(Color c)
            => $"{c.r:0.##},{c.g:0.##},{c.b:0.##},{c.a:0.##}";

        private void DescribeChildren(Transform t, int depth)
        {
            for (int i = 0; i < t.childCount; i++)
            {
                var c = t.GetChild(i);
                string pad = new string(' ', depth * 2);

                var cg = c.GetComponent<CanvasGroup>();
                var g = c.GetComponent<Graphic>();

                string cgTxt = cg == null ? "-" : cg.alpha.ToString("0.##");
                string gTxt = g == null ? "-" : $"{(g.enabled ? "on" : "off")} rgba={Fmt(g.color)}";

                // Component list too: whatever draws the blue shape may not be a Graphic.
                var comps = c.GetComponents<Component>();
                var names = new System.Text.StringBuilder();
                for (int k = 0; k < comps.Length; k++)
                    if (comps[k] != null) names.Append(comps[k].GetIl2CppType().Name).Append(' ');

                _log?.Msg($"{pad}{c.name}  active={c.gameObject.activeInHierarchy}  cg={cgTxt}  gfx={gTxt}  [{names}]");

                if (depth < 2) DescribeChildren(c, depth + 1);
            }
        }
        private Transform _previouslySelectedTab;
        private bool _restoredAfterClose;

        /// Keeps our tab unlit whenever the page is closed, and hands the previously lit tab
        /// back to the game exactly once.
        private void EnsureOurTabDeselected()
        {
            try
            {
                var ours = NativeTab.Button;
                if (ours == null) return;

                StyleNavTab(ours.transform, false);

                if (!_restoredAfterClose && _previouslySelectedTab != null)
                {
                    _restoredAfterClose = true;
                    RestoreGameTab(_previouslySelectedTab);
                    _previouslySelectedTab = null;
                }
            }
            catch
            {
            }
        }

        private void ApplyNavSelection(bool oursSelected)
        {
            try
            {
                var row = NativeTab.GameTabRow;
                var ours = NativeTab.Button;

                if (!_loggedNavState)
                {
                    _loggedNavState = true;
                    _log?.Msg($"nav highlight: row={(row == null ? "NULL" : row.childCount + " tabs")}, " +
                              $"button={(ours == null ? "NULL" : ours.name)}");
                }

                if (row == null || ours == null) return;

                // Clear every game tab while ours is up, then light ours.
                if (oursSelected)
                {
                    for (int i = 0; i < row.childCount; i++)
                    {
                        var tab = row.GetChild(i);
                        if (tab.gameObject == ours || tab.IsChildOf(ours.transform)) continue;

                        // Remember what was lit so it can be handed back on close.
                        if (_previouslySelectedTab == null && IsTabLit(tab)) _previouslySelectedTab = tab;

                        StyleNavTab(tab, false);
                    }
                }
                else if (_previouslySelectedTab != null)
                {
                    RestoreGameTab(_previouslySelectedTab);
                    _previouslySelectedTab = null;
                }

                StyleNavTab(ours.transform, oursSelected);
            }
            catch
            {
            }
        }

        // The nav-bar highlight is driven by a UIAlphaFade component that runs EVERY frame.
        // Calling FadeInInstant/FadeOutInstant works for exactly one frame and is then
        // undone by that component's own Update — which is why the highlight appeared once
        // at startup and never again.
        //
        // So: disable the fade driver while we own the highlight, write the alpha directly,
        // and re-enable it when handing control back.
        /// Deterministic highlight control.
        ///
        /// History, because this took far too many attempts: SetActive on SelectedUI did
        /// nothing (the visual is alpha driven); FadeInInstant/FadeOutInstant held for one
        /// frame (UIAlphaFade.Update overwrote it); ToggleSelected() worked for turning ON
        /// but the component re-selected itself and would not stay off.
        ///
        /// So we stop negotiating: disable the fade driver on the object we are controlling
        /// and write the CanvasGroup alpha ourselves, every frame, both directions. The
        /// driver is restored when a game tab is handed back.
        private void StyleNavTab(Transform tab, bool selected)
        {
            try
            {
                var sel = FindChildDeep(tab, "SelectedUI");
                if (sel == null) return;

                sel.gameObject.SetActive(true);

                var driver = sel.GetComponent<Il2CppBAPBAP.UI.UIAlphaFade>();
                if (driver != null && driver.enabled) driver.enabled = false;

                var group = sel.GetComponent<CanvasGroup>();
                if (group == null) group = sel.gameObject.AddComponent<CanvasGroup>();
                group.alpha = selected ? 1f : 0f;

                var holder = FindChildDeep(tab, "SelectBarHolder");
                if (holder != null) holder.gameObject.SetActive(selected);
            }
            catch (Exception ex)
            {
                _log?.Warning($"tab styling failed: {ex.Message}");
            }
        }

        /// Gives a game tab back: restore its alpha and let its own fade own it again.
        private void RestoreGameTab(Transform tab)
        {
            try
            {
                var sel = FindChildDeep(tab, "SelectedUI");
                if (sel == null) return;

                var group = sel.GetComponent<CanvasGroup>();
                if (group != null) group.alpha = 1f;

                var driver = sel.GetComponent<Il2CppBAPBAP.UI.UIAlphaFade>();
                if (driver != null) driver.enabled = true;

                var holder = FindChildDeep(tab, "SelectBarHolder");
                if (holder != null) holder.gameObject.SetActive(true);
            }
            catch
            {
            }
        }

        private void ReleaseNavTab(Transform tab, bool selected) => RestoreGameTab(tab);

        private static bool IsTabLit(Transform tab)
        {
            try
            {
                var sel = FindChildDeep(tab, "SelectedUI");
                if (sel == null) return false;
                var cg = sel.GetComponent<CanvasGroup>();
                return cg != null && cg.alpha > 0.5f;
            }
            catch
            {
                return false;
            }
        }

        private static Transform FindChildDeep(Transform root, string name)
        {
            for (int i = 0; i < root.childCount; i++)
            {
                var c = root.GetChild(i);
                if (c.name == name) return c;
                var deeper = FindChildDeep(c, name);
                if (deeper != null) return deeper;
            }
            return null;
        }

        /// True when the mouse is over one of the game's nav-bar tab buttons (not ours).
        private bool PointerOverNavTab()
        {
            try
            {
                var row = NativeTab.GameTabRow;
                if (row == null) return false;

                var ours = NativeTab.Button;

                for (int i = 0; i < row.childCount; i++)
                {
                    var tab = row.GetChild(i);

                    // Skip our own button AND everything under it. Not excluding its
                    // children meant a click on MODS registered as a click on a nav tab,
                    // which closed the page in the same frame it opened — so the button
                    // appeared to do nothing at all.
                    if (ours != null && (tab.gameObject == ours || tab.IsChildOf(ours.transform)))
                        continue;

                    var rt = tab.GetComponent<RectTransform>();
                    if (rt == null || !tab.gameObject.activeInHierarchy) continue;

                    if (RectTransformUtility.RectangleContainsScreenPoint(rt, Input.mousePosition, null))
                        return true;
                }
            }
            catch
            {
            }
            return false;
        }

        /// Safety net for closing their page: after ours goes away, make sure the lobby is
        /// showing SOMETHING. If no game page is open we ask the game to return to Play,
        /// which is a state it always considers valid — so the lobby can never be left blank.
        private void RestoreGamePage()
        {
            try
            {
                if (_tabGroup == null)
                {
                    _tabGroup = UnityEngine.Object.FindObjectOfType<Il2CppBAPBAP.UI.UILobbyTabGroup>();
                }
                if (_tabGroup == null) return;

                var pages = new Il2CppBAPBAP.UI.UILobbyTabPage[]
                {
                    _tabGroup.PlayPage,
                    _tabGroup.LockerPage,
                    _tabGroup.RankingsPage,
                    _tabGroup.ShopPage,
                    _tabGroup.ProfilePage
                };

                for (int i = 0; i < pages.Length; i++)
                {
                    if (pages[i] != null && _tabGroup.IsPageOpened(pages[i]))
                    {
                        _log?.Msg("lobby page already open after closing mods page.");
                        return;
                    }
                }

                _log?.Msg("no lobby page open after closing mods page — returning to Play.");
                _tabGroup.CloseAllPagesAndOpenPlayTab();
            }
            catch (Exception ex)
            {
                _log?.Warning($"could not restore a lobby page: {ex.Message}");
            }
        }

        private void CloseGamePage()
        {
            try
            {
                if (_tabGroup == null)
                {
                    if (Time.unscaledTime - _lastTabGroupLookup < 5f) return;
                    _lastTabGroupLookup = Time.unscaledTime;
                    _tabGroup = UnityEngine.Object.FindObjectOfType<Il2CppBAPBAP.UI.UILobbyTabGroup>();
                }
                if (_tabGroup == null) return;

                var pages = new Il2CppBAPBAP.UI.UILobbyTabPage[]
                {
                    _tabGroup.PlayPage,
                    _tabGroup.LockerPage,
                    _tabGroup.RankingsPage,
                    _tabGroup.ShopPage,
                    _tabGroup.ProfilePage
                };

                int closed = 0;
                for (int i = 0; i < pages.Length; i++)
                {
                    var page = pages[i];
                    if (page == null) continue;
                    if (_tabGroup.IsPageOpened(page))
                    {
                        _tabGroup.ClosePage(page);
                        closed++;
                    }
                }
                _log?.Msg($"mods page opened — closed {closed} lobby page(s) via the game's own ClosePage.");
            }
            catch
            {
                _tabGroup = null;
            }
        }

        // UIAlphaFade / UIPosLerpFade drive the open/close animation. Left enabled on a page
        // the game does not know about, they will happily fade our page back out.
        private void DisableFadeDrivers()
        {
            string[] drivers =
            {
                "Il2CppBAPBAP.UI.UIAlphaFade",
                "Il2CppBAPBAP.UI.UIPosLerpFade",
                "Il2CppUIAlphaFade",
                "Il2CppUIPosLerpFade"
            };

            foreach (var name in drivers)
            {
                var type = ResolveType(name, "Assembly-CSharp");
                if (type == null) continue;

                var comp = _page.GetComponent(type);
                var behaviour = comp != null ? comp.TryCast<Behaviour>() : null;
                if (behaviour != null) behaviour.enabled = false;
            }
        }

        /// Clears and rebuilds the page for the current view.
        private void Rebuild()
        {
            if (_contentRoot == null) return;

            _refreshers.Clear();
            for (int i = _contentRoot.childCount - 1; i >= 0; i--)
                UnityEngine.Object.DestroyImmediate(_contentRoot.GetChild(i).gameObject);

            if (_viewMod != null) PopulateSettings(_viewMod);
            else if (_browseMode) PopulateBrowse();
            else Populate(_entries, _onToggle);
        }

        /// One mod's settings, with a Back button — same shape as the settings-menu tab.
        private void PopulateSettings(ModEntry mod)
        {
            var back = new GameObject("BackRow");
            back.transform.SetParent(_contentRoot, false);
            back.AddComponent<RectTransform>();
            var bhl = back.AddComponent<HorizontalLayoutGroup>();
            bhl.childControlWidth = true;
            bhl.childControlHeight = true;
            var ble = back.AddComponent<LayoutElement>();
            ble.minHeight = 44f;
            ble.preferredHeight = 44f;

            var bimg = back.AddComponent<Image>();
            bimg.color = Palette.Row;
            var bbtn = back.AddComponent<Button>();
            bbtn.targetGraphic = bimg;
            bbtn.onClick.AddListener(new Action(() => { _viewMod = null; Rebuild(); }));

            var backLabel = AddRowLabel(back.transform, "<  Back to mods");
            if (backLabel != null) backLabel.fontSize = 20f;

            AddLabel(mod.DisplayName.ToUpperInvariant(), 24f);

            var settings = ModSettings.For(mod);
            if (settings.Count == 0)
            {
                AddLabel("This mod exposes no settings.", 19f);
                return;
            }

            foreach (var s in settings) AddSettingRow(s);
        }

        private void AddSettingRow(SettingDescriptor s)
        {
            var row = new GameObject($"Set_{s.Key}");
            row.transform.SetParent(_contentRoot, false);
            row.AddComponent<RectTransform>();

            var hlg = row.AddComponent<HorizontalLayoutGroup>();
            hlg.childControlWidth = true;
            hlg.childControlHeight = true;
            hlg.childForceExpandWidth = false;
            hlg.childAlignment = TextAnchor.MiddleLeft;
            hlg.spacing = 10f;

            var le = row.AddComponent<LayoutElement>();
            le.minHeight = 38f;
            le.preferredHeight = 38f;

            var label = AddRowLabel(row.transform, s.Label);
            if (label != null)
            {
                label.fontSize = 19f;
                label.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
            }

            if (s.Kind == SettingKind.Text)
            {
                var v = AddRowLabel(row.transform, s.RawValue);
                if (v != null)
                {
                    v.fontSize = 19f;
                    v.alignment = TextAlignmentOptions.MidlineRight;
                    v.gameObject.AddComponent<LayoutElement>().preferredWidth = 220f;
                }
                return;
            }

            if (s.Kind == SettingKind.Bool)
            {
                bool state = IniStore.ReadBool(s.IniFile, s.Key, false);
                TextMeshProUGUI stateLabel = null;
                var btn = MakeButton(row.transform, state ? "ON" : "OFF", 90f, null);
                stateLabel = btn.Item2;
                btn.Item1.onClick.AddListener(new Action(() =>
                {
                    state = !state;
                    IniStore.WriteBool(s.IniFile, s.Key, state);
                    if (stateLabel != null) stateLabel.text = state ? "ON" : "OFF";
                }));
                return;
            }

            float value = IniStore.ReadFloat(s.IniFile, s.Key, 0f);
            var number = AddRowLabel(row.transform, value.ToString("0.##"));
            if (number != null)
            {
                number.fontSize = 19f;
                number.alignment = TextAlignmentOptions.MidlineRight;
                number.gameObject.AddComponent<LayoutElement>().preferredWidth = 80f;
            }

            var minus = MakeButton(row.transform, "-", 44f, null);
            minus.Item1.onClick.AddListener(new Action(() =>
            {
                value = Mathf.Clamp(value - s.Step, s.Min, s.Max);
                IniStore.WriteFloat(s.IniFile, s.Key, value);
                if (number != null) number.text = value.ToString("0.##");
            }));

            var plus = MakeButton(row.transform, "+", 44f, null);
            plus.Item1.onClick.AddListener(new Action(() =>
            {
                value = Mathf.Clamp(value + s.Step, s.Min, s.Max);
                IniStore.WriteFloat(s.IniFile, s.Key, value);
                if (number != null) number.text = value.ToString("0.##");
            }));
        }

        public void RefreshRows()
        {
            foreach (var r in _refreshers) { try { r(); } catch { } }
        }

        // ---- construction helpers -------------------------------------------------

        private Transform FindPageTemplate()
        {
            // Rankings is a good donor: a plain content page with no live match data.
            var type = ResolveType("Il2CppBAPBAP.UI.UILobbyRankingsTabPage", "Assembly-CSharp")
                       ?? ResolveType("Il2CppBAPBAP.UI.UILobbyProfileTabPage", "Assembly-CSharp");
            if (type == null) return null;

            var objs = Resources.FindObjectsOfTypeAll(type);
            for (int i = 0; i < objs.Length; i++)
            {
                var comp = objs[i].TryCast<Component>();
                if (comp == null) continue;
                var go = comp.gameObject;
                if (go == null || !go.scene.IsValid()) continue;
                if (go.name == PageName) continue;

                _pagesContainer = go.transform.parent;
                return go.transform;
            }
            return null;
        }

        // InputToggle is the game's styled switch; any lobby TMP label works as a text donor.
        private bool LocateTemplates()
        {
            if (_toggleTemplate != null && _textTemplate != null) return true;

            var toggles = Resources.FindObjectsOfTypeAll(Il2CppType.Of<Toggle>());
            for (int i = 0; i < toggles.Length; i++)
            {
                var t = toggles[i].TryCast<Toggle>();
                if (t == null) continue;
                var go = t.gameObject;
                if (go == null || !go.scene.IsValid()) continue;

                string n = go.name.ToLowerInvariant();

                // InputToggle turned out to be the VOICE INPUT toggle — it carries
                // microphone sprites. Reject anything audio/voice/terms related.
                if (n.Contains("input") || n.Contains("mute") || n.Contains("mic") ||
                    n.Contains("voice") || n.Contains("latency") || n.Contains("terms"))
                    continue;

                // Prefer a plain settings checkbox.
                if (n.Contains("samechars")) { _toggleTemplate = go; break; }
                if (_toggleTemplate == null) _toggleTemplate = go;
            }

            var texts = Resources.FindObjectsOfTypeAll(Il2CppType.Of<TextMeshProUGUI>());
            for (int i = 0; i < texts.Length; i++)
            {
                var t = texts[i].TryCast<TextMeshProUGUI>();
                if (t == null) continue;
                var go = t.gameObject;
                if (go == null || !go.scene.IsValid()) continue;
                _textTemplate = go;
                break;
            }

            if (_toggleTemplate != null)
                _log?.Msg($"toggle template = '{_toggleTemplate.name}'.");
            else
                _log?.Warning("no Toggle template found.");
            if (_textTemplate == null) _log?.Warning("no TMP text template found.");

            // Only the text donor is essential now; rows build their own controls.
            return _textTemplate != null;
        }

        private void StripPageScripts(GameObject page)
        {
            // Remove the donor's page script so the game's tab logic never drives our clone.
            string[] scripts =
            {
                "Il2CppBAPBAP.UI.UILobbyRankingsTabPage",
                "Il2CppBAPBAP.UI.UILobbyProfileTabPage",
                "Il2CppBAPBAP.UI.UILobbyTabPage"
            };

            foreach (var name in scripts)
            {
                var type = ResolveType(name, "Assembly-CSharp");
                if (type == null) continue;
                var comp = page.GetComponent(type);
                if (comp != null) UnityEngine.Object.DestroyImmediate(comp, true);
            }
        }

        private void ClearChildren(Transform t)
        {
            for (int i = t.childCount - 1; i >= 0; i--)
                UnityEngine.Object.DestroyImmediate(t.GetChild(i).gameObject);
        }

        private Transform BuildLayoutRoot(Transform page)
        {
            var go = new GameObject("Content");
            go.transform.SetParent(page, false);

            // Stretch to fill the page rather than floating a fixed-size box in the middle,
            // which is what made it read as an overlay.
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = Vector2.zero;   // edge to edge; margins left the lobby visible
            rt.offsetMax = Vector2.zero;   // around the sides, which read as an overlay

            // A page backdrop so rows sit on a surface instead of over the lobby art.
            // Deep navy to match the game's own menus, fully opaque so no lobby art bleeds
            // through (that was what made this read as an overlay).
            var bg = go.AddComponent<Image>();
            bg.color = Palette.PageBackground;

            // Scrolling, so the list keeps working as more mods are added.
            var mask = go.AddComponent<RectMask2D>();
            var scroll = go.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.scrollSensitivity = 28f;

            var vlg = go.AddComponent<VerticalLayoutGroup>();
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.spacing = 8f;
            var pad = new RectOffset();
            pad.left = 24; pad.right = 24; pad.top = 24; pad.bottom = 24;
            // Top padding clears the game's nav bar — content was rendering underneath it.
            pad.left = 150; pad.right = 150; pad.top = 150; pad.bottom = 60;
            vlg.padding = pad;

            var scrollRect = go.GetComponent<ScrollRect>();
            var inner = new GameObject("Scroll");
            inner.transform.SetParent(go.transform, false);

            var innerRt = inner.AddComponent<RectTransform>();
            innerRt.anchorMin = new Vector2(0f, 1f);
            innerRt.anchorMax = new Vector2(1f, 1f);
            innerRt.pivot = new Vector2(0.5f, 1f);

            var innerVlg = inner.AddComponent<VerticalLayoutGroup>();
            innerVlg.childControlWidth = true;
            innerVlg.childControlHeight = true;
            innerVlg.childForceExpandWidth = true;
            innerVlg.childForceExpandHeight = false;
            innerVlg.spacing = 8f;
            var innerPad = new RectOffset();
            innerPad.left = 150; innerPad.right = 150; innerPad.top = 150; innerPad.bottom = 60;
            innerVlg.padding = innerPad;

            var innerFit = inner.AddComponent<ContentSizeFitter>();
            innerFit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            if (scrollRect != null)
            {
                scrollRect.content = innerRt;
                scrollRect.viewport = go.GetComponent<RectTransform>();
            }

            // Strip the outer layout now that the scroll child owns the flow.
            UnityEngine.Object.DestroyImmediate(vlg);

            return inner.transform;
        }

        /// INSTALLED | BROWSE. The active tab is highlighted; the other is a button.
        private void AddTabStrip()
        {
            var strip = new GameObject("Tabs");
            strip.transform.SetParent(_contentRoot, false);
            var le = strip.AddComponent<LayoutElement>();
            le.minHeight = 44f;
            le.preferredHeight = 44f;

            var layout = strip.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 10f;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;
            layout.childAlignment = TextAnchor.MiddleLeft;

            AddTabButton(strip.transform, "INSTALLED", !_browseMode, () => SetBrowseMode(false));
            AddTabButton(strip.transform, "BROWSE", _browseMode, () => SetBrowseMode(true));
        }

        private void AddTabButton(Transform parent, string text, bool active, Action onClick)
        {
            var pair = MakeButton(parent, text, 170f, active ? (Action)null : onClick);
            if (pair == null) return;

            var img = pair.Item1 != null ? pair.Item1.GetComponent<Image>() : null;
            if (img != null) img.color = active ? Palette.Highlight : Palette.Row;
            if (pair.Item2 != null)
                pair.Item2.color = active ? Palette.PageBackground : Palette.TextMuted;
        }

        internal void SetBrowseMode(bool browse)
        {
            if (_browseMode == browse) return;
            _browseMode = browse;
            if (browse) Browse.Load();
            Rebuild();
        }

        private void Populate(List<ModEntry> entries, Func<ModEntry, bool, bool> onToggle)
        {
            var title = AddLabel("MODS", 40f);
            if (title != null) title.color = Palette.Highlight;
            AddTabStrip();
            AddLabel("", 8f);

            var h1 = AddLabel("HOST-ONLY — affects everyone in your lobby", 22f);
            if (h1 != null) h1.color = Palette.Highlight;
            foreach (var e in entries)
                if (e.Category == ModCategory.HostOnly) AddRow(e, onToggle);

            AddLabel("", 12f);
            var h2 = AddLabel("CLIENT-SIDE — affects only you", 22f);
            if (h2 != null) h2.color = Palette.Highlight;
            foreach (var e in entries)
                if (e.Category == ModCategory.ClientSide) AddRow(e, onToggle);

            // Discovered mods we have no metadata for. Shown and toggleable, but flagged:
            // a DLL cannot tell us whether it affects other players.
            bool anyUnknown = false;
            foreach (var e in entries) if (e.Category == ModCategory.Unknown) { anyUnknown = true; break; }
            if (anyUnknown)
            {
                AddLabel("", 12f);
                var h3 = AddLabel("UNRECOGNISED — scope unknown, may affect your lobby", 22f);
                if (h3 != null) h3.color = Palette.Highlight;
                foreach (var e in entries)
                    if (e.Category == ModCategory.Unknown) AddRow(e, onToggle);
            }
        }

        // ---- browse tab ------------------------------------------------------------

        private void PopulateBrowse()
        {
            var title = AddLabel("MODS", 40f);
            if (title != null) title.color = Palette.Highlight;
            AddTabStrip();
            AddLabel("", 8f);

            switch (Browse.Screen)
            {
                case BrowseScreen.Confirm: PopulateConfirm(); return;
                case BrowseScreen.Busy:    PopulateBusy();    return;
            }

            if (Browse.Loading)
            {
                var loading = AddLabel("Loading catalog...", 24f);
                if (loading != null) loading.color = Palette.TextMuted;
                return;
            }

            if (!string.IsNullOrEmpty(Browse.Error))
            {
                var err = AddLabel(Browse.Error, 22f);
                if (err != null) err.color = Palette.Highlight;
                AddLabel("", 8f);
                var retry = MakeButton(_contentRoot, "RETRY", 170f, () => Browse.Load(true));
                if (retry != null && retry.Item1 != null)
                {
                    var ri = retry.Item1.GetComponent<Image>();
                    if (ri != null) ri.color = Palette.Accent;
                }
                return;
            }

            if (Browse.Packages == null || Browse.Packages.Count == 0)
            {
                var empty = AddLabel("No mods available.", 24f);
                if (empty != null) empty.color = Palette.TextMuted;
                return;
            }

            if (!string.IsNullOrEmpty(Browse.Status))
            {
                var status = AddLabel(Browse.Status, 20f);
                if (status != null) status.color = Palette.TextMuted;
            }
            AddLabel("", 6f);

            foreach (var package in Browse.Packages) AddBrowseRow(package);

            foreach (string note in Browse.Notes)
            {
                var n = AddLabel(note, 16f);
                if (n != null) n.color = Palette.TextMuted;
            }
        }

        private void AddBrowseRow(CatalogPackage package)
        {
            var row = new GameObject($"Browse_{package.Id}");
            row.transform.SetParent(_contentRoot, false);

            var le = row.AddComponent<LayoutElement>();
            le.minHeight = 62f;
            le.preferredHeight = 62f;

            var img = row.AddComponent<Image>();
            img.color = Browse.IsInstalled(package) ? Palette.RowEnabled : Palette.Row;

            var layout = row.AddComponent<HorizontalLayoutGroup>();
            var pad = new RectOffset();
            pad.left = 14; pad.right = 14; pad.top = 6; pad.bottom = 6;
            layout.padding = pad;
            layout.spacing = 12f;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = true;
            layout.childAlignment = TextAnchor.MiddleLeft;

            string version = string.IsNullOrEmpty(package.LatestVersion) ? "" : "  v" + package.LatestVersion;
            var label = AddRowLabel(row.transform, package.Name + version);
            if (label != null)
            {
                label.fontSize = 22f;
                var fit = label.gameObject.GetComponent<LayoutElement>();
                if (fit == null) fit = label.gameObject.AddComponent<LayoutElement>();
                fit.flexibleWidth = 1f;
            }

            // Source and scope, so it is obvious where a mod came from and who it affects.
            string source = string.IsNullOrEmpty(package.SourceDisplayName) ? "" : package.SourceDisplayName;
            var meta = AddRowLabel(row.transform, source + "  -  " + Browse.ScopeLabel(package));
            if (meta != null)
            {
                meta.fontSize = 16f;
                meta.color = package.ScopeKnown && package.Scope == ModCategory.HostOnly
                    ? Palette.Highlight : Palette.TextMuted;
                var ml = meta.gameObject.GetComponent<LayoutElement>();
                if (ml == null) ml = meta.gameObject.AddComponent<LayoutElement>();
                ml.preferredWidth = 280f;
            }

            // Anything the catalog flagged. Rendered from requirements[], never hardcoded.
            var warnings = BrowseTab.WarningsFor(package);
            if (warnings.Count > 0)
            {
                var warn = AddRowLabel(row.transform, warnings.Count == 1 ? "!" : $"! {warnings.Count}");
                if (warn != null)
                {
                    warn.fontSize = 20f;
                    warn.color = Palette.Highlight;
                    var wl = warn.gameObject.GetComponent<LayoutElement>();
                    if (wl == null) wl = warn.gameObject.AddComponent<LayoutElement>();
                    wl.preferredWidth = 40f;
                }
            }

            string action = Browse.ActionLabel(package);
            var button = MakeButton(row.transform, action, 130f, () =>
            {
                if (action == "REMOVE") Browse.Uninstall(package);
                else Browse.BeginInstall(package);
            });

            if (button != null && button.Item1 != null)
            {
                var bi = button.Item1.GetComponent<Image>();
                if (bi != null)
                    bi.color = action == "REMOVE" ? Palette.RowDisabled : Palette.Accent;
            }
        }

        /// What you are about to install, stated plainly, with every warning visible before
        /// anything is downloaded.
        private void PopulateConfirm()
        {
            var package = Browse.Pending;
            var version = Browse.PendingVersion;
            if (package == null || version == null) { Browse.CancelConfirm(); return; }

            var name = AddLabel(package.Name, 30f);
            if (name != null) name.color = Palette.TextPrimary;

            var sub = AddLabel($"version {version.Version}  -  from {package.SourceDisplayName}", 20f);
            if (sub != null) sub.color = Palette.TextMuted;
            AddLabel("", 6f);

            if (!string.IsNullOrEmpty(package.Summary))
            {
                var summary = AddLabel(package.Summary, 20f);
                if (summary != null) summary.color = Palette.TextPrimary;
                AddLabel("", 6f);
            }

            var scope = AddLabel(Browse.ScopeLabel(package).ToUpperInvariant(), 22f);
            if (scope != null)
                scope.color = package.ScopeKnown && package.Scope == ModCategory.ClientSide
                    ? Palette.TextMuted : Palette.Highlight;

            var warnings = BrowseTab.WarningsFor(package);
            if (warnings.Count > 0)
            {
                AddLabel("", 8f);
                foreach (var requirement in warnings)
                {
                    var w = AddLabel("!  " + requirement.Text, 20f);
                    if (w != null)
                        w.color = requirement.IsBlocking ? Palette.Highlight : Palette.TextPrimary;
                }
            }

            AddLabel("", 8f);
            var files = AddLabel($"writes {version.Files.Count} file(s):", 18f);
            if (files != null) files.color = Palette.TextMuted;
            foreach (var file in version.Files)
            {
                var f = AddLabel("   " + file.TargetPath, 17f);
                if (f != null) f.color = Palette.TextMuted;
            }

            AddLabel("", 12f);

            var actions = new GameObject("ConfirmActions");
            actions.transform.SetParent(_contentRoot, false);
            var ale = actions.AddComponent<LayoutElement>();
            ale.minHeight = 44f;
            ale.preferredHeight = 44f;
            var alayout = actions.AddComponent<HorizontalLayoutGroup>();
            alayout.spacing = 12f;
            alayout.childForceExpandWidth = false;
            alayout.childAlignment = TextAnchor.MiddleLeft;

            var install = MakeButton(actions.transform, "INSTALL", 190f, () => Browse.ConfirmInstall());
            if (install != null && install.Item1 != null)
            {
                var ii = install.Item1.GetComponent<Image>();
                if (ii != null) ii.color = Palette.Accent;
            }

            var cancel = MakeButton(actions.transform, "CANCEL", 150f, () => Browse.CancelConfirm());
            if (cancel != null && cancel.Item1 != null)
            {
                var ci = cancel.Item1.GetComponent<Image>();
                if (ci != null) ci.color = Palette.RowDisabled;
            }
        }

        private void PopulateBusy()
        {
            var status = AddLabel(string.IsNullOrEmpty(Browse.Status) ? "Working..." : Browse.Status, 24f);
            if (status != null) status.color = Palette.TextPrimary;

            var hint = AddLabel("Downloads are verified before anything is written.", 18f);
            if (hint != null) hint.color = Palette.TextMuted;
        }

        internal TextMeshProUGUI AddLabel(string text, float size)
        {
            var go = UnityEngine.Object.Instantiate(_textTemplate, _contentRoot, false);
            go.name = $"Label_{text}";
            go.SetActive(true);

            ClearChildren(go.transform); // donor labels sometimes carry icons

            var tmp = go.GetComponent<TextMeshProUGUI>();
            if (tmp != null)
            {
                tmp.text = text;
                tmp.fontSize = size;
                tmp.color = Palette.TextPrimary;
                tmp.enableAutoSizing = false;
            }

            var le = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
            le.minHeight = size + 10f;
            le.preferredHeight = size + 10f;
            le.ignoreLayout = false;

            return tmp;
        }

        private void AddRow(ModEntry entry, Func<ModEntry, bool, bool> onToggle)
        {
            var row = new GameObject($"Row_{entry.Id}");
            row.transform.SetParent(_contentRoot, false);
            row.AddComponent<RectTransform>();

            // The whole row is the control. Borrowed toggle widgets proved unreliable —
            // one donor came with microphone art, another had no graphics at all — so the
            // row draws its own surface and uses the game's font for the state text.
            var bg = row.AddComponent<Image>();
            bg.color = Palette.Row;

            var button = row.AddComponent<Button>();
            button.targetGraphic = bg;

            var colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.15f, 1.15f, 1.25f, 1f);
            colors.pressedColor = new Color(0.85f, 0.9f, 1f, 1f);
            colors.disabledColor = new Color(0.5f, 0.5f, 0.55f, 0.6f);
            button.colors = colors;

            var hlg = row.AddComponent<HorizontalLayoutGroup>();
            hlg.childForceExpandWidth = true;
            hlg.childForceExpandHeight = true;
            hlg.childControlWidth = true;
            hlg.childControlHeight = true;
            hlg.spacing = 14f;
            var pad = new RectOffset();
            pad.left = 16; pad.right = 16; pad.top = 4; pad.bottom = 4;
            hlg.padding = pad;

            var rowLe = row.AddComponent<LayoutElement>();
            rowLe.minHeight = 52f;
            rowLe.preferredHeight = 52f;

            string restartTag = entry.RequiresRestart ? "   [restart]" : "";
            var label = AddRowLabel(row.transform, RowText(entry, restartTag));

            var captured = entry;
            button.onClick.AddListener(new Action(() =>
            {
                bool target = !captured.Enabled;
                onToggle(captured, target);
                RefreshRows();
            }));

            // A config button, only for mods that actually expose settings.
            if (entry.HasOptions)
            {
                AddOptionsButton(row.transform, entry);
            }

            _refreshers.Add(() =>
            {
                bool locked = captured.Category == ModCategory.HostOnly && MatchState.HostEditingLocked;
                bool usable = captured.Installed && !locked;

                button.interactable = usable;

                if (label != null)
                {
                    label.text = RowText(captured, restartTag);
                    label.color = !usable ? Palette.TextMuted
                                : captured.Enabled ? Palette.TextPrimary
                                : Palette.TextMuted;
                }

                bg.color = !usable ? Palette.RowDisabled
                         : captured.Enabled ? Palette.RowEnabled
                         : Palette.Row;
            });
        }

        // Sprite-independent state text, so a donor toggle with odd art can never leave the
        // user guessing whether something is on.
        private static string RowText(ModEntry e, string restartTag)
        {
            string state = e.Enabled ? "ON " : "OFF";
            string version = string.IsNullOrEmpty(e.Version) ? "" : $" v{e.Version}";
            string options = e.HasOptions ? "   [has options]" : "";
            return $"[{state}]   {e.DisplayName}{version}   ({e.SourceLabel}){restartTag}{options}";
        }

        private readonly Dictionary<string, TextMeshProUGUI> _rowLabels = new Dictionary<string, TextMeshProUGUI>();

        /// Small "Config" button on the right of a row. Settings live in the game's
        /// SETTINGS > Mods tab, which has proper scrolling and clipping; duplicating that
        /// editor inline here produced a panel that ran off the bottom of the screen.
        private void AddOptionsButton(Transform row, ModEntry entry)
        {
            var go = new GameObject("Config");
            go.transform.SetParent(row, false);
            go.AddComponent<RectTransform>();

            var le = go.AddComponent<LayoutElement>();
            le.preferredWidth = 110f;
            le.minHeight = 36f;

            var img = go.AddComponent<Image>();
            img.color = Palette.Accent;

            var button = go.AddComponent<Button>();
            button.targetGraphic = img;

            var label = AddRowLabel(go.transform, "Config");
            if (label != null)
            {
                var rt = label.GetComponent<RectTransform>();
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.offsetMin = Vector2.zero;
                rt.offsetMax = Vector2.zero;
                label.alignment = TextAlignmentOptions.Center;
                label.fontSize = 17f;
            }

            var captured = entry;
            button.onClick.AddListener(new Action(() =>
            {
                _viewMod = captured;
                Rebuild();
            }));
        }

        /// Raised when a Config button is pressed, so the host mod can route it.
        public Action<ModEntry> OnConfigRequested;

        /// Small button; returns it with its label so callers can update the text later.
        internal Tuple<Button, TextMeshProUGUI> MakeButton(Transform parent, string text, float width, Action onClick)
        {
            var go = new GameObject($"Btn_{text}");
            go.transform.SetParent(parent, false);
            go.AddComponent<RectTransform>();

            var le = go.AddComponent<LayoutElement>();
            le.preferredWidth = width;
            le.minHeight = 32f;

            var img = go.AddComponent<Image>();
            img.color = Palette.Accent;

            var btn = go.AddComponent<Button>();
            btn.targetGraphic = img;
            if (onClick != null) btn.onClick.AddListener(onClick);

            var label = AddRowLabel(go.transform, text);
            if (label != null)
            {
                var rt = label.GetComponent<RectTransform>();
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.offsetMin = Vector2.zero;
                rt.offsetMax = Vector2.zero;
                label.alignment = TextAlignmentOptions.Center;
                label.fontSize = 18f;
            }

            return new Tuple<Button, TextMeshProUGUI>(btn, label);
        }

        internal TextMeshProUGUI AddRowLabel(Transform parent, string text)
        {
            var go = UnityEngine.Object.Instantiate(_textTemplate, parent, false);
            go.name = "Label";
            go.SetActive(true);
            ClearChildren(go.transform);

            var tmp = go.GetComponent<TextMeshProUGUI>();
            if (tmp != null)
            {
                tmp.text = text;
                tmp.fontSize = 22f;
                tmp.enableAutoSizing = false;
            }
            return tmp;
        }

        private static Il2CppSystem.Type ResolveType(string fullName, string assembly)
        {
            try
            {
                var t = Type.GetType($"{fullName}, {assembly}");
                return t == null ? null : Il2CppType.From(t, false);
            }
            catch { return null; }
        }
    }
}
