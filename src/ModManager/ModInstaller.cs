// Executes installs and uninstalls.
//
// The rule this file exists to enforce: a mod is either fully installed or not installed at all.
// Every file is downloaded to a staging folder and hash-verified BEFORE anything in the game
// folder is touched. If a move fails halfway, the files already moved are put back.
//
// Uninstall reads an install receipt rather than re-fetching version.json, so removing a mod
// works offline and still works if the catalog changed or disappeared since.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace BapbapMods.Manager
{
    public class InstallReport
    {
        public bool Ok;
        public string Message;
        public List<string> Files = new List<string>();

        /// Always true on success — MelonLoader cannot load an assembly mid-session.
        public bool NeedsRestart;

        public static InstallReport Fail(string why) => new InstallReport { Ok = false, Message = why };
    }

    /// What we wrote, so uninstall doesn't have to guess or go back to the network.
    public class InstallReceipt
    {
        public string PackageId;
        public string Name;
        public string Version;
        public string SourceId;
        public List<string> Files = new List<string>();
    }

    public static class ModInstaller
    {
        /// Signature of the download step, so tests can substitute a local copier for the
        /// network without changing the staging and move logic being tested.
        public delegate Task<FetchResult<string>> Downloader(
            string url, string destination, string sha256, CancellationToken token);

        public static string StagingDir(string userDataDir) =>
            Path.Combine(userDataDir, "bapbap-modkit", "staging");

        public static string ReceiptsDir(string userDataDir) =>
            Path.Combine(userDataDir, "bapbap-modkit", "installed");

        public static string ReceiptPath(string userDataDir, string packageId) =>
            Path.Combine(ReceiptsDir(userDataDir), SafeFileName(packageId) + ".json");

        // ---- install ----------------------------------------------------------------

        public static async Task<InstallReport> InstallAsync(
            CatalogPackage package,
            VersionManifest manifest,
            string gameRoot,
            string userDataDir,
            Downloader downloader = null,
            CancellationToken token = default)
        {
            if (package == null) return InstallReport.Fail("no package");
            if (string.IsNullOrEmpty(gameRoot)) return InstallReport.Fail("no game folder");

            if (!Catalog.IsInstallable(gameRoot, manifest, out string why))
                return InstallReport.Fail($"refused: {why}");

            downloader = downloader ?? CatalogFetcher.DownloadVerifiedAsync;

            string staging = Path.Combine(StagingDir(userDataDir), SafeFileName(package.Id));
            var staged = new List<KeyValuePair<string, string>>();   // stagedFile -> finalPath

            try
            {
                if (Directory.Exists(staging)) Directory.Delete(staging, true);
                Directory.CreateDirectory(staging);

                // --- 1. fetch and verify everything, touching nothing real ---------------
                foreach (var file in manifest.Files)
                {
                    token.ThrowIfCancellationRequested();

                    if (!Catalog.IsSafeTargetPath(gameRoot, file.TargetPath, out string finalPath))
                        return InstallReport.Fail($"unsafe target path: {file.TargetPath}");

                    // Relative to the version manifest's folder, NOT the catalog root.
                    string fileBase = string.IsNullOrEmpty(manifest.BaseUrl)
                        ? package.BaseUrl : manifest.BaseUrl;
                    string url = CatalogPackage.Combine(fileBase, file.SourcePath);
                    string stagedPath = Path.Combine(staging, SafeFileName(file.TargetPath));

                    var got = await downloader(url, stagedPath, file.Sha256, token).ConfigureAwait(false);
                    if (!got.Ok)
                        return InstallReport.Fail($"{Path.GetFileName(file.TargetPath)}: {got.Error}");

                    staged.Add(new KeyValuePair<string, string>(stagedPath, finalPath));
                }

                // --- 2. everything verified, now move into place ------------------------
                var moved = new List<KeyValuePair<string, string>>();   // finalPath -> backupPath
                try
                {
                    foreach (var pair in staged)
                    {
                        string finalPath = pair.Value;
                        Directory.CreateDirectory(Path.GetDirectoryName(finalPath));

                        string backup = null;
                        if (File.Exists(finalPath))
                        {
                            backup = finalPath + ".bak-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                            File.Move(finalPath, backup);
                        }

                        File.Copy(pair.Key, finalPath, true);
                        moved.Add(new KeyValuePair<string, string>(finalPath, backup));
                    }
                }
                catch (Exception ex)
                {
                    RollBack(moved);
                    return InstallReport.Fail($"install failed while writing ({ex.Message}) — rolled back");
                }

                // Only once every file is in place is the previous version disposable.
                foreach (var pair in moved)
                    if (pair.Value != null) TryDelete(pair.Value);

                var report = new InstallReport { Ok = true, NeedsRestart = true };
                foreach (var pair in moved) report.Files.Add(pair.Key);
                report.Message = $"{package.Name} {manifest.Version} installed — restart to load it.";

                WriteReceipt(userDataDir, new InstallReceipt
                {
                    PackageId = package.Id,
                    Name = package.Name,
                    Version = manifest.Version,
                    SourceId = package.SourceId,
                    Files = report.Files
                });

                return report;
            }
            catch (OperationCanceledException)
            {
                return InstallReport.Fail("cancelled");
            }
            catch (Exception ex)
            {
                return InstallReport.Fail(ex.Message);
            }
            finally
            {
                try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
            }
        }

        private static void RollBack(List<KeyValuePair<string, string>> moved)
        {
            foreach (var pair in moved)
            {
                try
                {
                    if (File.Exists(pair.Key)) File.Delete(pair.Key);
                    if (pair.Value != null && File.Exists(pair.Value)) File.Move(pair.Value, pair.Key);
                }
                catch { }
            }
        }

        // ---- uninstall --------------------------------------------------------------

        /// Removes exactly the files the receipt records. The mod's settings in UserData are
        /// KEPT unless deleteSettings is set, so reinstalling restores them — losing someone's
        /// configuration as a side effect of removing a mod is never acceptable.
        public static InstallReport Uninstall(
            string packageId, string gameRoot, string userDataDir,
            bool deleteSettings = false)
        {
            if (string.IsNullOrEmpty(packageId)) return InstallReport.Fail("no package id");

            var receipt = ReadReceipt(userDataDir, packageId);
            if (receipt == null)
                return InstallReport.Fail("not installed by the manager — remove its DLL by hand");

            var report = new InstallReport { Ok = true, NeedsRestart = true };
            var failed = new List<string>();

            foreach (string path in receipt.Files)
            {
                // Re-check on the way out: a receipt is a file on disk and could have been edited.
                string relative = MakeRelative(gameRoot, path);
                if (relative == null || !Catalog.IsSafeTargetPath(gameRoot, relative, out string resolved))
                {
                    failed.Add(path);
                    continue;
                }

                try
                {
                    if (File.Exists(resolved)) File.Delete(resolved);
                    report.Files.Add(resolved);
                }
                catch (Exception ex)
                {
                    failed.Add($"{Path.GetFileName(path)} ({ex.Message})");
                }
            }

            if (deleteSettings) DeleteSettingsFor(receipt, userDataDir, failed);

            TryDelete(ReceiptPath(userDataDir, packageId));

            report.Message = failed.Count == 0
                ? $"{receipt.Name} removed — restart to unload it."
                : $"{receipt.Name} partly removed; could not delete: {string.Join(", ", failed)}";
            report.Ok = failed.Count == 0;
            return report;
        }

        /// Settings are named after the assembly, so derive the stem from the DLLs we installed.
        private static void DeleteSettingsFor(InstallReceipt receipt, string userDataDir, List<string> failed)
        {
            foreach (string path in receipt.Files)
            {
                if (!path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                string stem = Path.GetFileNameWithoutExtension(path);

                foreach (string suffix in new[] { ".ini", ".settings.json" })
                {
                    string settings = Path.Combine(userDataDir, stem + suffix);
                    try { if (File.Exists(settings)) File.Delete(settings); }
                    catch (Exception ex) { failed.Add($"{stem}{suffix} ({ex.Message})"); }
                }
            }
        }

        // ---- receipts ---------------------------------------------------------------

        public static void WriteReceipt(string userDataDir, InstallReceipt receipt)
        {
            try
            {
                Directory.CreateDirectory(ReceiptsDir(userDataDir));

                var files = new JArray();
                foreach (string f in receipt.Files) files.Add(f);

                var obj = new JObject
                {
                    ["schemaVersion"] = 1,
                    ["packageId"] = receipt.PackageId,
                    ["name"] = receipt.Name,
                    ["version"] = receipt.Version,
                    ["sourceId"] = receipt.SourceId,
                    ["files"] = files
                };

                File.WriteAllText(ReceiptPath(userDataDir, receipt.PackageId), obj.ToString());
            }
            catch { }
        }

        public static InstallReceipt ReadReceipt(string userDataDir, string packageId)
        {
            try
            {
                string path = ReceiptPath(userDataDir, packageId);
                if (!File.Exists(path)) return null;

                var obj = JObject.Parse(File.ReadAllText(path));
                var receipt = new InstallReceipt
                {
                    PackageId = (string)obj["packageId"],
                    Name = (string)obj["name"] ?? packageId,
                    Version = (string)obj["version"] ?? "",
                    SourceId = (string)obj["sourceId"] ?? ""
                };

                if (obj["files"] is JArray files)
                    foreach (var f in files)
                    {
                        string s = (string)f;
                        if (!string.IsNullOrEmpty(s)) receipt.Files.Add(s);
                    }

                return receipt;
            }
            catch
            {
                return null;
            }
        }

        public static Dictionary<string, InstallReceipt> AllReceipts(string userDataDir)
        {
            var map = new Dictionary<string, InstallReceipt>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string dir = ReceiptsDir(userDataDir);
                if (!Directory.Exists(dir)) return map;

                foreach (string path in Directory.GetFiles(dir, "*.json"))
                {
                    try
                    {
                        var obj = JObject.Parse(File.ReadAllText(path));
                        string id = (string)obj["packageId"];
                        if (string.IsNullOrEmpty(id)) continue;

                        var receipt = ReadReceipt(userDataDir, id);
                        if (receipt != null) map[id] = receipt;
                    }
                    catch { }
                }
            }
            catch { }
            return map;
        }

        // ---- helpers ----------------------------------------------------------------

        private static string MakeRelative(string root, string full)
        {
            try
            {
                string baseFull = Path.GetFullPath(root);
                if (!baseFull.EndsWith(Path.DirectorySeparatorChar.ToString()))
                    baseFull += Path.DirectorySeparatorChar;

                string target = Path.GetFullPath(full);
                if (!target.StartsWith(baseFull, StringComparison.OrdinalIgnoreCase)) return null;

                return target.Substring(baseFull.Length);
            }
            catch
            {
                return null;
            }
        }

        /// Flattens a path into one filename, so staging never nests and never escapes.
        private static string SafeFileName(string value)
        {
            var sb = new System.Text.StringBuilder(value.Length);
            foreach (char c in value)
                sb.Append(char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_' ? c : '_');
            return sb.ToString();
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
