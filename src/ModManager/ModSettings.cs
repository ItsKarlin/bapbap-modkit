// GENERIC settings discovery. Nothing here is specific to any one mod.
//
// How a mod gets settings in the manager, in order of preference:
//
//   1. A descriptor file: UserData/<ModDll>.settings.json
//      Written by the mod author. Gives proper labels, ranges, scope and ordering.
//      This is the documented way to expose settings — see the repo README.
//
//   2. Its plain .ini: UserData/<ModDll>.ini
//      MelonLoader mods conventionally name their config after the assembly. We parse the
//      key=value pairs and infer types: True/False -> switch, numeric -> number, else text.
//      This means mods get usable options with NO extra work from their author.
//
// If neither exists the mod simply has no options, and the manager says so rather than
// pretending otherwise.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using MelonLoader.Utils;

namespace BapbapMods.Manager
{
    public enum SettingKind
    {
        Float,
        Bool,
        Text
    }

    public class SettingDescriptor
    {
        public string IniFile;
        public string Key;
        public string Label;
        public string Description;
        public SettingKind Kind;
        public ModCategory Scope = ModCategory.ClientSide;

        public float Min = 0f;
        public float Max = 100f;
        public float Step = 0.1f;

        public string RawValue;
    }

    public static class ModSettings
    {
        /// Settings for one mod, discovered from its own files.
        public static List<SettingDescriptor> For(ModEntry mod)
        {
            var result = new List<SettingDescriptor>();
            if (mod == null || string.IsNullOrEmpty(mod.DllName)) return result;

            string stem = Path.GetFileNameWithoutExtension(mod.DllName);

            var described = FromDescriptor(stem);
            if (described.Count > 0) return described;

            return FromIni(stem);
        }

        public static bool HasSettings(ModEntry mod) => For(mod).Count > 0;

        // ---- 1. author-provided descriptor ------------------------------------------

        /// Minimal hand-rolled JSON reader — enough for a flat array of setting objects,
        /// and avoids dragging a JSON library into an Il2Cpp mod.
        private static List<SettingDescriptor> FromDescriptor(string stem)
        {
            var list = new List<SettingDescriptor>();
            try
            {
                string path = Path.Combine(MelonEnvironment.UserDataDirectory, stem + ".settings.json");
                if (!File.Exists(path)) return list;

                string text = File.ReadAllText(path);
                string ini = stem + ".ini";

                foreach (string chunk in SplitObjects(text))
                {
                    string key = JsonValue(chunk, "key");
                    if (string.IsNullOrEmpty(key)) continue;

                    var d = new SettingDescriptor
                    {
                        IniFile = ini,
                        Key = key,
                        Label = JsonValue(chunk, "label") ?? key,
                        Description = JsonValue(chunk, "description") ?? "",
                    };

                    string type = (JsonValue(chunk, "type") ?? "").ToLowerInvariant();
                    d.Kind = type == "bool" ? SettingKind.Bool
                           : type == "text" ? SettingKind.Text
                           : SettingKind.Float;

                    string scope = (JsonValue(chunk, "scope") ?? "client").ToLowerInvariant();
                    d.Scope = scope == "host" ? ModCategory.HostOnly : ModCategory.ClientSide;

                    d.Min = ParseFloat(JsonValue(chunk, "min"), 0f);
                    d.Max = ParseFloat(JsonValue(chunk, "max"), 100f);
                    d.Step = ParseFloat(JsonValue(chunk, "step"), Math.Max(0.05f, (d.Max - d.Min) / 20f));

                    d.RawValue = IniStore.ReadRaw(ini, key, "");
                    list.Add(d);
                }
            }
            catch
            {
            }
            return list;
        }

        // ---- 2. plain .ini, types inferred ------------------------------------------

        private static List<SettingDescriptor> FromIni(string stem)
        {
            var list = new List<SettingDescriptor>();
            try
            {
                string ini = stem + ".ini";
                string path = Path.Combine(MelonEnvironment.UserDataDirectory, ini);
                if (!File.Exists(path)) return list;

                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;

                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;

                    string key = line.Substring(0, eq).Trim();
                    string value = line.Substring(eq + 1).Trim();
                    if (key.Length == 0) continue;

                    var d = new SettingDescriptor
                    {
                        IniFile = ini,
                        Key = key,
                        Label = Humanise(key),
                        RawValue = value
                    };

                    if (value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                        value.Equals("false", StringComparison.OrdinalIgnoreCase))
                    {
                        d.Kind = SettingKind.Bool;
                    }
                    else if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
                    {
                        d.Kind = SettingKind.Float;

                        // No range information available, so derive something sane from the
                        // current value rather than inventing arbitrary limits.
                        float magnitude = Math.Abs(n);
                        d.Min = 0f;
                        d.Max = magnitude <= 1f ? 2f
                              : magnitude <= 10f ? 20f
                              : magnitude <= 100f ? 200f
                              : magnitude * 2f;
                        d.Step = magnitude <= 2f ? 0.1f : magnitude <= 50f ? 1f : 5f;
                    }
                    else
                    {
                        d.Kind = SettingKind.Text; // shown read-only
                    }

                    list.Add(d);
                }
            }
            catch
            {
            }
            return list;
        }

        // ---- helpers ---------------------------------------------------------------

        /// "EnableCrashForensics" -> "Enable crash forensics"
        private static string Humanise(string key)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < key.Length; i++)
            {
                char c = key[i];
                if (i > 0 && char.IsUpper(c) && !char.IsUpper(key[i - 1])) sb.Append(' ');
                sb.Append(i == 0 ? char.ToUpper(c) : char.ToLower(c));
            }
            return sb.ToString();
        }

        private static float ParseFloat(string s, float fallback)
        {
            if (string.IsNullOrEmpty(s)) return fallback;
            return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
        }

        private static IEnumerable<string> SplitObjects(string json)
        {
            int depth = 0, start = -1;
            for (int i = 0; i < json.Length; i++)
            {
                if (json[i] == '{')
                {
                    if (depth == 0) start = i;
                    depth++;
                }
                else if (json[i] == '}')
                {
                    depth--;
                    if (depth == 0 && start >= 0) yield return json.Substring(start, i - start + 1);
                }
            }
        }

        private static string JsonValue(string obj, string key)
        {
            string needle = "\"" + key + "\"";
            int k = obj.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
            if (k < 0) return null;

            int colon = obj.IndexOf(':', k + needle.Length);
            if (colon < 0) return null;

            int i = colon + 1;
            while (i < obj.Length && char.IsWhiteSpace(obj[i])) i++;
            if (i >= obj.Length) return null;

            if (obj[i] == '"')
            {
                int end = obj.IndexOf('"', i + 1);
                return end < 0 ? null : obj.Substring(i + 1, end - i - 1);
            }

            int stop = i;
            while (stop < obj.Length && obj[stop] != ',' && obj[stop] != '}') stop++;
            return obj.Substring(i, stop - i).Trim();
        }
    }
}
