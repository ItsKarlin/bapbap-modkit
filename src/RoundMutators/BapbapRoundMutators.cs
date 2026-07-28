// BAPBAP Round Mutators — turns on the devs' built-in match modifiers in a normal lobby.
//
// The game already has 16 of these (Meteor Shower, All Gigantic, Night Time, XCOM…). They are
// normally reachable only from a dev lobby. Everything needed to switch them on is public and
// server-authoritative on GameMode:
//
//   enableGameModifiers          the gate
//   SvAddGameModifier(int)       host-side add; the game's own Rpc replicates it to every client
//   SvRemoveGameModifier(int)
//
// So we never reimplement an effect, never patch anything, and guests install nothing — the host
// asks the game to do it and the game tells everyone else.
//
// HOST-SIDE. Only the hosting machine reads this config; that is what makes desync impossible.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;
using Il2CppBAPBAP.Game;

[assembly: MelonInfo(typeof(BapbapRoundMutators.RoundMutatorsMod), "BAPBAP Round Mutators", "0.1.0", "ItsKarlin")]
[assembly: MelonGame(null, "BAPBAP")]

namespace BapbapRoundMutators
{
    /// One of the game's built-in modifiers. Ids came from a dump of the game's own enum.
    public class Mutator
    {
        public int Id;
        public string Key;      // ini key suffix
        public string Label;    // what a human calls it

        public Mutator(int id, string key, string label) { Id = id; Key = key; Label = label; }
    }

    public class RoundMutatorsMod : MelonMod
    {
        /// The devs' list, dumped from the game. Ids are the game's, not ours.
        public static readonly Mutator[] All =
        {
            new Mutator(0,  "AllGigantic",           "Everyone is gigantic"),
            new Mutator(1,  "AngledMap",             "Angled map"),
            new Mutator(2,  "CDReduction",           "Faster cooldowns"),
            new Mutator(3,  "FastZone",              "Fast closing zone"),
            new Mutator(4,  "GoldDropIncrease",      "More gold drops"),
            new Mutator(5,  "HpReduction",           "Less health"),
            new Mutator(6,  "MeteorShower",          "Meteor shower"),
            new Mutator(7,  "MoneyIsPower",          "Money is power"),
            new Mutator(8,  "MoveSpeedBoost",        "Faster movement"),
            new Mutator(9,  "NightTime",             "Night time"),
            new Mutator(10, "NoPainNoGain",          "No pain no gain"),
            new Mutator(11, "NoPotionDrops",         "No potion drops"),
            new Mutator(12, "ReviveTeammateOnKill",  "Revive a teammate on kill"),
            new Mutator(13, "UniqueItemChance",      "More unique items"),
            new Mutator(14, "UseJuiceBoost",         "Juice boost"),
            new Mutator(15, "XCOM",                  "XCOM"),
        };

        private Config _config;
        private string _configPath;
        private DateTime _configStamp;
        private float _configCheckTimer;

        private GameManager _gameManager;
        private float _lookupTimer;
        private bool _appliedThisMatch;
        private readonly System.Random _random = new System.Random();

        public override void OnInitializeMelon()
        {
            _configPath = Path.Combine(MelonEnvironment.UserDataDirectory, "BAPBAPRoundMutators.ini");
            _config = Config.Load(LoggerInstance, _configPath);
            try { _configStamp = File.GetLastWriteTimeUtc(_configPath); } catch { }

            LoggerInstance.Msg($"Round Mutators ready. Enabled={_config.Enabled}, " +
                               $"{_config.EnabledPool().Count} of {All.Length} mutators in the pool.");
        }

        public override void OnUpdate()
        {
            WatchConfig();

            // Cheap: one field read once a match is running, and a slow retry while it is not.
            var mode = CurrentGameMode();
            if (mode == null) { _appliedThisMatch = false; return; }

            if (_appliedThisMatch || !_config.Enabled) return;
            _appliedThisMatch = true;   // set first, so a throw cannot make us retry every frame

            ApplyMutators(mode);
        }

        /// The running match, or null. Cached — repeatedly searching for objects in IL2CPP
        /// allocates on every call and is a known stutter source in this game.
        private GameMode CurrentGameMode()
        {
            try
            {
                if (_gameManager == null)
                {
                    _lookupTimer += Time.unscaledDeltaTime;
                    if (_lookupTimer < 3f) return null;
                    _lookupTimer = 0f;

                    _gameManager = UnityEngine.Object.FindObjectOfType<GameManager>();
                    if (_gameManager == null) return null;
                }

                return _gameManager.currentGameMode;
            }
            catch
            {
                _gameManager = null;
                return null;
            }
        }

        private void ApplyMutators(GameMode mode)
        {
            try
            {
                // Only the host may set these. On a guest the call would do nothing anyway, but
                // being explicit keeps the intent obvious.
                if (!Il2CppMirror.NetworkServer.active)
                {
                    LoggerInstance.Msg("Not hosting — leaving modifiers to the host.");
                    return;
                }

                var pool = _config.EnabledPool();
                if (pool.Count == 0)
                {
                    LoggerInstance.Msg("No mutators switched on — nothing to do.");
                    return;
                }

                int want = Math.Max(1, Math.Min(_config.HowManyAtOnce, pool.Count));
                var chosen = Choose(pool, want);

                mode.enableGameModifiers = true;

                var names = new List<string>();
                foreach (var mutator in chosen)
                {
                    try
                    {
                        mode.SvAddGameModifier(mutator.Id);
                        names.Add(mutator.Label);
                        LoggerInstance.Msg($"  applied [{mutator.Id}] {mutator.Label}");
                    }
                    catch (Exception ex)
                    {
                        LoggerInstance.Warning($"  FAILED [{mutator.Id}] {mutator.Label}: {ex.Message}");
                    }
                }

                LoggerInstance.Msg(names.Count > 0
                    ? $"This match: {string.Join(", ", names)}"
                    : "No mutators applied — the game refused every one.");
            }
            catch (Exception ex)
            {
                LoggerInstance.Error($"Could not apply mutators: {ex}");
            }
        }

