// The "Mods" tab inside the game's SETTINGS window.
//
// Two views:
//   LIST   — every installed mod that exposes settings (discovered, never hardcoded)
//   DETAIL — one mod's settings, with a Back button
//
// Layout rules learned the hard way:
//   * Parent to the scroll VIEWPORT, not the content object. The content object carries a
//     ContentSizeFitter and grows unbounded, so anchoring to it spills outside the window.
//   * Add a RectMask2D so nothing can draw outside the settings panel.
//   * Use a ScrollRect — a mod like Hidden Dev Arguments exposes 60+ settings.
//   * Never clone the game's panels: they are faded (alpha 0), not deactivated, so a clone
//     inherits both the zero alpha and the fade drivers that restore it.
//   * Never deactivate the game's panels either: their tab controller still believes its
//     tab is selected and will not re-activate it, leaving the whole window blank.

using System;
using System.Collections.Generic;
using MelonLoader;
using UnityEngine;
using UnityEngine.UI;
using Il2CppInterop.Runtime;
using Il2CppTMPro;

namespace BapbapMods.Manager
{
    public class SettingsTab
    {
        private const string TabName = "Tab_Mods";
        private const string PanelName = "Contents_Mods";

        // Compact by design: the first attempt used 52px rows and ran off the screen.
        private const float RowHeight = 30f;
        private const float FontSize = 16f;
        private const float HeaderFontSize = 19f;

        private MelonLogger.Instance _log;
        private Func<List<ModEntry>> _modSource;

        private GameObject _tabButton;
        private GameObject _panel;
        private RectTransform _scrollContent;
        private GameObject _textDonor;

        private Transform _contentParent;

        /// True when the game's settings window is on screen. Our full-bleed page must not be
        /// visible at the same time: it renders over the lobby, so with the settings window up
        /// the two overlap and the settings panel looks broken.
        public bool IsWindowOpen()
        {
            try
            {
                return _contentParent != null &&
                       _contentParent.gameObject != null &&
                       _contentParent.gameObject.activeInHierarchy;
            }
            catch { return false; }
        }

        /// Key-triggered dump of what the settings window actually looks like right now.
        /// Written because two hypotheses about why the Mods panel renders empty were both
        /// wrong; guessing a third time is more expensive than measuring once.
        public void DumpState(MelonLogger.Instance log)
        {
            if (log == null) return;
            log.Msg("---- settings dump ----");

            try
            {
                log.Msg($"contentParent={(_contentParent == null ? "NULL" : _contentParent.name)} " +
                        $"active={(_contentParent != null && _contentParent.gameObject.activeInHierarchy)}");

                if (_contentParent != null)
                {
                    for (int i = 0; i < _contentParent.childCount; i++)
                    {
                        var child = _contentParent.GetChild(i);
                        var cg = child.GetComponent<CanvasGroup>();
                        log.Msg($"  panel[{i}] '{child.name}' active={child.gameObject.activeSelf}" +
                                $"/{child.gameObject.activeInHierarchy} children={child.childCount}" +
                                $" alpha={(cg == null ? "n/a" : cg.alpha.ToString("0.00"))}");
                    }
                }

                log.Msg($"our panel={(_panel == null ? "NULL" : _panel.name)}");
                if (_panel != null)
                {
                    var cg = _panel.GetComponent<CanvasGroup>();
                    var rt = _panel.GetComponent<RectTransform>();
                    log.Msg($"  active={_panel.activeSelf}/{_panel.activeInHierarchy}" +
                            $" children={_panel.transform.childCount}" +
                            $" alpha={(cg == null ? "n/a" : cg.alpha.ToString("0.00"))}" +
                            $" size={(rt == null ? "n/a" : rt.rect.size.ToString())}" +
                            $" pos={(rt == null ? "n/a" : rt.anchoredPosition.ToString())}");

                    for (int i = 0; i < _panel.transform.childCount && i < 6; i++)
                    {
                        var child = _panel.transform.GetChild(i);
                        var childRt = child.GetComponent<RectTransform>();
                        log.Msg($"    child[{i}] '{child.name}' active={child.gameObject.activeSelf}" +
                                $" size={(childRt == null ? "n/a" : childRt.rect.size.ToString())}");
                    }

                    var canvas = _panel.GetComponentInParent<Canvas>();
                    log.Msg($"  canvas={(canvas == null ? "NULL" : canvas.name + " order=" + canvas.sortingOrder)}");
                }
            }
            catch (Exception ex)
            {
                log.Error($"settings dump failed: {ex}");
            }

            log.Msg("---- end settings dump ----");
        }
        private readonly List<GameObject> _otherPanels = new List<GameObject>();
        private GameObject _panelOnOpen;

