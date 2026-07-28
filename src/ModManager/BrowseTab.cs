// The Browse tab: what you can install, as opposed to what you already have.
//
// State lives here; NativePage renders it. The two are separated because the network is
// asynchronous and the UI is not — a fetch starts, the tab keeps drawing "Loading…", and the
// result arrives later on the main thread via MainThread.Post.
//
// Screens:
//   List     — every package from every source, with Install / Update / Installed
//   Confirm  — what you are about to install, its scope, and any requirement warnings
//   Busy     — a fetch or install in progress
//
// Requirement warnings are rendered from the catalog's own requirements[] and are never
// hardcoded. This is what stops a Boss Rush mod being installed onto the wrong game build
// without the user having deliberately acknowledged it.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MelonLoader;

namespace BapbapMods.Manager
{
    public enum BrowseScreen { List, Confirm, Busy }

    public class BrowseTab
    {
        private MelonLogger.Instance _log;
        private string _userDataDir;
        private string _gameRoot;

        /// The mods MelonLoader actually loaded. A receipt only exists for mods installed
        /// through this manager, so without this every hand-installed mod looked missing.
        private Func<List<ModEntry>> _installedMods;

        public BrowseScreen Screen { get; private set; } = BrowseScreen.List;

        /// Null until the first fetch completes.
        public List<CatalogPackage> Packages { get; private set; }
        public Dictionary<string, InstallReceipt> Installed { get; private set; }
            = new Dictionary<string, InstallReceipt>(StringComparer.OrdinalIgnoreCase);

        public string Status { get; private set; } = "";
        public string Error { get; private set; }
        public List<string> Notes { get; private set; } = new List<string>();

        /// The package the Confirm screen is about.
        public CatalogPackage Pending { get; private set; }
        public VersionManifest PendingVersion { get; private set; }

        public bool Loading { get; private set; }
        private CancellationTokenSource _cancel;

        /// Raised whenever anything the UI draws has changed, so the page can rebuild.
        public Action OnChanged;

        public void Init(MelonLogger.Instance log, string userDataDir, string gameRoot,
                         Func<List<ModEntry>> installedMods = null)
        {
            _log = log;
            _userDataDir = userDataDir;
            _gameRoot = gameRoot;
            _installedMods = installedMods;
        }

        /// Names are compared loosely because the same mod is named differently in the two
        /// places: BAPHub's catalog calls it "BAPBAP More Custom Settings", its assembly
        /// announces "MoreCustomSettings". Strip case, spaces and the BAPBAP prefix.
        internal static string NormaliseName(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";

            var sb = new System.Text.StringBuilder(value.Length);
            foreach (char c in value)
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));

