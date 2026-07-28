// Native "MODS" tab button injected into the game's own lobby tab bar.
//
// ARCHITECTURE NOTE — why we clone a button instead of registering a real page:
// UILobbyTabGroup is NOT extensible. Its pages are private fields and PageIndex is a fixed
// 6-value enum (Rankings, Characters, Play, Locker, Shop, Profile). UILobbyTabPage is
// abstract with abstract members. Adding a genuine 7th page would mean injecting a new
// Il2Cpp subclass via ClassInjector and satisfying that abstract contract — high risk, and
// it would still not appear in their PageIndex switchboard.
//
// So instead: clone an existing tab button (inheriting its art, hover effects, fonts and
// theming for free), strip the UILobbyTabButton component so the game's tab controller
// ignores it, and drive our own panel. The button looks and feels native because it
// literally IS one of their buttons; only its behaviour is ours.
//
// Trade-off: the game does not know our tab exists, so when the user clicks a real tab we
// must hide our panel ourselves. Handled by polling IsPageOpened each frame in the manager.

using System;
using MelonLoader;
using UnityEngine;
using UnityEngine.UI;
using Il2CppInterop.Runtime;
using Il2CppTMPro;

namespace BapbapMods.Manager
{
    public static class NativeTab
    {
        public const string CloneName = "BAPBAPModsTab";

        public static bool Injected { get; private set; }
        public static GameObject Button { get; private set; }

        /// The game's own tab row (the HorizontalLayoutGroup holding Play/Locker/Shop...).
        /// Exposed so the page can hit-test those tabs without re-searching.
        public static Transform GameTabRow { get; private set; }

        /// Our clone's highlight graphic. With UILobbyTabButton stripped, nothing else
        /// drives this — so the page can set it directly and it stays set.
        public static CanvasGroup OurHighlight { get; private set; }

        /// Set by the injected button's click handler; the manager consumes and clears it.
        public static bool ClickPending;

        private static MelonLogger.Instance _log;

        public static void Init(MelonLogger.Instance log) => _log = log;

        /// Attempts injection. Safe to call repeatedly — it no-ops once injected, and returns
        /// false quietly whenever the lobby UI is not present yet (e.g. still loading).
        public static bool TryInject()
        {
            if (Injected && Button != null) return true;

            try
            {
                var template = FindTabButtonTemplate(out var container);
                if (template == null || container == null) return false;

                // Set on every path. Previously this was only assigned when a NEW clone was
                // created, so after a scene reload (which finds the existing button and
                // returns early) it stayed null — and everything depending on it, including
                // the nav-bar highlight, silently did nothing.
                GameTabRow = container;

                // Already injected in a previous scene pass?
                var hostForLookup = container.parent != null ? container.parent : container;
                var existing = hostForLookup.Find(CloneName);
                if (existing != null)
                {
                    Button = existing.gameObject;
                    Injected = true;
                    _log?.Msg($"reusing existing MODS button; tab row has {container.childCount} tabs.");
                    return true;
                }

                // IMPORTANT: do NOT parent into the game's tab Container. That container's
                // children are managed by UILobbyTabGroup/UITabController and adding an
                // extra child disturbed their layout (it displaced the Shop tab). Parent one
                // level up into NavBarButtons instead, so their row is byte-identical to
                // vanilla and we simply sit alongside it.
                var host = container.parent != null ? container.parent : container;

                var clone = UnityEngine.Object.Instantiate(template.gameObject, host, false);
                clone.name = CloneName;
                PlaceAlongsideRow(clone, container);

                // Never inherit the template's hidden state.
                clone.SetActive(true);

                // UILobbyTabButton is KEPT — a side-by-side dump of a lit tab versus ours
                // proved it is the ONLY difference between them. Every other property
                // (active states, CanvasGroup alphas, graphic RGBA, component lists) is
                // identical, because the highlight is drawn by UberSDF shader state that
                // this component drives via its _sdfEventHandlers. Nothing reachable through
                // CanvasGroup or Graphic can reproduce it.
                //
                // It self-selects on click and never deselects, so the page drives
                // ToggleSelected() every frame in BOTH directions rather than once.
                ClearSelectionVisuals(clone);
                bool labelled = SetLabel(clone, "MODS");
                string icon = SetDistinctIcon(clone);
                ClearSelectionVisuals(clone);
                WireClick(clone);

                OurHighlight = PrepareHighlight(clone);

                Button = clone;
                Injected = true;

                _log?.Msg($"native MODS tab injected — cloned '{template.name}', " +
                          $"parent '{DescribePath(clone.transform.parent)}', " +
                          $"rowUntouched={container.childCount} children, " +
                          $"templateActive={template.gameObject.activeInHierarchy}, " +
                          $"cloneActive={clone.activeInHierarchy}, labelSet={labelled}, icon={icon}");
                LogTabRow(container);
                return true;
            }
            catch (Exception ex)
            {
                _log?.Warning($"native tab injection failed: {ex.Message} — F5 panel still works.");
                return false;
            }
        }