        /// Panels we faded out while our tab is showing, and the alpha to give back.
        private readonly List<KeyValuePair<CanvasGroup, float>> _fadedPanels =
            new List<KeyValuePair<CanvasGroup, float>>();

        private bool _built;
        private ModEntry _detailMod;   // null = list view

        // The game's own tab buttons. We strip UITabController from our clone (it has no
        // page registered for us), which also removes the selected-state visuals — so the
        // highlight has to be driven by hand.
        private readonly List<GameObject> _gameTabs = new List<GameObject>();
        private static readonly Color SelectedColor = new Color(1f, 0.85f, 0.2f, 1f);
        private static readonly Color UnselectedColor = new Color(0.62f, 0.65f, 0.72f, 1f);

        public bool Built => _built;

        public void Init(MelonLogger.Instance log, Func<List<ModEntry>> modSource)
        {
            _log = log;
            _modSource = modSource;
        }

        public void Reset()
        {
            _built = false;
            _tabButton = null;
            _panel = null;
            _scrollContent = null;
            _contentParent = null;
            _detailMod = null;
            _otherPanels.Clear();
        }

        public void TryBuild()
        {
            if (_built) return;

            try
            {
                var tabTemplate = FindInactiveByName("Tab_Controls");
                var panelTemplate = FindInactiveByName("Contents_General");
                if (tabTemplate == null || panelTemplate == null) return;

                _contentParent = panelTemplate.transform.parent;      // "Content"
                var viewport = _contentParent.parent;                 // scroll viewport
                if (viewport == null) return;

                _otherPanels.Clear();
                for (int i = 0; i < _contentParent.childCount; i++)
                {
                    _otherPanels.Add(_contentParent.GetChild(i).gameObject);
                }

                // Remember the game's tabs so their highlight can be cleared when ours opens.
                _gameTabs.Clear();
                var tabParent = tabTemplate.transform.parent;
                for (int i = 0; i < tabParent.childCount; i++)
                {
                    var child = tabParent.GetChild(i).gameObject;
                    if (child.name.StartsWith("Tab_") && child.name != TabName) _gameTabs.Add(child);
                }

                BuildTabButton(tabTemplate);
                BuildPanel(viewport);

                _built = true;
                _log?.Msg("Mods settings tab ready (settings discovered per mod).");
            }
            catch (Exception ex)
            {
                _log?.Warning($"Settings tab build failed: {ex.Message}");
            }
        }

        private void BuildTabButton(GameObject template)
        {
            _tabButton = UnityEngine.Object.Instantiate(template, template.transform.parent, false);
            _tabButton.name = TabName;
            _tabButton.SetActive(true);
            _tabButton.transform.SetSiblingIndex(template.transform.GetSiblingIndex() + 1);

            var controllerType = ResolveType("Il2CppBAPBAP.UI.UITabController", "Assembly-CSharp");
            if (controllerType != null)
            {
                var comp = _tabButton.GetComponent(controllerType);
                if (comp != null) UnityEngine.Object.DestroyImmediate(comp, true);
            }

            var label = _tabButton.GetComponentInChildren<TextMeshProUGUI>(true);
            if (label != null) label.text = "Mods";

            var button = _tabButton.GetComponent<Button>() ?? _tabButton.AddComponent<Button>();
            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(new Action(Open));
        }

