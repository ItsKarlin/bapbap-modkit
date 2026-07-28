// BAPBAP Third Person
// Copyright (c) 2026 ItsKarlin — MIT licensed (see LICENSE)
//
// A clean-room implementation. This is written directly against BAPBAP's own public API,
// discovered by inspecting the game's IL2CPP metadata:
//
//   BAPBAP.Debugging.DebugGameplayManager.SetThirdPersonMode(bool)
//   BAPBAP.Debugging.DebugGameplayManager.thirdPersonCursor
//   BAPBAP.UI.UIAugments.canvasGroup
//
// No third-party mod source is used or derived from. The camera mode itself is a feature
// the developers built into the game; this mod exposes it and adds a usable pointer.
//
// Features
//   * F1 toggles the game's third-person camera
//   * The centre crosshair is hidden (it is not useful in this mode)
//   * A pointer appears for card selection and menus, on a dedicated overlay canvas so it
//     always draws above the UI, and never intercepts clicks
//   * Settings live in UserData/BAPBAPThirdPerson.ini
//
// Performance notes (these matter — naive versions of this mod cause frame stutter):
//   * No object scanning on a timer. Every lookup is cached and only retried when null,
//     at most once every 10 seconds.
//   * Menu canvases are enumerated ONCE per scene, then only polled with bool reads.
//   * In IL2CPP, Resources.FindObjectsOfTypeAll allocates an array AND every TryCast
//     allocates an interop wrapper. Doing that per frame — or even per second — generates
//     enough garbage to trigger visible GC hitches. Steady-state cost here is a handful of
//     null checks and one position write.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;
using UnityEngine.UI;
using Il2CppInterop.Runtime;
using Il2CppBAPBAP.UI;
using Il2CppBAPBAP.Debugging;

[assembly: MelonInfo(typeof(BapbapThirdPerson.ThirdPersonMod), "BAPBAP Third Person", "1.0.0", "ItsKarlin")]
[assembly: MelonGame(null, "BAPBAP")]

namespace BapbapThirdPerson
{
    public class ThirdPersonMod : MelonMod
    {
        private const float LookupCooldown = 10f;

        private Config _config;

        // Cached game objects. Null means "not found yet"; each is retried on a cooldown.
        private DebugGameplayManager _debugManager;
        private float _lastManagerLookup = -999f;

        private UIAugments _augments;
        private float _lastAugmentLookup = -999f;

        private GameObject _gameCrosshair;

        // Menu canvases: found once per scene, then only read.
        private List<GameObject> _menuCanvases = new List<GameObject>();
        private bool _menusScanned;

        // Our pointer.
        private GameObject _pointerRoot;
        private RectTransform _pointerRect;
        private Image _pointerImage;

        private bool _thirdPersonActive;
        private bool _keyHeldLastFrame;

        // Camera settings are applied to the game's own CameraThirdPerson driver. Cached
        // like everything else; re-acquired only when it goes null.
        private Il2CppBAPBAP.Local.CameraThirdPerson _tpCamera;
        private float _lastCameraLookup = -999f;
        private float _appliedFov = -1f;
        private float _appliedSens = -1f;

        // The settings file is watched by timestamp so edits apply without a restart. A
        // stat() every couple of seconds is far cheaper than re-reading or scanning.
        private string _configPath;
        private DateTime _configStamp;
        private float _configCheckTimer;

        public override void OnInitializeMelon()
        {
            _config = Config.Load(LoggerInstance);
            _configPath = Path.Combine(MelonEnvironment.UserDataDirectory, "BAPBAPThirdPerson.ini");
            try { _configStamp = File.GetLastWriteTimeUtc(_configPath); } catch { }
            LoggerInstance.Msg($"BAPBAP Third Person ready — press {_config.ToggleKey} to toggle.");
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            // Everything cached belongs to the old scene.
            _debugManager = null;
            _augments = null;
            _gameCrosshair = null;
            _tpCamera = null;
            _menuCanvases.Clear();
            _menusScanned = false;
        }

        public override void OnUpdate()
        {
            HandleToggleKey();
            WatchConfig();
        }