        // Finds any live UILobbyTabButton and returns it plus its parent container.
        private static Transform FindTabButtonTemplate(out Transform container)
        {
            container = null;
            Transform fallback = null;
            Transform fallbackParent = null;

            var type = ResolveType("Il2CppBAPBAP.UI.UILobbyTabButton", "Assembly-CSharp");
            if (type == null) return null;

            var objs = Resources.FindObjectsOfTypeAll(type);
            for (int i = 0; i < objs.Length; i++)
            {
                var comp = objs[i].TryCast<Component>();
                if (comp == null) continue;

                var go = comp.gameObject;
                if (go == null || !go.scene.IsValid()) continue;
                if (go.name == CloneName) continue;

                var parent = go.transform.parent;
                if (parent == null) continue;

                // The real tab row is a HorizontalLayoutGroup named "Container".
                if (parent.GetComponent<HorizontalLayoutGroup>() == null) continue;

                // Prefer a VISIBLE template. FindObjectsOfTypeAll also returns inactive
                // objects, and Instantiate copies activeSelf — cloning a hidden tab
                // produces a hidden button, which is exactly the bug seen on first try.
                if (!go.activeInHierarchy)
                {
                    if (fallback == null) { fallback = go.transform; fallbackParent = parent; }
                    continue;
                }

                container = parent;
                return go.transform;
            }

            if (fallback != null)
            {
                _log?.Warning("no ACTIVE tab button found; cloning an inactive one.");
                container = fallbackParent;
                return fallback;
            }
            return null;
        }

        // Remove the game's own tab component so their controller never tries to manage a
        // button it has no page for. Visual components (Image, hover lerps, SFX) stay.


        private static bool SetLabel(GameObject clone, string text)
        {
            var label = clone.transform.Find("Text");
            var tmp = label != null ? label.GetComponent<TextMeshProUGUI>() : null;

            // Fall back to any TMP text anywhere under the clone.
            if (tmp == null) tmp = clone.GetComponentInChildren<TextMeshProUGUI>(true);
            if (tmp == null) return false;

            // Disable any localisation-driven overwrite by simply setting the text; the
            // game re-localises its own buttons, but this clone is no longer one of them.
            tmp.text = text;
            tmp.gameObject.SetActive(true);
            return true;
        }

        // The clone inherits the template's icon, which makes it look like a duplicate of
        // whichever tab we copied. Swap in a visually distinct sprite so it reads as its own
        // tab. Falls back to hiding the icon entirely rather than shipping a lookalike.
        // Anchors the button to the right-hand end of the nav bar, just past the game's own
        // tab row, without becoming part of that row's layout.
        private static void PlaceAlongsideRow(GameObject clone, Transform row)
        {
            try
            {
                var rt = clone.GetComponent<RectTransform>();
                var rowRt = row.GetComponent<RectTransform>();
                if (rt == null) return;

                // Detach from any layout control it inherited.
                var le = clone.GetComponent<LayoutElement>();
                if (le != null) le.ignoreLayout = true;

                rt.anchorMin = new Vector2(1f, 0.5f);
                rt.anchorMax = new Vector2(1f, 0.5f);
                rt.pivot = new Vector2(1f, 0.5f);

                float width = rt.sizeDelta.x > 1f ? rt.sizeDelta.x : 140f;
                float rowWidth = rowRt != null ? rowRt.rect.width : 0f;

                // Sit just to the right of the tab row, nudged in from the edge.
                rt.anchoredPosition = new Vector2(-24f, 0f);
                rt.SetAsLastSibling();

                _log?.Msg($"placed MODS button beside the row " +
                          $"(width {width}, row width {rowWidth}).");
            }
            catch (Exception ex)
            {
                _log?.Warning($"placement fallback: {ex.Message}");
            }
        }

