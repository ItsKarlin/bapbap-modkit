// The downloader's data layer: what mods exist, where their files are, and whether a given
// file is safe to write.
//
// Parsing uses Newtonsoft.Json, which MelonLoader already ships in the same net6 runtime this
// mod runs in. The hand-rolled reader in ModSettings.cs is fine for a flat ini descriptor but
// cannot read a catalog: it finds the first "key" by string search inside a brace-split chunk,
// so a package containing nested authors[] or requirements[] would have an author's id read as
// the package id.
//
// NOTHING about any specific source lives in this file. BAPHub is described by a SourceDescriptor
// loaded from sources.json like any other source, so adding a third catalog is a data change.

using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;

namespace BapbapMods.Manager
{
    /// Where a catalog lives and how to build paths within it.
    public class SourceDescriptor
    {
        public string SourceId;
        public string DisplayName;

        /// Everything else resolves against this. Always ends with '/'.
        public string BaseUrl;

        /// Relative to BaseUrl. The browse list.
        public string PackagesPath = "packages.json";

        /// Used when a package does not carry an explicit versionManifestPath.
        /// Supports {id} and {version}.
        public string VersionManifestTemplate;

        public bool Enabled = true;

        /// Package ids to drop from this source. Data, not code, so hiding something never
        /// means a new build.
        public List<string> Exclude = new List<string>();
    }

    public class CatalogRequirement
    {
        public string Type;
        public string Text;
        public string Severity = "info";

        public bool IsBlocking => string.Equals(Severity, "error", StringComparison.OrdinalIgnoreCase);
    }

    public class CatalogPackage
    {
        public string Id;
        public string Name;
        public string Summary;
        public string Author;
        public string LatestVersion;
        public List<string> Tags = new List<string>();
        public List<CatalogRequirement> Requirements = new List<CatalogRequirement>();

        /// Unknown unless the source states it. BAPHub has no scope concept, so its packages
        /// arrive Unknown and are presented exactly like an unrecognised installed mod.
        public ModCategory Scope = ModCategory.Unknown;
        public bool ScopeKnown;

        /// Filled in from the descriptor the package came from.
        public string SourceId;
        public string SourceDisplayName;
        public string BaseUrl;

        public string VersionManifestPath;

        public string VersionManifestUrl => Combine(BaseUrl, VersionManifestPath);

        internal static string Combine(string baseUrl, string relative)
        {
            if (string.IsNullOrEmpty(relative)) return null;
            if (relative.StartsWith("http://") || relative.StartsWith("https://")) return relative;
            if (string.IsNullOrEmpty(baseUrl)) return relative;
            return baseUrl.EndsWith("/") ? baseUrl + relative : baseUrl + "/" + relative;
        }
    }

    public class CatalogFileEntry
    {
        public string SourcePath;
        public string TargetPath;
        public string Sha256;
        public string Description;
    }

    public class VersionManifest
    {
        public string Id;
        public string Version;

        /// The folder version.json itself lives in. sourcePath entries are relative to THIS,
        /// not to the catalog root — BAPHub stores payloads next to each version manifest.
        public string BaseUrl;
        public List<CatalogFileEntry> Files = new List<CatalogFileEntry>();
    }

    public static class Catalog
    {
        /// Only these roots may ever be written to. A catalog is remote data; without a
        /// whitelist a malicious targetPath could drop a DLL next to the executable.
        private static readonly string[] AllowedRoots = { "Mods", "UserData" };

        // ---- parsing ---------------------------------------------------------------

