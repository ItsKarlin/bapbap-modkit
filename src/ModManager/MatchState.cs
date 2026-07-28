// "Is a match running?" detection — powers the host-only lock.
//
// v1 of this check (in ui-recon) was WRONG: it counted types via
// Resources.FindObjectsOfTypeAll, which includes inactive objects and prefab assets. That
// reported "GameMode: 4 instances" while sitting in the main menu, because all four
// gamemode prefabs are simply loaded in memory.
//
// v2 asks two better questions:
//   1. Is Mirror actually running a server/client right now?
//   2. Is a GameMode object genuinely ACTIVE IN THE HIERARCHY (not just loaded)?
//
// FAIL-SAFE: if the state cannot be determined, we report "in match" so host toggles stay
// locked. Blocking a legal edit is much cheaper than allowing an illegal one mid-round.

using System;
using UnityEngine;
using Il2CppInterop.Runtime;

namespace BapbapMods.Manager
{
    public enum MatchStatus
    {
        InMenu,
        InMatch,
        Unknown
    }

    public static class MatchState
    {
        // Reflection results are cached: these were previously resolved on every single
        // evaluation, which meant Type.GetType + Il2CppType.From several times a second.
        private static System.Reflection.PropertyInfo _mirrorActiveProp;
        private static bool _mirrorPropResolved;

        public static MatchStatus Current { get; private set; } = MatchStatus.Unknown;
        public static string LastReason { get; private set; } = "not yet evaluated";

        /// True when host-only settings must be locked.
        public static bool HostEditingLocked => Current != MatchStatus.InMenu;

        public static void Evaluate()
        {
            try
            {
                bool serverActive = MirrorServerActive();
                bool gameModeLive = ActiveGameModeExists();

                if (gameModeLive)
                {
                    Current = MatchStatus.InMatch;
                    LastReason = serverActive
                        ? "an active GameMode is live and Mirror is hosting"
                        : "an active GameMode is live";
                    return;
                }

                Current = MatchStatus.InMenu;
                LastReason = serverActive
                    ? "Mirror is up (lobby) but no GameMode is active"
                    : "no active GameMode, Mirror idle";
            }
            catch (Exception ex)
            {
                // Never let detection failure unlock the host controls.
                Current = MatchStatus.Unknown;
                LastReason = $"probe failed ({ex.Message}) — locking as a precaution";
            }
        }

        private static bool MirrorServerActive()
        {
            try
            {
                if (!_mirrorPropResolved)
                {
                    _mirrorPropResolved = true;
                    var t = Type.GetType("Il2CppMirror.NetworkServer, Il2CppMirror");
                    _mirrorActiveProp = t?.GetProperty("active");
                }

                if (_mirrorActiveProp == null) return false;

                object val = _mirrorActiveProp.GetValue(null);
                return val is bool b && b;
            }
            catch
            {
                return false;
            }
        }

        // [perf rewrite] The previous implementation called Resources.FindObjectsOfTypeAll
        // and TryCast'd every result, on a timer. In Il2Cpp that is a GC machine: the call
        // allocates an array and EVERY TryCast allocates an interop wrapper. Sustained
        // garbage means collections at irregular intervals — which is exactly the stutter
        // signature we were chasing.
        //
        // Replacement: hold one cached GameManager reference and read currentGameMode off
        // it. FindObjectOfType runs only when the cache is empty, and never more than once
        // every few seconds. Steady-state cost is one null check and one field read.
        private static Il2CppBAPBAP.Game.GameManager _gameManager;
        private static float _lastManagerLookup = -999f;
        private const float ManagerLookupCooldown = 3f;

        private static bool ActiveGameModeExists()
        {
            try
            {
                if (_gameManager == null)
                {
                    if (Time.unscaledTime - _lastManagerLookup < ManagerLookupCooldown)
                    {
                        return false; // recently looked, still absent — do not rescan
                    }
                    _lastManagerLookup = Time.unscaledTime;
                    _gameManager = UnityEngine.Object.FindObjectOfType<Il2CppBAPBAP.Game.GameManager>();
                }

                if (_gameManager == null) return false;

                // A live gamemode on the manager means a match is actually running. This is
                // the same signal BAPFPS uses, and it costs one field read.
                return _gameManager.currentGameMode != null;
            }
            catch
            {
                _gameManager = null;
                return false;
            }
        }

        private static Il2CppSystem.Type ResolveIl2CppType(string fullName, string assembly)
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