        private static string SetDistinctIcon(GameObject clone)
        {
            var iconTf = clone.transform.Find("Icon");
            var image = iconTf != null ? iconTf.GetComponent<Image>() : null;
            if (image == null) return "<no icon node>";

            string[] wanted = { "setting", "gear", "cog", "wrench", "tool", "option", "config", "slider" };

            try
            {
                var sprites = Resources.FindObjectsOfTypeAll(Il2CppType.Of<Sprite>());
                foreach (var key in wanted)
                {
                    for (int i = 0; i < sprites.Length; i++)
                    {
                        var sp = sprites[i].TryCast<Sprite>();
                        if (sp == null || string.IsNullOrEmpty(sp.name)) continue;
                        if (sp.name.ToLowerInvariant().Contains(key))
                        {
                            image.sprite = sp;
                            return sp.name;
                        }
                    }
                }
            }
            catch { /* fall through to hiding */ }

            // No suitable sprite: hide the icon so the tab is text-only rather than a
            // confusing copy of another tab's iconography.
            iconTf.gameObject.SetActive(false);
            return "<hidden — no match>";
        }

        // A fresh tab must not look pre-selected. These nodes are the selection bar and
        // highlight the game toggles when a tab is active.
        /// Takes ownership of the clone's SelectedUI: disables the fade driver that would
        /// otherwise animate it, and starts it hidden.
        private static void StripTabButton(GameObject clone)
        {
            var type = ResolveType("Il2CppBAPBAP.UI.UILobbyTabButton", "Assembly-CSharp");
            if (type == null) return;

            var comp = clone.GetComponent(type);
            if (comp != null) UnityEngine.Object.DestroyImmediate(comp, true);
        }

        private static CanvasGroup PrepareHighlight(GameObject clone)
        {
            try
            {
                var sel = FindDeep(clone.transform, "SelectedUI");
                if (sel == null) return null;

                sel.gameObject.SetActive(true);

                var fadeType = ResolveType("Il2CppBAPBAP.UI.UIAlphaFade", "Assembly-CSharp");
                if (fadeType != null)
                {
                    var fade = sel.GetComponent(fadeType);
                    var beh = fade != null ? fade.TryCast<Behaviour>() : null;
                    if (beh != null) beh.enabled = false;
                }

                var group = sel.GetComponent<CanvasGroup>() ?? sel.gameObject.AddComponent<CanvasGroup>();
                group.alpha = 0f;
                return group;
            }
            catch
            {
                return null;
            }
        }

        private static Transform FindDeep(Transform root, string name)
        {
            for (int i = 0; i < root.childCount; i++)
            {
                var c = root.GetChild(i);
                if (c.name == name) return c;
                var deeper = FindDeep(c, name);
                if (deeper != null) return deeper;
            }
            return null;
        }

        private static void ClearSelectionVisuals(GameObject clone)
        {
            string[] nodes = { "SelectBarHolder", "SelectedUI", "Notification" };
            foreach (var n in nodes)
            {
                var tf = clone.transform.Find(n);
                if (tf != null) tf.gameObject.SetActive(false);
            }
        }

        private static void LogTabRow(Transform container)
        {
            try
            {
                var names = new System.Text.StringBuilder();
                for (int i = 0; i < container.childCount; i++)
                {
                    var c = container.GetChild(i);
                    names.Append($"{i}:{c.name}{(c.gameObject.activeInHierarchy ? "" : "(off)")}  ");
                }
                _log?.Msg($"tab row -> {names}");
            }
            catch { }
        }

        private static string DescribePath(Transform t)
        {
            try
            {
                string path = t.name;
                var p = t.parent;
                int guard = 0;
                while (p != null && guard++ < 4) { path = p.name + "/" + path; p = p.parent; }
                return path;
            }
            catch { return "<unknown>"; }
        }

        private static void WireClick(GameObject clone)
        {
            var button = clone.GetComponent<Button>();
            if (button == null) return;

            // Their listeners point at the donor tab's page (Shop), so they must go — but
            // that also removes the click sound, which UISelectSfxElement wires to the same
            // Button. Play it explicitly instead.
            button.onClick.RemoveAllListeners();

            var sfx = clone.GetComponent<Il2CppBAPBAP.UI.UISelectSfxElement>();

            button.onClick.AddListener(new Action(() =>
            {
                try { if (sfx != null) sfx.OnClick(1f); } catch { }
                ClickPending = true;
            }));
        }

        /// Called when the scene is torn down / reloaded so we re-inject cleanly.
        public static void Reset()
        {
            Injected = false;
            Button = null;
            GameTabRow = null;
            OurHighlight = null;
            ClickPending = false;
        }

        private static Il2CppSystem.Type ResolveType(string fullName, string assembly)
        {
            try
            {
                var t = Type.GetType($"{fullName}, {assembly}");
                return t == null ? null : Il2CppType.From(t, false);
            }
            catch
            {
                return null;
            }
        }
    }
}