        /// Applies edits to the .ini without a restart, so changing a slider in-game takes
        /// effect immediately. Timestamp check only — no file read unless it actually moved.
        private void WatchConfig()
        {
            _configCheckTimer += Time.unscaledDeltaTime;
            if (_configCheckTimer < 2f) return;
            _configCheckTimer = 0f;

            try
            {
                var stamp = File.GetLastWriteTimeUtc(_configPath);
                if (stamp == _configStamp) return;

                _configStamp = stamp;
                _config = Config.Load(LoggerInstance);
                _appliedFov = -1f;   // force re-apply
                _appliedSens = -1f;
                LoggerInstance.Msg("Settings reloaded.");
            }
            catch
            {
            }
        }

        /// Pushes client-side camera preferences into the game's third-person camera.
        /// Only writes when a value actually differs, so this is near-free per frame.
        private void ApplyCameraSettings()
        {
            try
            {
                if (_tpCamera == null)
                {
                    if (Time.unscaledTime - _lastCameraLookup < LookupCooldown) return;
                    _lastCameraLookup = Time.unscaledTime;
                    _tpCamera = UnityEngine.Object.FindObjectOfType<Il2CppBAPBAP.Local.CameraThirdPerson>();
                    if (_tpCamera == null) return;
                }

                if (Math.Abs(_config.FovMultiplier - _appliedFov) > 0.001f)
                {
                    _tpCamera.SetFoVMultiplier(_config.FovMultiplier);
                    _appliedFov = _config.FovMultiplier;
                }

                if (_config.Sensitivity > 0f && Math.Abs(_config.Sensitivity - _appliedSens) > 0.001f)
                {
                    _tpCamera.sensitivity = _config.Sensitivity;
                    _appliedSens = _config.Sensitivity;
                }

                if (_config.CameraHeight != 0f) _tpCamera.yHeight = _config.CameraHeight;
                if (_config.CameraPitch != 0f) _tpCamera.pitch = _config.CameraPitch;
            }
            catch
            {
                _tpCamera = null;
            }
        }

        // Pointer work runs in LateUpdate so it settles after the game's own UI pass.
        public override void OnLateUpdate()
        {
            if (!_config.PointerEnabled) return;

            ApplyCameraSettings();

            ScanMenusOnce();
            if (!EnsurePointer()) return;

            SuppressGameCrosshair();

            bool wantPointer = CardsAreVisible() || MenuIsOpen();

            if (_pointerRoot.activeSelf != wantPointer)
            {
                _pointerRoot.SetActive(wantPointer);
            }

            if (!wantPointer) return;

            // The pointer canvas is ScreenSpaceOverlay, so mouse pixels map 1:1.
            Vector3 mouse = Input.mousePosition;
            _pointerRect.position = new Vector3(mouse.x, mouse.y, 0f);
        }

        // ---- camera toggle ---------------------------------------------------------

        private void HandleToggleKey()
        {
            bool held = Input.GetKey(_config.ToggleKey);
            bool pressed = held && !_keyHeldLastFrame;
            _keyHeldLastFrame = held;

            if (!pressed) return;

            SetThirdPerson(!_thirdPersonActive);
        }

        private void SetThirdPerson(bool enabled)
        {
            var manager = GetDebugManager();
            if (manager == null)
            {
                LoggerInstance.Warning("Cannot toggle yet — the game's debug manager is not available.");
                return;
            }

            try
            {
                manager.SetThirdPersonMode(enabled);
                _thirdPersonActive = enabled;
                LoggerInstance.Msg($"Third person {(enabled ? "ON" : "OFF")}");
            }
            catch (Exception ex)
            {
                LoggerInstance.Error($"Failed to set camera mode: {ex.Message}");
            }
        }

        // ---- cached lookups --------------------------------------------------------

        private DebugGameplayManager GetDebugManager()
        {
            if (_debugManager != null) return _debugManager;
            if (Time.unscaledTime - _lastManagerLookup < LookupCooldown) return null;

            _lastManagerLookup = Time.unscaledTime;
            try { _debugManager = UnityEngine.Object.FindObjectOfType<DebugGameplayManager>(); }
            catch { _debugManager = null; }

            return _debugManager;
        }

        /// The card-pick panel is faded in and out, and its holder object stays active
        /// permanently — so the CanvasGroup alpha is the only reliable "is it on screen"
        /// signal. Testing activeInHierarchy here yields a permanently visible pointer.
        private bool CardsAreVisible()
        {
            try
            {
                if (_augments == null)
                {
                    if (Time.unscaledTime - _lastAugmentLookup < LookupCooldown) return false;
                    _lastAugmentLookup = Time.unscaledTime;
                    _augments = UnityEngine.Object.FindObjectOfType<UIAugments>();
                }

                if (_augments == null) return false;

                var group = _augments.canvasGroup;
                return group != null && group.alpha > 0.05f;
            }
            catch
            {
                _augments = null;
                return false;
            }
        }