        /// Random pick without repeats, unless the config says take them in order.
        private List<Mutator> Choose(List<Mutator> pool, int count)
        {
            var picked = new List<Mutator>();

            if (!_config.RandomEachMatch)
            {
                for (int i = 0; i < count && i < pool.Count; i++) picked.Add(pool[i]);
                return picked;
            }

            var remaining = new List<Mutator>(pool);
            while (picked.Count < count && remaining.Count > 0)
            {
                int i = _random.Next(remaining.Count);
                picked.Add(remaining[i]);
                remaining.RemoveAt(i);
            }
            return picked;
        }

        /// Edits to the .ini apply without a restart. Timestamp check only — no read unless it moved.
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
                _config = Config.Load(LoggerInstance, _configPath);
                LoggerInstance.Msg($"Settings reloaded — {_config.EnabledPool().Count} mutator(s) in the pool.");
            }
            catch { }
        }
    }

    public class Config
    {
        public bool Enabled = true;
        public int HowManyAtOnce = 1;
        public bool RandomEachMatch = true;

        /// Which mutators the host allows, keyed by Mutator.Key.
        public readonly Dictionary<string, bool> Pool = new Dictionary<string, bool>(StringComparer.Ordinal);

        public List<Mutator> EnabledPool()
        {
            var list = new List<Mutator>();
            foreach (var mutator in RoundMutatorsMod.All)
                if (Pool.TryGetValue(mutator.Key, out bool on) && on) list.Add(mutator);
            return list;
        }

        public static Config Load(MelonLogger.Instance log, string path)
        {
            var config = new Config();

            // Default pool: the ones people actually notice. Everything else is off until asked
            // for, because half the list is a stat tweak nobody sees mid-fight.
            foreach (var mutator in RoundMutatorsMod.All)
                config.Pool[mutator.Key] =
                    mutator.Key == "MeteorShower" || mutator.Key == "AllGigantic" ||
                    mutator.Key == "NightTime"    || mutator.Key == "XCOM";

            try
            {
                if (!File.Exists(path))
                {
                    Save(config, path);
                    log.Msg($"Created default config at {path}");
                    return config;
                }

                var seen = new HashSet<string>(StringComparer.Ordinal);

                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;

                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;

                    string key = line.Substring(0, eq).Trim();
                    string value = line.Substring(eq + 1).Trim();
                    seen.Add(key);

                    if (key == "Enabled") config.Enabled = IsTrue(value);
                    else if (key == "RandomEachMatch") config.RandomEachMatch = IsTrue(value);
                    else if (key == "HowManyAtOnce")
                    {
                        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
                            config.HowManyAtOnce = Math.Max(1, Math.Min(n, RoundMutatorsMod.All.Length));
                    }
                    else if (key.StartsWith("Use_"))
                    {
                        config.Pool[key.Substring(4)] = IsTrue(value);
                    }
                }

                // An ini written by an older build is missing anything added since, and those
                // settings would stay invisible in the mod manager. Fill the gaps once.
                var missing = new List<string>();
                foreach (string key in KnownKeys()) if (!seen.Contains(key)) missing.Add(key);

                if (missing.Count > 0)
                {
                    try
                    {
                        Save(config, path);
                        log.Msg($"Config updated with {missing.Count} new setting(s).");
                    }
                    catch (Exception ex)
                    {
                        log.Warning($"Could not update the config ({ex.Message}).");
                    }
                }
            }
            catch (Exception ex)
            {
                log.Warning($"Config load failed ({ex.Message}) — using defaults.");
            }

            return config;
        }

        private static IEnumerable<string> KnownKeys()
        {
            yield return "Enabled";
            yield return "HowManyAtOnce";
            yield return "RandomEachMatch";
            foreach (var mutator in RoundMutatorsMod.All) yield return "Use_" + mutator.Key;
        }

        private static bool IsTrue(string value) =>
            value.Equals("true", StringComparison.OrdinalIgnoreCase);

        private static void Save(Config config, string path)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# BAPBAP Round Mutators");
            sb.AppendLine("# HOST-SIDE: only the hosting machine reads this, and the game itself");
            sb.AppendLine("# tells everyone else. Guests do not need this mod installed.");
            sb.AppendLine();
            sb.AppendLine("# Turn the whole mod on or off.");
            sb.AppendLine($"Enabled={config.Enabled}");
            sb.AppendLine();
            sb.AppendLine("# How many mutators run at the same time.");
            sb.AppendLine($"HowManyAtOnce={config.HowManyAtOnce}");
            sb.AppendLine();
            sb.AppendLine("# On: pick at random each match. Off: always use the first ones in the list.");
            sb.AppendLine($"RandomEachMatch={config.RandomEachMatch}");
            sb.AppendLine();
            sb.AppendLine("# Which mutators are allowed to come up.");
            foreach (var mutator in RoundMutatorsMod.All)
            {
                config.Pool.TryGetValue(mutator.Key, out bool on);
                sb.AppendLine($"# {mutator.Label}");
                sb.AppendLine($"Use_{mutator.Key}={on}");
            }

            File.WriteAllText(path, sb.ToString());
        }
    }
}
