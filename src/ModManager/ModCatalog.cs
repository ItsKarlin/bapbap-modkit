// DISCOVERY-BASED catalog.
//
// Previously this file hardcoded a list of known mods and merely checked whether each file
// existed — so anything not written into the table was invisible, and renaming a DLL broke
// its entry. That is a menu pretending to be a mod manager.
//
// Now: enumerate what MelonLoader has actually loaded and read each mod's real identity
// from its MelonInfo attribute (name, version, author) — the same data the mod author
// stamped into the assembly. Disabled mods are picked up by scanning Mods/disabled/.
//
// A small metadata overlay adds the one thing a DLL cannot tell us: whether a mod affects
// the whole lobby or just this client. Nothing in MelonLoader encodes that. Mods absent
// from the overlay still appear — they are simply marked Unknown rather than hidden.

using System;
using System.Collections.Generic;
using System.IO;
using MelonLoader;

namespace BapbapMods.Manager
{
    public enum ModSource
    {
        BapHub,
        Local,
        Unknown
    }

    public enum ModCategory
    {
        /// Affects every player in the lobby when you host. Locked during a match.
        HostOnly,

        /// Affects only your own client. Always safe to toggle.
        ClientSide,

        /// We have no metadata for this mod. Shown and toggleable, but flagged — it may or
        /// may not affect other players.
        Unknown
    }

    public class ModEntry
    {
        public string Id;             // MelonInfo name — stable across file renames
        public string DisplayName;
        public string Version;
        public string Author;
        public string Description;
        public ModSource Source;
        public ModCategory Category;

        public string DllName;
        public bool Enabled;
        public bool Installed = true;

        /// True when this mod has settings the manager can edit.
        public bool HasOptions;

        /// False for a DLL sitting in Mods/ that MelonLoader has not loaded — i.e. one just
        /// downloaded. It is installed, it simply is not running yet.
        public bool Loaded = true;

        public bool RequiresRestart => true; // MelonLoader cannot unload mods mid-session

        public string SourceLabel
        {
            get
            {
                switch (Source)
                {
                    case ModSource.BapHub: return "BAPHub";
                    case ModSource.Local: return "Local";
                    default: return Author ?? "unknown";
                }
            }
        }
    }

    /// The overlay: what we know beyond what a DLL can report. Keyed on MelonInfo name.
    internal class ModMeta
    {
        public ModCategory Category;
        public ModSource Source;
        public string Description;
    }

    public static class ModCatalog
    {
        private static readonly Dictionary<string, ModMeta> Known =
            new Dictionary<string, ModMeta>(StringComparer.OrdinalIgnoreCase)
        {
            { "BAPBAP Hidden Dev Arguments", new ModMeta {
                Category = ModCategory.HostOnly, Source = ModSource.BapHub,
                Description = "Exposes the game's hidden dev switches and item categories." } },
            { "BAPBAP Pool Randomizer", new ModMeta {
                Category = ModCategory.HostOnly, Source = ModSource.BapHub,
                Description = "Rarity-aware item randomizer across vanilla, legacy and hidden pools." } },
            { "BAPBAP Arena Random Chars", new ModMeta {
                Category = ModCategory.HostOnly, Source = ModSource.BapHub,
                Description = "Rotates player characters during arena matches." } },
            { "BAPBAP More Custom Settings", new ModMeta {
                Category = ModCategory.HostOnly, Source = ModSource.BapHub,
                Description = "More custom match and bot settings. Author did not flag its scope; treated as host-side to be safe." } },
            { "BAPBAP HP Numbers", new ModMeta {
                Category = ModCategory.ClientSide, Source = ModSource.BapHub,
                Description = "Live numeric HP on health bars. Your screen only." } },
            { "BAPBAP Asset Dumper", new ModMeta {
                Category = ModCategory.ClientSide, Source = ModSource.BapHub,
                Description = "Dumps assets, icons and sounds to a folder. F8 in-game." } },
            { "BAPBAP Third Person", new ModMeta {
                Category = ModCategory.ClientSide, Source = ModSource.Local,
                Description = "Third-person camera. F1 toggles. Pointer for cards and menus." } },
            { "BAPBAP Mods", new ModMeta {
                Category = ModCategory.ClientSide, Source = ModSource.Local,
                Description = "This mod manager." } }
        };

        /// Scope declared by whatever catalog the mod was installed from, keyed by DLL name.
        /// Consulted before the built-in overlay, so a mod states its own scope and the manager
        /// needs no entry for it. This is what keeps "nothing hardcoded per-mod" true.
        private static Dictionary<string, ModCategory> _declaredScopes =
            new Dictionary<string, ModCategory>(StringComparer.OrdinalIgnoreCase);