        // ---- menus -----------------------------------------------------------------

        private void ScanMenusOnce()
        {
            if (_menusScanned) return;
            _menusScanned = true;

            try
            {
                var canvases = Resources.FindObjectsOfTypeAll(Il2CppType.Of<Canvas>());
                for (int i = 0; i < canvases.Length; i++)
                {
                    var canvas = canvases[i].TryCast<Canvas>();
                    if (canvas == null) continue;

                    var go = canvas.gameObject;
                    if (go == null || !go.scene.IsValid()) continue;

                    string name = go.name.ToLowerInvariant();
                    if (name.Contains("settingsmenu") || name.Contains("pause") || name.Contains("optionsmenu"))
                    {
                        _menuCanvases.Add(go);
                    }
                }

                LoggerInstance.Msg($"Tracking {_menuCanvases.Count} menu canvas(es) for pointer visibility.");
            }
            catch (Exception ex)
            {
                LoggerInstance.Warning($"Menu scan failed: {ex.Message}");
            }
        }

        private bool MenuIsOpen()
        {
            for (int i = 0; i < _menuCanvases.Count; i++)
            {
                var go = _menuCanvases[i];
                if (go != null && go.activeInHierarchy) return true;
            }
            return false;
        }

        // ---- pointer ---------------------------------------------------------------

        /// Builds a dedicated overlay canvas rather than reusing the game's crosshair, which
        /// lives on a low-sorting canvas and therefore renders underneath menus.
        private bool EnsurePointer()
        {
            if (_pointerRect != null) return true;

            var manager = GetDebugManager();
            if (manager == null) return false;

            Sprite sprite = null;
            try
            {
                _gameCrosshair = manager.thirdPersonCursor;
                if (_gameCrosshair != null)
                {
                    var image = _gameCrosshair.GetComponent<Image>();
                    if (image != null) sprite = image.sprite;
                }
            }
            catch
            {
                return false;
            }

            if (sprite == null) return false;

            try
            {
                _pointerRoot = new GameObject("BAPBAPThirdPerson_Pointer");
                UnityEngine.Object.DontDestroyOnLoad(_pointerRoot);

                var canvas = _pointerRoot.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = _config.PointerSortingOrder;

                var child = new GameObject("Icon");
                child.transform.SetParent(_pointerRoot.transform, false);

                _pointerImage = child.AddComponent<Image>();
                _pointerImage.sprite = sprite;
                _pointerImage.raycastTarget = false; // must never swallow UI clicks

                _pointerRect = child.GetComponent<RectTransform>();
                _pointerRect.sizeDelta = new Vector2(_config.PointerSize, _config.PointerSize);

                _pointerRoot.SetActive(false);
                LoggerInstance.Msg($"Pointer created (sorting order {_config.PointerSortingOrder}).");
                return true;
            }
            catch (Exception ex)
            {
                LoggerInstance.Error($"Could not create pointer: {ex.Message}");
                _pointerRect = null;
                return false;
            }
        }

        /// The game's centre crosshair is hidden by disabling its Image, leaving the object
        /// itself alone so the game keeps ownership of its active state.
        private void SuppressGameCrosshair()
        {
            if (!_config.HideCrosshair || _gameCrosshair == null) return;

            try
            {
                var image = _gameCrosshair.GetComponent<Image>();
                if (image != null && image.enabled) image.enabled = false;
            }
            catch
            {
                _gameCrosshair = null;
            }
        }
    }

    /// Plain key=value settings file, matching the convention other BAPBAP mods use.
    internal class Config
    {
        // [scope: client] Every setting here is a LOCAL preference. None of them affect
        // other players, so none can desync a lobby and none need networking. Host-scoped
        // settings (things that change the match itself) belong to mods that read them only
        // on the hosting machine.
        public KeyCode ToggleKey = KeyCode.F1;
        public bool PointerEnabled = true;
        public bool HideCrosshair = true;
        public float PointerSize = 32f;
        public int PointerSortingOrder = 30000;