        private void BuildPanel(Transform viewport)
        {
            _panel = new GameObject(PanelName);
            _panel.transform.SetParent(viewport, false);

            var rt = _panel.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;

            var bg = _panel.AddComponent<Image>();
            bg.color = new Color(0.07f, 0.08f, 0.11f, 1f);

            _panel.AddComponent<RectMask2D>();   // hard clip to the settings window

            var scroll = _panel.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.scrollSensitivity = 24f;

            var contentGo = new GameObject("Content");
            contentGo.transform.SetParent(_panel.transform, false);

            _scrollContent = contentGo.AddComponent<RectTransform>();
            _scrollContent.anchorMin = new Vector2(0f, 1f);
            _scrollContent.anchorMax = new Vector2(1f, 1f);
            _scrollContent.pivot = new Vector2(0.5f, 1f);
            _scrollContent.offsetMin = Vector2.zero;
            _scrollContent.offsetMax = Vector2.zero;

            var vlg = contentGo.AddComponent<VerticalLayoutGroup>();
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.spacing = 3f;
            var pad = new RectOffset();
            pad.left = 18; pad.right = 18; pad.top = 14; pad.bottom = 14;
            vlg.padding = pad;

            var fitter = contentGo.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            scroll.content = _scrollContent;
            scroll.viewport = rt;

            _panel.SetActive(false);
        }

        // ---- views -----------------------------------------------------------------

        public void Open()
        {
            if (!_built) return;

            _panelOnOpen = CurrentGamePanel();
            _detailMod = null;

            _panel.SetActive(true);
            _panel.transform.SetAsLastSibling();
            HideGamePanels();

            // Take the highlight: clear every game tab, light ours up.
            for (int i = 0; i < _gameTabs.Count; i++) SetTabSelected(_gameTabs[i], false);
            SetTabSelected(_tabButton, true);

            Rebuild();
        }

        /// Mirrors the game's own selected look: yellow label, icon, and underline bar.
        ///
        /// Re-asserted every frame while our panel is open — the game re-applies its own tab
        /// styling (theme palette components), so a single write on click gets overwritten
        /// and the previously selected tab stays lit.
        private void SetTabSelected(GameObject tab, bool selected)
        {
            if (tab == null) return;

            try
            {
                var graphics = tab.GetComponentsInChildren<Graphic>(true);
                for (int i = 0; i < graphics.Length; i++)
                {
                    var g = graphics[i];
                    if (g == null) continue;

                    // Only the label and icon carry the highlight colour; the row background
                    // must keep its own look.
                    if (g.gameObject == tab) continue;

                    var tmp = g.TryCast<TextMeshProUGUI>();
                    var img = g.TryCast<Image>();
                    if (tmp == null && img == null) continue;
                    if (img != null && img.gameObject.name.Contains("Hover")) continue;

                    g.color = selected ? SelectedColor : UnselectedColor;
                }

                var bar = FindDeep(tab.transform, "SelectBarHolder");
                if (bar != null) bar.gameObject.SetActive(selected);
            }
            catch
            {
            }
        }

        private static Transform FindDeep(Transform root, string name)
        {
            for (int i = 0; i < root.childCount; i++)
            {
                var child = root.GetChild(i);
                if (child.name == name) return child;

                var deeper = FindDeep(child, name);
                if (deeper != null) return deeper;
            }
            return null;
        }