        /// Reads a browse list. Handles both our catalog.json and BAPHub's packages.json —
        /// the difference is which optional fields are present, not the shape.
        public static List<CatalogPackage> ParsePackages(string json, SourceDescriptor source)
        {
            var result = new List<CatalogPackage>();
            if (string.IsNullOrEmpty(json) || source == null) return result;

            JObject root;
            try { root = JObject.Parse(json); }
            catch { return result; }

            // A source may override its own display name and base url.
            string baseUrl = Str(root["baseUrl"]) ?? source.BaseUrl;
            string display = Str(root["displayName"]) ?? source.DisplayName;

            var packages = root["packages"] as JArray;
            if (packages == null) return result;

            foreach (var node in packages)
            {
                var obj = node as JObject;
                if (obj == null) continue;

                string id = Str(obj["id"]);
                if (string.IsNullOrEmpty(id)) continue;
                if (IsExcluded(source, id)) continue;

                var pkg = new CatalogPackage
                {
                    Id = id,
                    Name = Str(obj["name"]) ?? id,
                    Summary = Str(obj["summary"]) ?? "",
                    LatestVersion = Str(obj["latestVersion"]) ?? "",
                    SourceId = source.SourceId,
                    SourceDisplayName = display,
                    BaseUrl = baseUrl
                };

                // Author may be a plain string (ours) or an authors[] array (BAPHub).
                pkg.Author = Str(obj["author"]);
                if (string.IsNullOrEmpty(pkg.Author))
                {
                    var authors = obj["authors"] as JArray;
                    if (authors != null && authors.Count > 0)
                        pkg.Author = Str(authors[0]?["name"]) ?? Str(authors[0]?["id"]);
                }
                if (string.IsNullOrEmpty(pkg.Author))
                    pkg.Author = Str(obj["owner"]?["name"]);

                string scope = Str(obj["scope"]);
                if (!string.IsNullOrEmpty(scope))
                {
                    pkg.ScopeKnown = true;
                    pkg.Scope = scope.Equals("host", StringComparison.OrdinalIgnoreCase)
                        ? ModCategory.HostOnly
                        : ModCategory.ClientSide;
                }

                var tags = obj["tags"] as JArray;
                if (tags != null)
                    foreach (var t in tags)
                    {
                        string tag = Str(t);
                        if (!string.IsNullOrEmpty(tag)) pkg.Tags.Add(tag);
                    }

                pkg.Requirements = ParseRequirements(obj["requirements"] as JArray);

                // Explicit path wins; otherwise build one from the source's template.
                pkg.VersionManifestPath = Str(obj["versionManifestPath"]);
                if (string.IsNullOrEmpty(pkg.VersionManifestPath) &&
                    !string.IsNullOrEmpty(source.VersionManifestTemplate) &&
                    !string.IsNullOrEmpty(pkg.LatestVersion))
                {
                    pkg.VersionManifestPath = source.VersionManifestTemplate
                        .Replace("{id}", pkg.Id)
                        .Replace("{version}", pkg.LatestVersion);
                }

                result.Add(pkg);
            }

            return result;
        }

        /// Shipped inside the manager so the downloader works on a fresh install with no
        /// bootstrap request. A user file overrides it; see LoadSources.
        public const string DefaultSourcesJson = @"{
  ""schemaVersion"": 1,
  ""sources"": [
    { ""sourceId"": ""modkit"", ""displayName"": ""Modkit"",
      ""baseUrl"": ""https://raw.githubusercontent.com/ItsKarlin/bapbap-modkit/main/"",
      ""packagesPath"": ""catalog/catalog.json"", ""enabled"": true },
    { ""sourceId"": ""baphub"", ""displayName"": ""BAPHub"",
      ""baseUrl"": ""https://raw.githubusercontent.com/Sonic0810/BAPBAPLauncher/main/manifest/channels/release/"",
      ""packagesPath"": ""packages.json"",
      ""versionManifestTemplate"": ""{id}/versions/{version}/version.json"", ""enabled"": true,
      ""exclude"": [ ""sonic.bapbap.br-ui-old-but-gold"", ""sonic.bapbap.fps-camera"" ] }
  ]
}";

        /// The sources the downloader will use. A file at
        /// UserData/bapbap-catalog-sources.json wins, so anyone can add a source, reorder trust
        /// or disable one without a new build. Falls back to the shipped defaults.
        public static List<SourceDescriptor> LoadSources(string userDataDir)
        {
            try
            {
                string path = Path.Combine(userDataDir, "bapbap-catalog-sources.json");
                if (File.Exists(path))
                {
                    var custom = ParseSources(File.ReadAllText(path));
                    if (custom.Count > 0) return custom;
                }
            }
            catch { }

            return ParseSources(DefaultSourcesJson);
        }