        public static void LoadDeclaredScopes(string userDataDir)
        {
            var map = new Dictionary<string, ModCategory>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var pair in ModInstaller.AllReceipts(userDataDir))
                {
                    var receipt = pair.Value;
                    if (receipt == null || string.IsNullOrEmpty(receipt.Scope)) continue;

                    var scope = receipt.Scope.Equals("host", StringComparison.OrdinalIgnoreCase)
                        ? ModCategory.HostOnly : ModCategory.ClientSide;

                    foreach (string file in receipt.Files)
                    {
                        string name = Path.GetFileName(file);
                        if (!string.IsNullOrEmpty(name)) map[name] = scope;
                    }
                }
            }
            catch { }
            _declaredScopes = map;
        }

        public static List<ModEntry> Build(string modsDir, FlagStore flags)
        {
            var list = new List<ModEntry>();
            var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // --- what MelonLoader actually loaded -------------------------------------
            try
            {
                foreach (var melon in MelonMod.RegisteredMelons)
                {
                    if (melon == null || melon.Info == null) continue;

                    string file = "";
                    try
                    {
                        string loc = melon.MelonAssembly?.Location;
                        if (!string.IsNullOrEmpty(loc)) file = Path.GetFileName(loc);
                    }
                    catch { }

                    if (!string.IsNullOrEmpty(file)) seenFiles.Add(file);

                    string name = melon.Info.Name ?? file;
                    var entry = new ModEntry
                    {
                        Id = name,
                        DisplayName = name,
                        Version = melon.Info.Version,
                        Author = melon.Info.Author,
                        DllName = file,
                        Enabled = true,
                        Installed = true
                    };

                    ApplyMeta(entry);
                    entry.HasOptions = ModSettings.For(entry).Count > 0;
                    list.Add(entry);
                }
            }
            catch
            {
                // If enumeration ever fails we still fall through to the disabled scan
                // rather than showing the user an empty manager.
            }

            // --- parked mods (not loaded, so no MelonInfo available) -------------------
            try
            {
                string disabledDir = Path.Combine(modsDir, "disabled");
                if (Directory.Exists(disabledDir))
                {
                    foreach (string path in Directory.GetFiles(disabledDir, "*.dll"))
                    {
                        string file = Path.GetFileName(path);
                        if (seenFiles.Contains(file)) continue;

                        // Not loaded, so the friendly name is unavailable: fall back to the
                        // filename and let the overlay match if it happens to line up.
                        string guess = Path.GetFileNameWithoutExtension(file);
                        var entry = new ModEntry
                        {
                            Id = guess,
                            DisplayName = guess,
                            Version = "",
                            Author = "",
                            DllName = file,
                            Enabled = false,
                            Installed = true
                        };

                        ApplyMeta(entry);
                        entry.HasOptions = ModSettings.For(entry).Count > 0;
                        list.Add(entry);
                    }
                }
            }
            catch
            {
            }

            // --- present but not loaded -----------------------------------------------
            // A mod downloaded this session is in Mods/ but was not there at startup, so it is
            // neither in RegisteredMelons nor in disabled/. Without this it vanishes from the
            // list until the next launch, which reads as a failed install.
            try
            {
                if (Directory.Exists(modsDir))
                {
                    foreach (string path in Directory.GetFiles(modsDir, "*.dll"))
                    {
                        string file = Path.GetFileName(path);
                        if (seenFiles.Contains(file)) continue;

                        string guess = Path.GetFileNameWithoutExtension(file);
                        var entry = new ModEntry
                        {
                            Id = guess,
                            DisplayName = guess,
                            Version = "",
                            Author = "",
                            DllName = file,
                            Enabled = true,
                            Installed = true,
                            Loaded = false
                        };

                        ApplyMeta(entry);
                        entry.Description = "Installed, not running yet - restart to load it. " +
                                            (entry.Description ?? "");
                        entry.HasOptions = ModSettings.For(entry).Count > 0;
                        list.Add(entry);
                        seenFiles.Add(file);
                    }
                }
            }
            catch
            {
            }

            list.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
            return list;
        }

        private static void ApplyMeta(ModEntry entry)
        {
            // What the mod's own catalog entry said, if it was installed through the manager.
            if (!string.IsNullOrEmpty(entry.DllName) &&
                _declaredScopes.TryGetValue(entry.DllName, out var declared))
            {
                entry.Category = declared;
                entry.Source = ModSource.Unknown;
                entry.Description = declared == ModCategory.HostOnly
                    ? "Affects everyone in your lobby when you host."
                    : "Affects only your own screen.";

                if (Known.TryGetValue(entry.Id, out var extra))
                {
                    entry.Source = extra.Source;
                    entry.Description = extra.Description;
                }
                return;
            }

            if (Known.TryGetValue(entry.Id, out var meta))
            {
                entry.Category = meta.Category;
                entry.Source = meta.Source;
                entry.Description = meta.Description;
                return;
            }

            // Unknown mod: show it, allow toggling, and say plainly that we cannot vouch
            // for its scope.
            entry.Category = ModCategory.Unknown;
            entry.Source = ModSource.Unknown;
            entry.Description = string.IsNullOrEmpty(entry.Author)
                ? "Not recognised — scope unknown. It may affect other players in your lobby."
                : $"By {entry.Author}. Not recognised — scope unknown. It may affect other players in your lobby.";
        }
    }
}