        public void Poll()
        {
            if (!_built || _panel == null || !_panel.activeSelf) return;

            // Detect a click on one of the game's tabs directly.
            //
            // Watching for the game's panel to CHANGE is not enough: if the settings menu was
            // opened on General, then Mods, then General again, the game considers General
            // still selected and swaps nothing — so our panel never stood down and the
            // settings menu was stuck on Mods.
            if (Input.GetMouseButtonDown(0) && PointerOverGameTab())
            {
                Close();
                return;
            }

            // Hold the highlight against the game re-applying its own styling.
            for (int i = 0; i < _gameTabs.Count; i++) SetTabSelected(_gameTabs[i], false);
            SetTabSelected(_tabButton, true);

            // Also step aside if the game swapped panels by any other route.
            if (CurrentGamePanel() != _panelOnOpen) Close();
        }

        private void Rebuild()
        {
            if (_scrollContent == null) return;

            for (int i = _scrollContent.childCount - 1; i >= 0; i--)
            {
                UnityEngine.Object.DestroyImmediate(_scrollContent.GetChild(i).gameObject);
            }

            if (_detailMod == null) BuildList();
            else BuildDetail(_detailMod);
        }

        private void BuildList()
        {
            AddHeader("MOD SETTINGS");

            var mods = _modSource != null ? _modSource() : new List<ModEntry>();
            int shown = 0;

            foreach (var mod in mods)
            {
                var settings = ModSettings.For(mod);
                if (settings.Count == 0) continue;

                shown++;
                var captured = mod;
                AddButtonRow(mod.DisplayName, $"{settings.Count} settings  >",
                             () => { _detailMod = captured; Rebuild(); });
            }

            if (shown == 0) AddText("No installed mod exposes any settings.", FontSize);
        }

        private void BuildDetail(ModEntry mod)
        {
            AddButtonRow("<  Back", "", () => { _detailMod = null; Rebuild(); });
            AddHeader(mod.DisplayName.ToUpperInvariant());

            foreach (var s in ModSettings.For(mod)) AddSettingRow(s);
        }

        // ---- row builders ----------------------------------------------------------

        private void AddHeader(string text)
        {
            var t = AddText(text, HeaderFontSize);
            if (t != null)
            {
                t.fontStyle = FontStyles.Bold;
                t.color = new Color(1f, 0.85f, 0.2f, 1f);   // the game's yellow
            }
        }

        private TextMeshProUGUI AddText(string text, float size)
        {
            var tmp = MakeText(_scrollContent, text, size);
            if (tmp == null) return null;

            var le = tmp.gameObject.AddComponent<LayoutElement>();
            le.minHeight = RowHeight;
            le.preferredHeight = RowHeight;
            return tmp;
        }

        private void AddButtonRow(string left, string right, Action onClick)
        {
            var row = NewRow();

            var img = row.AddComponent<Image>();
            img.color = new Color(1f, 1f, 1f, 0.10f);

            var btn = row.AddComponent<Button>();
            btn.targetGraphic = img;
            btn.onClick.AddListener(onClick);

            var l = MakeText(row.transform, left, FontSize);
            if (l != null) l.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;

            if (!string.IsNullOrEmpty(right))
            {
                var r = MakeText(row.transform, right, FontSize);
                if (r != null)
                {
                    r.alignment = TextAlignmentOptions.MidlineRight;
                    r.gameObject.AddComponent<LayoutElement>().preferredWidth = 150f;
                }
            }
        }