            string s = sb.ToString();
            if (s.StartsWith("bapbap")) s = s.Substring(6);
            return s;
        }

        /// The loaded mod matching this package, or null.
        private ModEntry MatchLoaded(CatalogPackage package)
        {
            if (package == null || _installedMods == null) return null;

            var entries = _installedMods();
            if (entries == null) return null;

            string wanted = NormaliseName(package.Name);
            if (wanted.Length == 0) return null;

            foreach (var entry in entries)
            {
                if (NormaliseName(entry.DisplayName) == wanted) return entry;
                if (NormaliseName(entry.Id) == wanted) return entry;
                if (!string.IsNullOrEmpty(entry.DllName) &&
                    NormaliseName(Path.GetFileNameWithoutExtension(entry.DllName)) == wanted) return entry;
            }
            return null;
        }

        private void Changed() => OnChanged?.Invoke();

        // ---- state helpers used by the renderer -------------------------------------

        public bool IsInstalled(CatalogPackage p) =>
            p != null && (Installed.ContainsKey(p.Id) || MatchLoaded(p) != null);

        /// True when we hold an older version than the catalog offers.
        public bool HasUpdate(CatalogPackage p)
        {
            if (p == null || string.IsNullOrEmpty(p.LatestVersion)) return false;

            if (Installed.TryGetValue(p.Id, out var receipt))
                return !string.Equals(receipt.Version, p.LatestVersion, StringComparison.OrdinalIgnoreCase);

            // Installed by hand: compare against what the assembly reports.
            var loaded = MatchLoaded(p);
            if (loaded != null && !string.IsNullOrEmpty(loaded.Version))
                return !string.Equals(loaded.Version, p.LatestVersion, StringComparison.OrdinalIgnoreCase);

            return false;
        }

        public string ActionLabel(CatalogPackage p)
        {
            if (HasUpdate(p)) return "UPDATE";
            if (IsInstalled(p)) return "REMOVE";
            return "INSTALL";
        }

        /// Warnings worth showing in the list, most severe first.
        public static List<CatalogRequirement> WarningsFor(CatalogPackage p)
        {
            var list = new List<CatalogRequirement>();
            if (p == null) return list;

            foreach (var r in p.Requirements)
                if (r.IsBlocking) list.Add(r);
            foreach (var r in p.Requirements)
                if (!r.IsBlocking) list.Add(r);

            return list;
        }

        public string ScopeLabel(CatalogPackage p)
        {
            if (p == null) return "";
            if (!p.ScopeKnown) return "scope unknown";
            return p.Scope == ModCategory.HostOnly ? "affects your whole lobby" : "affects only you";
        }

        // ---- loading ----------------------------------------------------------------

        /// Fetches the catalog. Safe to call repeatedly; a second call while loading is ignored.
        public void Load(bool force = false)
        {
            if (Loading) return;
            if (Packages != null && !force) return;

            Loading = true;
            Error = null;
            Notes.Clear();
            Status = "Loading catalog…";
            Installed = ModInstaller.AllReceipts(_userDataDir);
            Changed();

            _cancel?.Cancel();
            _cancel = new CancellationTokenSource();
            var token = _cancel.Token;

            Task.Run(async () =>
            {
                var problems = new List<string>();
                try
                {
                    var sources = Catalog.LoadSources(_userDataDir);
                    var result = await CatalogFetcher
                        .FetchCatalogAsync(sources, problems, token).ConfigureAwait(false);

                    MainThread.Post(() =>
                    {
                        Loading = false;
                        if (!result.Ok)
                        {
                            Error = result.Error;
                            Status = "";
                        }
                        else
                        {
                            Packages = result.Value;
                            Notes = problems;
                            Status = $"{Packages.Count} mod(s) available";
                        }
                        Changed();
                    });
                }
                catch (Exception ex)
                {
                    MainThread.Post(() =>
                    {
                        Loading = false;
                        Error = ex.Message;
                        Changed();
                    });
                }
            });
        }

        // ---- the install flow -------------------------------------------------------

        /// Step one: fetch the version manifest so the confirmation can state exactly what
        /// will be written before the user agrees to anything.
        public void BeginInstall(CatalogPackage package)
        {
            if (package == null || Loading) return;

            Pending = package;
            PendingVersion = null;
            Screen = BrowseScreen.Busy;
            Status = $"Checking {package.Name}…";
            Error = null;
            Changed();

            Task.Run(async () =>
            {
                var result = await CatalogFetcher.FetchVersionAsync(package).ConfigureAwait(false);

                MainThread.Post(() =>
                {
                    if (!result.Ok)
                    {
                        Screen = BrowseScreen.List;
                        Error = $"{package.Name}: {result.Error}";
                        Status = "";
                        Changed();
                        return;
                    }

                    // Refuse early and visibly rather than at write time.
                    if (!Catalog.IsInstallable(_gameRoot, result.Value, out string why))
                    {
                        Screen = BrowseScreen.List;
                        Error = $"{package.Name} refused: {why}";
                        Status = "";
                        Changed();
                        return;
                    }

                    PendingVersion = result.Value;
                    Screen = BrowseScreen.Confirm;
                    Status = "";
                    Changed();
                });
            });
        }

        /// Step two: the user agreed. Download, verify, write.
        public void ConfirmInstall()
        {
            var package = Pending;
            var version = PendingVersion;
            if (package == null || version == null) return;

            Screen = BrowseScreen.Busy;
            Status = $"Downloading {package.Name} {version.Version}…";
            Error = null;
            Changed();

            Task.Run(async () =>
            {
                var report = await ModInstaller
                    .InstallAsync(package, version, _gameRoot, _userDataDir).ConfigureAwait(false);

                MainThread.Post(() =>
                {
                    Screen = BrowseScreen.List;
                    Pending = null;
                    PendingVersion = null;

                    if (report.Ok)
                    {
                        Status = report.Message;
                        Installed = ModInstaller.AllReceipts(_userDataDir);
                        _log?.Msg($"[browse] {report.Message}");
                    }
                    else
                    {
                        Error = report.Message;
                        Status = "";
                        _log?.Warning($"[browse] install failed: {report.Message}");
                    }
                    Changed();
                });
            });
        }

        public void CancelConfirm()
        {
            Pending = null;
            PendingVersion = null;
            Screen = BrowseScreen.List;
            Status = "";
            Changed();
        }

        // ---- uninstall --------------------------------------------------------------

        /// Synchronous: it only deletes local files, so there is nothing to wait for.
        public void Uninstall(CatalogPackage package, bool deleteSettings = false)
        {
            if (package == null) return;

            // No receipt means it was installed by hand. We still know exactly which file it
            // is — the loaded assembly names itself — so write a receipt for that one DLL and
            // remove it through the same validated path as anything else.
            if (!Installed.ContainsKey(package.Id))
            {
                var loaded = MatchLoaded(package);
                if (loaded == null || string.IsNullOrEmpty(loaded.DllName))
                {
                    Error = $"{package.Name}: cannot tell which file to remove.";
                    Changed();
                    return;
                }

                ModInstaller.WriteReceipt(_userDataDir, new InstallReceipt
                {
                    PackageId = package.Id,
                    Name = package.Name,
                    Version = loaded.Version ?? "",
                    SourceId = package.SourceId,
                    Files = new List<string> { Path.Combine(_gameRoot, "Mods", loaded.DllName) }
                });
            }

            var report = ModInstaller.Uninstall(package.Id, _gameRoot, _userDataDir, deleteSettings);

            if (report.Ok) { Status = report.Message; Error = null; }
            else { Error = report.Message; Status = ""; }

            Installed = ModInstaller.AllReceipts(_userDataDir);
            _log?.Msg($"[browse] {report.Message}");
            Changed();
        }

        public void Dispose()
        {
            try { _cancel?.Cancel(); } catch { }
        }
    }
}
