// Features/Discord/DiscordCoverService.cs - the cover art pipeline.
//
// The presence's large image is the BOOK, not a generic reader icon: this
// service replicates the pdf-summarizer-extension / Axo thumbnail system
// verbatim, so a book with a cover in the reader's own database shows that
// cover on their Discord profile.
//
// The resolution order, exactly the extension's:
//   1. the persistent URL cache (cover_urls.json, a cleanTitle -> url map
//      held in a thread-safe dictionary) - a hit costs 0 ms and no network,
//   2. the local thumbnail folders - the books' ThumbnailCache first, the
//      Articles' ThumbnailCache second; an exact <cleanTitle>.jpg match
//      wins, then the substring rule (name == lower, or either contains
//      the other, all lowercased),
//   3. an anonymous upload of the found .jpg to uguu.se - the response's
//      files[0].url joins the cache and the presence uses it,
//   4. anything missing or failing falls back to the "kindle" asset.
//
// The paths are the spec's own (the reader's desktop book database); a
// machine without them simply always falls back. Every file, JSON and
// HTTP touch is wrapped so nothing here can ever throw into the reader's
// way - a chat client's cover is a decoration, never a dependency.

namespace Avalanche.Features.Discord
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Text.Json;
    using System.Threading.Tasks;

    internal static class DiscordCoverService
    {
        // The spec's storage: the reader's local book database. The URL
        // cache lives beside the books' thumbnails; the Articles folder is
        // the secondary (fallback) source for the same lookup.
        private const string PrimaryThumbnailDir =
            @"C:\Users\PC\Desktop\database\books\ThumbnailCache";
        private const string ArticlesThumbnailDir =
            @"C:\Users\PC\Desktop\database\Articles\ThumbnailCache";
        private const string UrlCacheFile =
            @"C:\Users\PC\Desktop\database\books\cover_urls.json";
        private const string UploadEndpoint = "https://uguu.se/upload";

        private static readonly object Gate = new();
        private static Dictionary<string, string>? _urlCache;   // cleanTitle -> https url
        private static bool _cacheLoaded;

        // One shared client for the whole process: connections pool, and
        // the timeout keeps a dead endpoint from hanging a resolution.
        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(20);
            try { client.DefaultRequestHeaders.UserAgent.ParseAdd("Avalanche/" + "1.16"); }
            catch { /* a missing UA header is nobody's crash */ }
            return client;
        }

        /// <summary>The clean title the whole pipeline keys on: the file's
        /// name without its extension, trimmed - verbatim from the spec.
        /// Every character of the name survives, hyphens included, so the
        /// exact cleanTitle.jpg match hits real thumbnails like "After the
        /// Ice A Global Human History, 20,000-5000 BC.jpg". NOT the
        /// presence's display title (that one is title-cased); the cache
        /// and the thumbnail files key on the raw name.</summary>
        internal static string CleanTitle(string filePath)
        {
            try
            {
                return Path.GetFileNameWithoutExtension(filePath).Trim();
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>Resolves the cover art for a book: cache, local file,
        /// upload - in that order, the extension's exact ladder. Null means
        /// "no cover found": the controller falls back to "kindle". The
        /// whole chain runs on a worker thread (the controller kicks it in
        /// the background), so no UI thread and no presence update ever
        /// waits on a network round trip.</summary>
        internal static Task<string?> ResolveAsync(string cleanTitle)
            => Task.Run(async () =>
            {
                if (string.IsNullOrWhiteSpace(cleanTitle))
                {
                    return null;
                }

                // The defensive gate: a repaired book's temp working copy
                // must never become a cover lookup - killerpdf_repaired_
                // {guid}.jpg exists in no thumbnail cache, and uploading it
                // would be absurd. Null here keeps the "kindle" asset warm.
                if (cleanTitle.Contains("killerpdf_", StringComparison.OrdinalIgnoreCase)
                    || cleanTitle.Contains("_repaired_", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                try
                {
                    // Step 1 - the persistent URL cache: 0 ms on a hit.
                    string? cached = ReadUrlCache(cleanTitle);
                    if (cached is not null)
                    {
                        return cached;
                    }

                    // Step 2 - the local thumbnail files (books, then articles).
                    string? local = FindLocalThumbnail(cleanTitle, PrimaryThumbnailDir)
                                 ?? FindLocalThumbnail(cleanTitle, ArticlesThumbnailDir);
                    if (local is null)
                    {
                        return null;
                    }

                    // Step 3 - anonymous upload; the URL joins the cache on
                    // success so this book never uploads twice.
                    string? url = await UploadCoverAsync(local).ConfigureAwait(false);
                    if (url is null)
                    {
                        return null;
                    }

                    WriteUrlCache(cleanTitle, url);
                    return url;
                }
                catch
                {
                    // Step 4 - resilience: every failure is a silent null,
                    // and the kindle asset keeps the seat warm.
                    return null;
                }
            });

        // ---- the URL cache --------------------------------------------------------

        private static void EnsureCacheLoaded()
        {
            // Callers hold Gate. A missing or unreadable cache file just
            // means the in-memory map starts empty - the feature shrugs.
            if (_cacheLoaded && _urlCache is not null)
            {
                return;
            }

            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                if (File.Exists(UrlCacheFile))
                {
                    string json = File.ReadAllText(UrlCacheFile);
                    var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                    if (parsed is not null)
                    {
                        dict = parsed;
                    }
                }
            }
            catch
            {
                // a broken cache file costs nothing but the cached URLs
            }

            _urlCache = dict;
            _cacheLoaded = true;
        }

        private static string? ReadUrlCache(string cleanTitle)
        {
            lock (Gate)
            {
                EnsureCacheLoaded();
                if (_urlCache!.TryGetValue(cleanTitle, out string? url)
                    && url.StartsWith("https://", StringComparison.Ordinal))
                {
                    return url;
                }

                return null;
            }
        }

        private static void WriteUrlCache(string cleanTitle, string url)
        {
            try
            {
                lock (Gate)
                {
                    EnsureCacheLoaded();
                    _urlCache![cleanTitle] = url;
                    string? dir = Path.GetDirectoryName(UrlCacheFile);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    File.WriteAllText(UrlCacheFile, JsonSerializer.Serialize(_urlCache));
                }
            }
            catch
            {
                // best-effort persistence: the presence still uses the URL
            }
        }

        // ---- the local thumbnail lookup ----------------------------------------------

        private static string? FindLocalThumbnail(string cleanTitle, string directory)
        {
            try
            {
                if (!Directory.Exists(directory))
                {
                    return null;
                }

                // Exact: <cleanTitle>.jpg sitting right in the cache folder.
                string exact = Path.Combine(directory, cleanTitle + ".jpg");
                if (File.Exists(exact))
                {
                    return exact;
                }

                // Fuzzy: the filename (without .jpg, lowercased) and the
                // clean title (lowercased) accept each other when either
                // contains the other - the extension's own substring rule.
                string lower = cleanTitle.ToLowerInvariant();
                if (lower.Length == 0)
                {
                    return null;
                }

                foreach (string file in Directory.EnumerateFiles(directory, "*.jpg"))
                {
                    string name;
                    try
                    {
                        name = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
                    }
                    catch
                    {
                        continue;
                    }

                    if (name.Length == 0)
                    {
                        continue;
                    }

                    if (name == lower
                        || lower.Contains(name, StringComparison.Ordinal)
                        || name.Contains(lower, StringComparison.Ordinal))
                    {
                        return file;
                    }
                }
            }
            catch
            {
                // a missing drive or a denied folder is not a crash
            }

            return null;
        }

        // ---- the anonymous upload ---------------------------------------------------

        // POST https://uguu.se/upload as multipart/form-data - the file rides
        // as "files[]" named "cover.jpg" - and the JSON answer's files[0].url
        // is the public image URL Discord fetches for the presence.
        private static async Task<string?> UploadCoverAsync(string localPath)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(localPath);
                if (bytes.Length == 0)
                {
                    return null;
                }

                using var form = new MultipartFormDataContent();
                var fileContent = new ByteArrayContent(bytes);
                fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
                form.Add(fileContent, "files[]", "cover.jpg");

                using HttpResponseMessage response =
                    await Http.PostAsync(UploadEndpoint, form).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                // {"success":true,"files":[{"name":"cover.jpg","url":"https://a.uguu.se/...","size":12345}]}
                using JsonDocument doc = JsonDocument.Parse(body);
                JsonElement root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("files", out JsonElement files)
                    && files.ValueKind == JsonValueKind.Array
                    && files.GetArrayLength() > 0
                    && files[0].ValueKind == JsonValueKind.Object
                    && files[0].TryGetProperty("url", out JsonElement urlElement))
                {
                    string? url = urlElement.GetString();
                    if (!string.IsNullOrWhiteSpace(url)
                        && url.StartsWith("http", StringComparison.Ordinal))
                    {
                        return url;
                    }
                }
            }
            catch
            {
                // no network, dead endpoint, unexpected JSON: a silent null
            }

            return null;
        }
    }
}