        private void AddSettingRow(SettingDescriptor s)
        {
            var row = NewRow();

            string scopeTag = s.Scope == ModCategory.HostOnly ? "  [host]" : "";
            var label = MakeText(row.transform, s.Label + scopeTag, FontSize);
            if (label != null) label.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;

            if (s.Kind == SettingKind.Text)
            {
                var v = MakeText(row.transform, s.RawValue, FontSize);
                if (v != null)
                {
                    v.alignment = TextAlignmentOptions.MidlineRight;
                    v.gameObject.AddComponent<LayoutElement>().preferredWidth = 200f;
                }
                return;
            }

            if (s.Kind == SettingKind.Bool)
            {
                bool state = IniStore.ReadBool(s.IniFile, s.Key, false);
                var holder = new TextHolder();

                MakeSmallButton(row.transform, state ? "ON" : "OFF", 74f, () =>
                {
                    state = !state;
                    IniStore.WriteBool(s.IniFile, s.Key, state);
                    if (holder.Text != null) holder.Text.text = state ? "ON" : "OFF";
                }, holder);
                return;
            }

            float value = IniStore.ReadFloat(s.IniFile, s.Key, 0f);
            var number = MakeText(row.transform, value.ToString("0.##"), FontSize);
            if (number != null)
            {
                number.alignment = TextAlignmentOptions.MidlineRight;
                number.gameObject.AddComponent<LayoutElement>().preferredWidth = 70f;
            }

            MakeSmallButton(row.transform, "-", 34f, () =>
            {
                value = Mathf.Clamp(value - s.Step, s.Min, s.Max);
                IniStore.WriteFloat(s.IniFile, s.Key, value);
                if (number != null) number.text = value.ToString("0.##");
            }, null);

            MakeSmallButton(row.transform, "+", 34f, () =>
            {
                value = Mathf.Clamp(value + s.Step, s.Min, s.Max);
                IniStore.WriteFloat(s.IniFile, s.Key, value);
                if (number != null) number.text = value.ToString("0.##");
            }, null);
        }

        private class TextHolder { public TextMeshProUGUI Text; }

        private GameObject NewRow()
        {
            var row = new GameObject("Row");
            row.transform.SetParent(_scrollContent, false);
            row.AddComponent<RectTransform>();

            var hlg = row.AddComponent<HorizontalLayoutGroup>();
            hlg.childControlWidth = true;
            hlg.childControlHeight = true;
            hlg.childForceExpandWidth = false;
            hlg.childAlignment = TextAnchor.MiddleLeft;
            hlg.spacing = 8f;
            var pad = new RectOffset();
            pad.left = 10; pad.right = 10;
            hlg.padding = pad;

            var le = row.AddComponent<LayoutElement>();
            le.minHeight = RowHeight;
            le.preferredHeight = RowHeight;
            return row;
        }

        private void MakeSmallButton(Transform parent, string text, float width, Action onClick,
                                     TextHolder holder)
        {
            var go = new GameObject("Btn");
            go.transform.SetParent(parent, false);
            go.AddComponent<RectTransform>();

            var le = go.AddComponent<LayoutElement>();
            le.preferredWidth = width;
            le.minHeight = RowHeight - 6f;

            var img = go.AddComponent<Image>();
            img.color = new Color(0.45f, 0.6f, 0.95f, 0.55f);

            var btn = go.AddComponent<Button>();
            btn.targetGraphic = img;
            btn.onClick.AddListener(onClick);

            var label = MakeText(go.transform, text, FontSize);
            if (label != null)
            {
                var rt = label.GetComponent<RectTransform>();
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.offsetMin = Vector2.zero;
                rt.offsetMax = Vector2.zero;
                label.alignment = TextAlignmentOptions.Center;
            }
            if (holder != null) holder.Text = label;
        }

        // ---- text ------------------------------------------------------------------

        /// A TextMeshProUGUI added to a bare GameObject has no font asset and renders
        /// nothing. Clone one of the game's labels so we inherit its font and material.
        private TextMeshProUGUI MakeText(Transform parent, string content, float size)
        {
            if (_textDonor == null) _textDonor = FindTextDonor();
            if (_textDonor == null) return null;

            var go = UnityEngine.Object.Instantiate(_textDonor, parent, false);
            go.name = "Text";
            go.SetActive(true);

            var t = go.transform;
            for (int i = t.childCount - 1; i >= 0; i--)
                UnityEngine.Object.DestroyImmediate(t.GetChild(i).gameObject);

            var tmp = go.GetComponent<TextMeshProUGUI>();
            if (tmp == null) return null;

            tmp.text = content;
            tmp.fontSize = size;
            // The donor label carries its own colour, which on a dark panel is unreadable.
            // Force an explicit light colour rather than inheriting whatever we cloned.
            tmp.color = new Color(0.92f, 0.94f, 1f, 1f);
            tmp.enableAutoSizing = false;
            tmp.enableWordWrapping = false;
            tmp.alignment = TextAlignmentOptions.MidlineLeft;
            tmp.overflowMode = TextOverflowModes.Ellipsis;
            return tmp;
        }

