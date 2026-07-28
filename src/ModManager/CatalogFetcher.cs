// Network layer for the downloader.
//
// Uses System.Net.Http, not UnityWebRequest. MelonLoader mods run on a real .NET 6 runtime, so
// HttpClient is available directly and needs no coroutine, no Il2Cpp interop and no main-thread
// pumping. UnityWebRequest would drag all three in for no benefit.
//
// Everything here runs off the main thread. Results come back through MainThread.Post so the UI
// only ever touches Unity objects from Unity's thread. Nothing polls: when no request is in
// flight this file costs nothing.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace BapbapMods.Manager
{
    /// Marshals callbacks back onto Unity's thread. OnUpdate drains this; when it's empty the
    /// drain is a single volatile read.
    public static class MainThread
    {
        private static readonly Queue<Action> Pending = new Queue<Action>();
        private static volatile int _count;

        public static void Post(Action action)
        {
            if (action == null) return;
            lock (Pending)
            {
                Pending.Enqueue(action);
                _count = Pending.Count;
            }
        }

        /// Call once per frame. Returns immediately when idle.
        public static void Drain(Action<Exception> onError = null)
        {
            if (_count == 0) return;

            while (true)
            {
                Action next;
                lock (Pending)
                {
                    if (Pending.Count == 0) { _count = 0; return; }
                    next = Pending.Dequeue();
                    _count = Pending.Count;
                }

                try { next(); }
                catch (Exception ex) { onError?.Invoke(ex); }
            }
        }
    }

    public class FetchResult<T>
    {
        public bool Ok;
        public T Value;
        public string Error;

        public static FetchResult<T> Fail(string why) => new FetchResult<T> { Ok = false, Error = why };
        public static FetchResult<T> Good(T v) => new FetchResult<T> { Ok = true, Value = v };
    }

    public static class CatalogFetcher
    {
        private static HttpClient _client;
        private static readonly object ClientLock = new object();

        /// How long any single request may take before it is abandoned.
        public static TimeSpan Timeout = TimeSpan.FromSeconds(30);

        private static HttpClient Client
        {
            get
            {
                if (_client != null) return _client;
                lock (ClientLock)
                {
                    if (_client == null)
                    {
                        _client = new HttpClient { Timeout = Timeout };
                        _client.DefaultRequestHeaders.Add("User-Agent", "bapbap-modkit");
                    }
                }
                return _client;
            }
        }

        // ---- text -------------------------------------------------------------------

        public static async Task<FetchResult<string>> GetTextAsync(string url, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(url)) return FetchResult<string>.Fail("no url");

            try
            {
                using (var response = await Client.GetAsync(url, token).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                        return FetchResult<string>.Fail($"HTTP {(int)response.StatusCode} for {url}");

                    string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    return FetchResult<string>.Good(body);
                }
            }
            catch (OperationCanceledException)
            {
                return FetchResult<string>.Fail("cancelled");
            }
            catch (Exception ex)
            {
                return FetchResult<string>.Fail(ex.Message);
            }
        }

        // ---- files ------------------------------------------------------------------

        /// Downloads to <paramref name="destination"/> and verifies its sha256. On any failure
        /// the partial file is removed, so a staging directory never holds an unverified file.
        public static async Task<FetchResult<string>> DownloadVerifiedAsync(
            string url, string destination, string expectedSha256, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(url)) return FetchResult<string>.Fail("no url");
            if (string.IsNullOrEmpty(expectedSha256))
                return FetchResult<string>.Fail("refusing to download a file with no expected hash");

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination));

                using (var response = await Client.GetAsync(url, token).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                        return FetchResult<string>.Fail($"HTTP {(int)response.StatusCode} for {url}");

                    using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var file = File.Create(destination))
                    {
                        await stream.CopyToAsync(file, 81920, token).ConfigureAwait(false);
                    }
                }

                string actual = Sha256OfFile(destination);
                if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(destination);
                    return FetchResult<string>.Fail(
                        $"sha256 mismatch: expected {expectedSha256}, got {actual}");
                }

                return FetchResult<string>.Good(destination);
            }
            catch (Exception ex)
            {
                TryDelete(destination);
                return FetchResult<string>.Fail(ex.Message);
            }
        }

        public static string Sha256OfFile(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
            {
                byte[] hash = sha.ComputeHash(stream);
                var sb = new System.Text.StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        // ---- catalog-level ----------------------------------------------------------

        /// Fetches every enabled source and merges them. Sources are fetched concurrently; one
        /// source failing degrades the list rather than failing the whole browse.
        public static async Task<FetchResult<List<CatalogPackage>>> FetchCatalogAsync(
            List<SourceDescriptor> sources, List<string> problems, CancellationToken token = default)
        {
            if (sources == null || sources.Count == 0)
                return FetchResult<List<CatalogPackage>>.Fail("no sources configured");

            var tasks = new List<Task<List<CatalogPackage>>>();
            foreach (var source in sources)
            {
                if (source == null || !source.Enabled) continue;
                tasks.Add(FetchOneSourceAsync(source, problems, token));
            }

            if (tasks.Count == 0)
                return FetchResult<List<CatalogPackage>>.Fail("every source is disabled");

            var lists = await Task.WhenAll(tasks).ConfigureAwait(false);

            var merged = Catalog.Merge(lists, out var collisions);
            if (collisions.Count > 0 && problems != null)
                problems.Add($"{collisions.Count} duplicate package id(s) ignored: {string.Join(", ", collisions)}");

            return FetchResult<List<CatalogPackage>>.Good(merged);
        }

        private static async Task<List<CatalogPackage>> FetchOneSourceAsync(
            SourceDescriptor source, List<string> problems, CancellationToken token)
        {
            string url = CatalogPackage.Combine(source.BaseUrl, source.PackagesPath);
            var result = await GetTextAsync(url, token).ConfigureAwait(false);

            if (!result.Ok)
            {
                lock (problems ?? new object())
                    problems?.Add($"{source.DisplayName}: {result.Error}");
                return new List<CatalogPackage>();
            }

            return Catalog.ParsePackages(result.Value, source);
        }

        public static async Task<FetchResult<VersionManifest>> FetchVersionAsync(
            CatalogPackage package, CancellationToken token = default)
        {
            if (package == null || string.IsNullOrEmpty(package.VersionManifestUrl))
                return FetchResult<VersionManifest>.Fail("package has no version manifest");

            var text = await GetTextAsync(package.VersionManifestUrl, token).ConfigureAwait(false);
            if (!text.Ok) return FetchResult<VersionManifest>.Fail(text.Error);

            var manifest = Catalog.ParseVersion(text.Value);
            return manifest == null
                ? FetchResult<VersionManifest>.Fail("version manifest did not parse")
                : FetchResult<VersionManifest>.Good(manifest);
        }
    }
}