        // Camera tuning. 0 means "leave the game's value alone".
        public float FovMultiplier = 1.0f;   // 0.5 - 2.0
        public float Sensitivity = 0f;       // 0 = untouched
        public float CameraHeight = 0f;      // 0 = untouched
        public float CameraPitch = 0f;       // 0 = untouched

        public static Config Load(MelonLogger.Instance log)
        {
            var config = new Config();
            string path = Path.Combine(MelonEnvironment.UserDataDirectory, "BAPBAPThirdPerson.ini");

            try
            {
                if (!File.Exists(path))
                {
                    Save(config, path);
                    log.Msg($"Created default config at {path}");
                    return config;
                }

                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;

                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;

                    string key = line.Substring(0, eq).Trim();
                    string value = line.Substring(eq + 1).Trim();

                    switch (key)
                    {
                        case "ToggleKey":
                            if (Enum.TryParse<KeyCode>(value, true, out var parsed)) config.ToggleKey = parsed;
                            break;
                        case "PointerEnabled":
                            config.PointerEnabled = value.Equals("true", StringComparison.OrdinalIgnoreCase);
                            break;
                        case "HideCrosshair":
                            config.HideCrosshair = value.Equals("true", StringComparison.OrdinalIgnoreCase);
                            break;
                        case "PointerSize":
                            if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var size))
                                config.PointerSize = size;
                            break;
                        case "PointerSortingOrder":
                            if (int.TryParse(value, out var order)) config.PointerSortingOrder = order;
                            break;
                        case "FovMultiplier":
                            if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var fov))
                                config.FovMultiplier = Mathf.Clamp(fov, 0.3f, 3f);
                            break;
                        case "Sensitivity":
                            if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var sens))
                                config.Sensitivity = Mathf.Max(0f, sens);
                            break;
                        case "CameraHeight":
                            if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var h))
                                config.CameraHeight = h;
                            break;
                        case "CameraPitch":
                            if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var pi))
                                config.CameraPitch = pi;
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                log.Warning($"Config load failed ({ex.Message}) — using defaults.");
            }

            return config;
        }

        private static void Save(Config config, string path)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# BAPBAP Third Person configuration");
            sb.AppendLine("# All settings here are CLIENT-side: they affect only your own screen,");
            sb.AppendLine("# never other players, and can be changed freely without desyncing a lobby.");
            sb.AppendLine();
            sb.AppendLine("# Key that toggles the third-person camera (Unity KeyCode name).");
            sb.AppendLine($"ToggleKey={config.ToggleKey}");
            sb.AppendLine();
            sb.AppendLine("# Show a mouse pointer during card selection and menus.");
            sb.AppendLine($"PointerEnabled={config.PointerEnabled}");
            sb.AppendLine();
            sb.AppendLine("# Hide the game's centre crosshair.");
            sb.AppendLine($"HideCrosshair={config.HideCrosshair}");
            sb.AppendLine();
            sb.AppendLine("# Pointer size in pixels.");
            sb.AppendLine($"PointerSize={config.PointerSize.ToString(CultureInfo.InvariantCulture)}");
            sb.AppendLine();
            sb.AppendLine("# Canvas sorting order. Higher draws above more UI.");
            sb.AppendLine($"PointerSortingOrder={config.PointerSortingOrder}");
            sb.AppendLine();
            sb.AppendLine("# --- camera (client-side) ---");
            sb.AppendLine("# Field of view multiplier. 1.0 = game default, higher = wider.");
            sb.AppendLine($"FovMultiplier={config.FovMultiplier.ToString(CultureInfo.InvariantCulture)}");
            sb.AppendLine();
            sb.AppendLine("# Look sensitivity in third person. 0 = leave the game's value alone.");
            sb.AppendLine($"Sensitivity={config.Sensitivity.ToString(CultureInfo.InvariantCulture)}");
            sb.AppendLine();
            sb.AppendLine("# Camera height offset. 0 = leave alone.");
            sb.AppendLine($"CameraHeight={config.CameraHeight.ToString(CultureInfo.InvariantCulture)}");
            sb.AppendLine();
            sb.AppendLine("# Camera pitch. 0 = leave alone.");
            sb.AppendLine($"CameraPitch={config.CameraPitch.ToString(CultureInfo.InvariantCulture)}");

            File.WriteAllText(path, sb.ToString());
        }
    }
}