        private GameObject FindTextDonor()
        {
            var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<TextMeshProUGUI>());
            for (int i = 0; i < all.Length; i++)
            {
                var tmp = all[i].TryCast<TextMeshProUGUI>();
                if (tmp == null || tmp.font == null) continue;
                var go = tmp.gameObject;
                if (go == null || !go.scene.IsValid()) continue;
                return go;
            }
            return null;
        }

        /// The game's tab controller has no idea our tab exists, so clicking it never runs the
        /// controller's switch-panel logic and whatever was selected stays active at alpha 1,
        /// sharing the content area with ours. Measured: 'Contents_Controls' active=True
        /// alpha=1.00 at the same time as our own panel.
        ///
        /// Fade them the way the GAME does rather than deactivating them — deactivating is the
        /// documented trap that leaves the settings window blank, because the controller still
        /// believes its tab is selected. Alpha is reversible and is the game's own mechanism.
        private void HideGamePanels()
        {
            RestoreGamePanels();

            foreach (var panel in _otherPanels)
            {
                if (panel == null || !panel.activeInHierarchy) continue;

                var cg = panel.GetComponent<CanvasGroup>();
                if (cg == null || cg.alpha <= 0f) continue;

                _fadedPanels.Add(new KeyValuePair<CanvasGroup, float>(cg, cg.alpha));
                cg.alpha = 0f;
            }
        }

        /// Always give back exactly what we took, so leaving our tab cannot strand one of the
        /// game's panels invisible.
        private void RestoreGamePanels()
        {
            foreach (var pair in _fadedPanels)
            {
                try { if (pair.Key != null) pair.Key.alpha = pair.Value; } catch { }
            }
            _fadedPanels.Clear();
        }

        public void Close()
        {
            RestoreGamePanels();
            if (_panel != null) _panel.SetActive(false);
            SetTabSelected(_tabButton, false);

            // Restore the game's own highlight on whichever panel is showing.
            var open = CurrentGamePanel();
            for (int i = 0; i < _gameTabs.Count; i++)
            {
                var tab = _gameTabs[i];
                bool matches = open != null && tab != null &&
                               open.name.Replace("Contents_", "") == tab.name.Replace("Tab_", "");
                SetTabSelected(tab, matches);
            }
        }

        private bool PointerOverGameTab()
        {
            Vector2 mouse = Input.mousePosition;

            for (int i = 0; i < _gameTabs.Count; i++)
            {
                var tab = _gameTabs[i];
                if (tab == null || !tab.activeInHierarchy) continue;

                var rt = tab.GetComponent<RectTransform>();
                if (rt == null) continue;

                if (RectTransformUtility.RectangleContainsScreenPoint(rt, mouse, null)) return true;
            }
            return false;
        }

        private GameObject CurrentGamePanel()
        {
            for (int i = 0; i < _otherPanels.Count; i++)
            {
                var p = _otherPanels[i];
                if (p != null && p.activeSelf) return p;
            }
            return null;
        }

        private GameObject FindInactiveByName(string name)
        {
            var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<RectTransform>());
            for (int i = 0; i < all.Length; i++)
            {
                var rt = all[i].TryCast<RectTransform>();
                if (rt == null) continue;
                var go = rt.gameObject;
                if (go == null || !go.scene.IsValid()) continue;
                if (go.name == name) return go;
            }
            return null;
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
