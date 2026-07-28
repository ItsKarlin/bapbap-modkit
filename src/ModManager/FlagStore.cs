// Flag persistence for mod-manager toggles.
//
// Deliberately dependency-free: a hand-rolled reader/writer over a small JSON file beats
// pulling a JSON library into an Il2Cpp mod. The file is shared with the individual
// mods, which read their own flag the same way.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace BapbapMods.Manager
{
    public class FlagStore
    {
        private readonly string _path;
        private readonly Dictionary<string, bool> _flags = new Dictionary<string, bool>();

        public FlagStore(string userDataDir)
        {
            _path = Path.Combine(userDataDir, "BAPBAPMods.flags.json");
            Load();
        }

        public bool Get(string id, bool fallback) => _flags.TryGetValue(id, out var v) ? v : fallback;

        public void Set(string id, bool value)
        {
            _flags[id] = value;
            Save();
        }

        public void Load()
        {
            _flags.Clear();
            try
            {
                if (!File.Exists(_path)) return;

                string text = File.ReadAllText(_path);
                foreach (Match m in Regex.Matches(text, "\"([A-Za-z0-9_.\\-]+)\"\\s*:\\s*(true|false)",
                                                  RegexOptions.IgnoreCase))
                {
                    string key = m.Groups[1].Value;
                    if (key.Equals("_comment", StringComparison.OrdinalIgnoreCase)) continue;
                    _flags[key] = m.Groups[2].Value.Equals("true", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                // A malformed config must never take the game down. Defaults apply.
            }
        }

        private void Save()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("{");
                sb.AppendLine("  \"_comment\": \"Mod manager flags. Managed in-game; safe to hand-edit.\",");
                sb.AppendLine("  \"EXPERIMENTS\": {");

                int i = 0;
                foreach (var kv in _flags)
                {
                    string comma = (++i < _flags.Count) ? "," : "";
                    sb.AppendLine($"    \"{kv.Key}\": {(kv.Value ? "true" : "false")}{comma}");
                }

                sb.AppendLine("  }");
                sb.AppendLine("}");

                // Write via temp + move so a crash mid-write can't leave a truncated config.
                string tmp = _path + ".tmp";
                File.WriteAllText(tmp, sb.ToString());
                if (File.Exists(_path)) File.Delete(_path);
                File.Move(tmp, _path);
            }
            catch
            {
                // Non-fatal: the toggle stays live for this session even if persistence fails.
            }
        }
    }
}