        private static bool IsExcluded(SourceDescriptor source, string packageId)
        {
            if (source.Exclude == null) return false;
            foreach (string excluded in source.Exclude)
                if (string.Equals(excluded, packageId, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// Reads sources.json. Order is trust order — see Merge.
        public static List<SourceDescriptor> ParseSources(string json)
        {
            var list = new List<SourceDescriptor>();
            if (string.IsNullOrEmpty(json)) return list;

            JObject root;
            try { root = JObject.Parse(json); }
            catch { return list; }

            var sources = root["sources"] as JArray;
            if (sources == null) return list;

            foreach (var node in sources)
            {
                var obj = node as JObject;
                if (obj == null) continue;

                string id = Str(obj["sourceId"]);
                string baseUrl = Str(obj["baseUrl"]);
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(baseUrl)) continue;

                if (!baseUrl.EndsWith("/")) baseUrl += "/";

                var enabled = obj["enabled"];

                var descriptor = new SourceDescriptor
                {
                    SourceId = id,
                    DisplayName = Str(obj["displayName"]) ?? id,
                    BaseUrl = baseUrl,
                    PackagesPath = Str(obj["packagesPath"]) ?? "packages.json",
                    VersionManifestTemplate = Str(obj["versionManifestTemplate"]),
                    Enabled = enabled == null || enabled.Type != JTokenType.Boolean || (bool)enabled
                };

                if (obj["exclude"] is JArray excluded)
                    foreach (var e in excluded)
                    {
                        string excludedId = Str(e);
                        if (!string.IsNullOrEmpty(excludedId)) descriptor.Exclude.Add(excludedId);
                    }

                list.Add(descriptor);
            }

            return list;
        }

        /// Requirements can also live in a package's own package.json, so this is public.
        public static List<CatalogRequirement> ParseRequirements(JArray array)
        {
            var list = new List<CatalogRequirement>();
            if (array == null) return list;

            foreach (var node in array)
            {
                var obj = node as JObject;
                if (obj == null) continue;

                string text = Str(obj["text"]);
                if (string.IsNullOrEmpty(text)) continue;

                list.Add(new CatalogRequirement
                {
                    Type = Str(obj["type"]) ?? "",
                    Text = text,
                    Severity = Str(obj["severity"]) ?? "info"
                });
            }
            return list;
        }

        public static VersionManifest ParseVersion(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;

            JObject root;
            try { root = JObject.Parse(json); }
            catch { return null; }

            var manifest = new VersionManifest
            {
                Id = Str(root["id"]),
                Version = Str(root["version"])
            };

            var files = root["files"] as JArray;
            if (files == null) return manifest;

            foreach (var node in files)
            {
                var obj = node as JObject;
                if (obj == null) continue;

                string target = Str(obj["targetPath"]);
                string source = Str(obj["sourcePath"]);
                if (string.IsNullOrEmpty(target) || string.IsNullOrEmpty(source)) continue;

                manifest.Files.Add(new CatalogFileEntry
                {
                    SourcePath = source,
                    TargetPath = target,
                    Sha256 = Str(obj["sha256"]) ?? "",
                    Description = Str(obj["description"]) ?? ""
                });
            }

            return manifest;
        }

        // ---- merging ---------------------------------------------------------------

        /// Merges sources by package id. First source listed wins a collision, so the order of
        /// sources.json is a trust order. Returns the ids that collided so the caller can log.
        public static List<CatalogPackage> Merge(
            IEnumerable<List<CatalogPackage>> perSource, out List<string> collisions)
        {
            var merged = new List<CatalogPackage>();
            var seen = new Dictionary<string, CatalogPackage>(StringComparer.OrdinalIgnoreCase);
            collisions = new List<string>();

            foreach (var list in perSource)
            {
                if (list == null) continue;
                foreach (var pkg in list)
                {
                    if (pkg == null || string.IsNullOrEmpty(pkg.Id)) continue;
                    if (seen.ContainsKey(pkg.Id)) { collisions.Add(pkg.Id); continue; }
                    seen[pkg.Id] = pkg;
                    merged.Add(pkg);
                }
            }

            merged.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return merged;
        }

        // ---- safety ----------------------------------------------------------------

        /// True when targetPath is a relative path that lands inside gameDir, under one of the
        /// allowed roots. Everything a catalog asks us to write goes through here first.
        public static bool IsSafeTargetPath(string gameDir, string targetPath, out string resolved)
        {
            resolved = null;
            if (string.IsNullOrEmpty(gameDir) || string.IsNullOrEmpty(targetPath)) return false;

            // Reject the obvious before touching the filesystem API.
            if (targetPath.IndexOf("..", StringComparison.Ordinal) >= 0) return false;
            if (targetPath.StartsWith("/") || targetPath.StartsWith("\\")) return false;
            if (targetPath.Length >= 2 && targetPath[1] == ':') return false;   // C:\...
            if (targetPath.IndexOf('\0') >= 0) return false;

            string normalised = targetPath.Replace('\\', '/');

            int slash = normalised.IndexOf('/');
            if (slash <= 0) return false;                    // must be <root>/<file>
            string root = normalised.Substring(0, slash);

            bool rootAllowed = false;
            foreach (string allowed in AllowedRoots)
                if (string.Equals(root, allowed, StringComparison.OrdinalIgnoreCase)) rootAllowed = true;
            if (!rootAllowed) return false;

            // Then confirm with the real path, which catches anything the string checks missed.
            try
            {
                string full = Path.GetFullPath(Path.Combine(gameDir, normalised));
                string baseFull = Path.GetFullPath(gameDir);

                if (!baseFull.EndsWith(Path.DirectorySeparatorChar.ToString()))
                    baseFull += Path.DirectorySeparatorChar;

                if (!full.StartsWith(baseFull, StringComparison.OrdinalIgnoreCase)) return false;

                resolved = full;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// A package is installable only if every file it declares is safe and hashed.
        public static bool IsInstallable(string gameDir, VersionManifest manifest, out string reason)
        {
            reason = null;
            if (manifest == null) { reason = "no version manifest"; return false; }
            if (manifest.Files.Count == 0) { reason = "declares no files"; return false; }

            foreach (var file in manifest.Files)
            {
                if (string.IsNullOrEmpty(file.Sha256))
                {
                    reason = $"{file.TargetPath} has no sha256";
                    return false;
                }
                if (!IsSafeTargetPath(gameDir, file.TargetPath, out _))
                {
                    reason = $"unsafe target path: {file.TargetPath}";
                    return false;
                }
            }
            return true;
        }

        private static string Str(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            string s = token.Type == JTokenType.String ? (string)token : token.ToString();
            return string.IsNullOrEmpty(s) ? null : s;
        }
    }
}
