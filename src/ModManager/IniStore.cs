// Reads and writes the key=value .ini files that mods use for their settings.
//
// Writes preserve the rest of the file — comments, ordering, unknown keys — because these
// files are also hand-editable and a mod may store settings the manager knows nothing
// about. Only the targeted line is replaced.

using System;
using System.Globalization;
using System.IO;
using MelonLoader.Utils;

namespace BapbapMods.Manager
{
    public static class IniStore
    {
        private static string PathFor(string iniFile)
            => Path.Combine(MelonEnvironment.UserDataDirectory, iniFile);

        public static string ReadRaw(string iniFile, string key, string fallback)
        {
            try
            {
                string path = PathFor(iniFile);
                if (!File.Exists(path)) return fallback;

                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;

                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;

                    if (line.Substring(0, eq).Trim() == key)
                    {
                        return line.Substring(eq + 1).Trim();
                    }
                }
            }
            catch
            {
            }
            return fallback;
        }

        public static float ReadFloat(string iniFile, string key, float fallback)
        {
            string raw = ReadRaw(iniFile, key, null);
            if (raw == null) return fallback;

            return float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                ? v : fallback;
        }

        public static bool ReadBool(string iniFile, string key, bool fallback)
        {
            string raw = ReadRaw(iniFile, key, null);
            if (raw == null) return fallback;
            return raw.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        public static void Write(string iniFile, string key, string value)
        {
            try
            {
                string path = PathFor(iniFile);
                if (!File.Exists(path)) return; // the owning mod creates its own file

                var lines = File.ReadAllLines(path);
                bool replaced = false;

                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;

                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;

                    if (line.Substring(0, eq).Trim() == key)
                    {
                        lines[i] = $"{key}={value}";
                        replaced = true;
                        break;
                    }
                }

                if (!replaced)
                {
                    var grown = new string[lines.Length + 1];
                    Array.Copy(lines, grown, lines.Length);
                    grown[lines.Length] = $"{key}={value}";
                    lines = grown;
                }

                // Temp + move: a crash mid-write must not truncate a user's settings.
                string tmp = path + ".tmp";
                File.WriteAllLines(tmp, lines);
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch
            {
            }
        }

        public static void WriteFloat(string iniFile, string key, float value)
            => Write(iniFile, key, value.ToString("0.###", CultureInfo.InvariantCulture));

        public static void WriteBool(string iniFile, string key, bool value)
            => Write(iniFile, key, value ? "True" : "False");
    }
}
