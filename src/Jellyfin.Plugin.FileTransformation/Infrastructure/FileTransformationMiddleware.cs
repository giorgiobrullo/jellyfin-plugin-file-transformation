using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.FileTransformation.Library;
using MediaBrowser.Common.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Plugin.FileTransformation.Infrastructure
{
    public sealed class FileTransformationMiddleware
    {
        // The query parameter carrying the hash of a transformed file's content (see
        // VersionReferences): a URL with the current one names exactly that content.
        private const string VersionParameter = "ft";

        // How long a computed content hash is trusted before the file is transformed again to check
        // it: bounds how long a change in a transformation's output can take to reach index.html.
        private static readonly TimeSpan s_versionLifetime = TimeSpan.FromSeconds(60);

        private static readonly ConcurrentDictionary<string, (DateTime ComputedAt, string Hash)> s_versions = new(StringComparer.OrdinalIgnoreCase);

        // Compressed bodies by content hash and encoding: the transformed files are the same for
        // every request, and compressing the 500 KB main bundle each time is wasted work.
        private static readonly ConcurrentDictionary<string, byte[]> s_compressed = new(StringComparer.Ordinal);

        private readonly RequestDelegate m_next;

        public FileTransformationMiddleware(RequestDelegate next)
        {
            m_next = next;
        }

        public async Task InvokeAsync(
            HttpContext context,
            IWebFileTransformationReadService readService,
            IApplicationPaths applicationPaths,
            ILogger<FileTransformationMiddleware> logger)
        {
            string path = context.Request.Path.Value ?? string.Empty;

            // Fast path: only intercept /web/ paths.
            // Also handle /web (no trailing slash) which Jellyfin redirects.
            int webIndex = path.IndexOf("/web/", StringComparison.OrdinalIgnoreCase);
            if (webIndex < 0 && !path.EndsWith("/web", StringComparison.OrdinalIgnoreCase))
            {
                await m_next(context);
                return;
            }

            // Extract the relative path after /web/
            // When path is "/web/" or "/web", resolve to "index.html" since
            // Jellyfin's UseDefaultFiles serves index.html for the root.
            string relativePath = webIndex >= 0 ? path.Substring(webIndex + 5) : string.Empty;
            if (string.IsNullOrEmpty(relativePath))
            {
                relativePath = "index.html";
            }

            bool needsTransform = readService.NeedsTransformation(relativePath);

            if (!needsTransform)
            {
                await m_next(context);
                return;
            }

            logger.LogDebug($"[FileTransformation] Intercepting response for: {relativePath}");

            // Save the client's Accept-Encoding before stripping — we'll re-compress after transforming.
            string acceptEncoding = context.Request.Headers.AcceptEncoding.ToString();

            // Strip Accept-Encoding so Jellyfin serves uncompressed content into our buffer.
            context.Request.Headers.Remove("Accept-Encoding");

            // Buffer the response body so we can run transformations on it
            Stream originalBody = context.Response.Body;
            using MemoryStream bufferedBody = new MemoryStream();
            context.Response.Body = bufferedBody;

            try
            {
                await m_next(context);

                // Virtual file synthesis: if a transform is registered for a file that
                // doesn't exist on disk (404), create an empty response and let the
                // transform callbacks generate the content. This is how Plugin Pages
                // creates virtual pages like userpluginsettings.html.
                bool isSynthesized = false;
                if (context.Response.StatusCode == 404 && needsTransform)
                {
                    logger.LogDebug($"[FileTransformation] Synthesizing virtual file for '{relativePath}'");
                    isSynthesized = true;
                    bufferedBody.SetLength(0);
                    context.Response.StatusCode = 200;

                    FileExtensionContentTypeProvider contentTypeProvider = new FileExtensionContentTypeProvider();
                    if (contentTypeProvider.TryGetContentType(relativePath, out string? contentType))
                    {
                        context.Response.ContentType = contentType;
                    }
                    else
                    {
                        context.Response.ContentType = "application/octet-stream";
                    }
                }
                else if (context.Response.StatusCode != 200)
                {
                    // Non-200 response with no transform registered — pass through
                    bufferedBody.Seek(0, SeekOrigin.Begin);
                    context.Response.Body = originalBody;
                    await bufferedBody.CopyToAsync(context.Response.Body);
                    return;
                }

                // Snapshot the original bytes so we can fall back to unmodified content
                // if a transform throws partway through.
                byte[] originalContent = bufferedBody.ToArray();

                try
                {
                    await readService.RunTransformation(relativePath, bufferedBody);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, $"[FileTransformation] Transformation pipeline failed for '{relativePath}'. Serving original content.");
                    bufferedBody.SetLength(0);
                    await bufferedBody.WriteAsync(originalContent);
                    bufferedBody.Seek(0, SeekOrigin.Begin);
                }

                // If this was a synthesized virtual file and the transforms produced nothing,
                // revert to 404 instead of serving an empty 200.
                if (isSynthesized && bufferedBody.Length == 0)
                {
                    logger.LogWarning($"[FileTransformation] Virtual file synthesis for '{relativePath}' produced empty content, reverting to 404");
                    context.Response.StatusCode = 404;
                    context.Response.ContentLength = 0;
                    context.Response.Body = originalBody;
                    return;
                }

                // index.html names the other transformed files by their content: they can then be
                // kept by browsers, and a change in their transformations reaches them at once.
                if (string.Equals(relativePath, "index.html", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        await VersionReferences(bufferedBody, readService, applicationPaths.WebPath, logger);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "[FileTransformation] Could not version the references in index.html");
                    }
                }

                // Compute ETag from the transformed content so browsers can do conditional requests.
                // This brings back 304 responses for unchanged transformed content.
                bufferedBody.Seek(0, SeekOrigin.Begin);
                string contentHash = await HashContent(bufferedBody);
                string etag = $"\"{contentHash}\"";
                if (HttpMethods.IsGet(context.Request.Method))
                {
                    s_versions[NormalizeKey(relativePath)] = (DateTime.UtcNow, contentHash);
                }

                // Asked for by the URL index.html gives it: that URL never names other content.
                string cacheControl = string.Equals(context.Request.Query[VersionParameter].ToString(), contentHash, StringComparison.Ordinal)
                    ? "public, max-age=31536000, immutable"
                    : "no-cache";

                // Check If-None-Match — return 304 if content hasn't changed
                string ifNoneMatch = context.Request.Headers.IfNoneMatch.ToString();
                if (!string.IsNullOrEmpty(ifNoneMatch) && string.Equals(ifNoneMatch, etag, StringComparison.Ordinal))
                {
                    context.Response.StatusCode = 304;
                    context.Response.ContentLength = 0;
                    context.Response.Headers[HeaderNames.ETag] = etag;
                    context.Response.Headers[HeaderNames.CacheControl] = cacheControl;
                    context.Response.Body = originalBody;
                    return;
                }

                // Prepare the response headers
                context.Response.Headers.Remove("Content-Encoding");
                context.Response.Headers.Remove("Last-Modified");
                context.Response.Headers[HeaderNames.ETag] = etag;
                context.Response.Headers[HeaderNames.CacheControl] = cacheControl;
                context.Response.Headers[HeaderNames.Vary] = "Accept-Encoding";

                // Re-compress the transformed content if the client supports it.
                // Skip compression for local/LAN clients where it just adds CPU overhead.
                bufferedBody.Seek(0, SeekOrigin.Begin);
                context.Response.Body = originalBody;

                // Parse Accept-Encoding tokens properly — honor q=0 (means "not supported")
                bool isLocal = IsLocalRequest(context);
                HashSet<string> encodings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string entry in acceptEncoding.Split(','))
                {
                    string trimmed = entry.Trim();
                    string[] parts = trimmed.Split(';');
                    string token = parts[0].Trim();
                    bool rejected = false;
                    for (int i = 1; i < parts.Length; i++)
                    {
                        string param = parts[i].Trim();
                        if (param.StartsWith("q=", StringComparison.OrdinalIgnoreCase))
                        {
                            string qVal = param.Substring(2).Trim();
                            // InvariantCulture is required here — HTTP quality values always use '.' as
                            // the decimal separator per RFC 7231, but the default TryParse overload uses
                            // the server's current culture which may use ',' (e.g. de-DE, fr-FR).
                            if (float.TryParse(qVal, NumberStyles.Float, CultureInfo.InvariantCulture, out float quality) && quality <= 0f)
                            {
                                rejected = true;
                            }

                            break;
                        }
                    }

                    if (!rejected && token.Length > 0)
                    {
                        encodings.Add(token);
                    }
                }

                string? selectedEncoding = isLocal ? null : SelectEncoding(encodings);
                bool compressed = false;
                if (selectedEncoding != null)
                {
                    // Defensive: if a future encoding is added to SelectEncoding without a matching
                    // CreateCompressionStream case, fall back to uncompressed instead of failing the
                    // response. Better for users than a broken /web/ page.
                    try
                    {
                        string compressedKey = contentHash + ":" + selectedEncoding;
                        if (!s_compressed.TryGetValue(compressedKey, out byte[]? compressedBytes))
                        {
                            using MemoryStream compressedBuffer = new MemoryStream();
                            using (Stream compressor = CreateCompressionStream(selectedEncoding, compressedBuffer))
                            {
                                await bufferedBody.CopyToAsync(compressor);
                            }

                            compressedBytes = compressedBuffer.ToArray();
                            if (s_compressed.Count >= 32)
                            {
                                s_compressed.Clear();
                            }

                            s_compressed[compressedKey] = compressedBytes;
                        }

                        context.Response.Headers[HeaderNames.ContentEncoding] = selectedEncoding;
                        context.Response.ContentLength = compressedBytes.Length;
                        // Set before the write to originalBody — once the response starts flushing to
                        // the client we cannot fall back to uncompressed without sending garbled bytes.
                        compressed = true;
                        await originalBody.WriteAsync(compressedBytes);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, $"[FileTransformation] Compression as '{selectedEncoding}' failed for '{relativePath}', serving uncompressed");
                    }
                }

                if (!compressed)
                {
                    context.Response.ContentLength = bufferedBody.Length;
                    await bufferedBody.CopyToAsync(originalBody);
                }
            }
            catch
            {
                context.Response.Body = originalBody;
                throw;
            }
        }

        private static string NormalizeKey(string path) => path.TrimStart('/');

        private static async Task<string> HashContent(Stream content)
        {
            byte[] hashBytes = await SHA256.HashDataAsync(content);
            return Convert.ToHexStringLower(hashBytes, 0, 8);
        }

        /// <summary>
        /// Adds the hash of their transformed content to index.html's references to the other
        /// transformed files (main.jellyfin.bundle.js?abc becomes main.jellyfin.bundle.js?abc&amp;ft=hash).
        /// </summary>
        internal static async Task VersionReferences(
            MemoryStream indexBody,
            IWebFileTransformationReadService readService,
            string webPath,
            ILogger logger)
        {
            string html = Encoding.UTF8.GetString(indexBody.ToArray());
            string original = html;

            foreach (string path in readService.GetRegisteredPaths())
            {
                if (string.Equals(path, "index.html", StringComparison.OrdinalIgnoreCase)
                    || !(path.EndsWith(".js", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".css", StringComparison.OrdinalIgnoreCase))
                    || html.IndexOf(path, StringComparison.Ordinal) < 0)
                {
                    continue;
                }

                string? hash = await GetVersion(path, readService, webPath, logger);
                if (hash == null)
                {
                    continue;
                }

                html = Regex.Replace(
                    html,
                    "(?<=[\"'/])" + Regex.Escape(path) + "(\\?[^\"'\\s>]*)?(?=[\"'])",
                    m => m.Groups[1].Success && m.Groups[1].Length > 1
                        ? $"{path}{m.Groups[1].Value}&{VersionParameter}={hash}"
                        : $"{path}?{VersionParameter}={hash}",
                    RegexOptions.None,
                    TimeSpan.FromSeconds(1));
            }

            if (!string.Equals(html, original, StringComparison.Ordinal))
            {
                indexBody.SetLength(0);
                await indexBody.WriteAsync(Encoding.UTF8.GetBytes(html));
            }

            indexBody.Seek(0, SeekOrigin.Begin);
        }

        /// <summary>
        /// The hash of a web file's transformed content: from its last response, or the file run
        /// through its transformations again when that is older than a minute.
        /// </summary>
        internal static async Task<string?> GetVersion(
            string path,
            IWebFileTransformationReadService readService,
            string webPath,
            ILogger logger)
        {
            if (s_versions.TryGetValue(path, out (DateTime ComputedAt, string Hash) known)
                && DateTime.UtcNow - known.ComputedAt < s_versionLifetime)
            {
                return known.Hash;
            }

            string fullPath = Path.GetFullPath(Path.Combine(webPath, path));
            if (!fullPath.StartsWith(Path.GetFullPath(webPath), StringComparison.Ordinal) || !File.Exists(fullPath))
            {
                return null;
            }

            try
            {
                using MemoryStream content = new MemoryStream();
                await using (FileStream file = File.OpenRead(fullPath))
                {
                    await file.CopyToAsync(content);
                }

                await readService.RunTransformation(path, content);
                content.Seek(0, SeekOrigin.Begin);
                string hash = await HashContent(content);
                s_versions[path] = (DateTime.UtcNow, hash);
                return hash;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, $"[FileTransformation] Could not version '{path}'");
                return null;
            }
        }

        private string? SelectEncoding(HashSet<string> encodings)
        {
            if (encodings.Contains("br"))
            {
                return "br";
            }

            if (encodings.Contains("gzip"))
            {
                return "gzip";
            }

            return null;
        }

        private Stream CreateCompressionStream(string encoding, Stream output)
        {
            return encoding switch
            {
                "br" => new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true),
                "gzip" => new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true),
                _ => throw new ArgumentException($"Unsupported encoding: {encoding}", nameof(encoding)),
            };
        }

        private bool IsLocalRequest(HttpContext context)
        {
            // Through a reverse proxy, every request comes from the proxy's private address (this
            // middleware runs before Jellyfin reads the forwarded headers): the client is elsewhere.
            if (context.Request.Headers.ContainsKey("X-Forwarded-For"))
            {
                return false;
            }

            IPAddress? remoteIp = context.Connection.RemoteIpAddress;
            if (remoteIp == null)
            {
                return true;
            }

            // Handle IPv6-mapped IPv4 (e.g. ::ffff:192.168.1.5 from Docker/dual-stack)
            if (remoteIp.IsIPv4MappedToIPv6)
            {
                remoteIp = remoteIp.MapToIPv4();
            }

            // Loopback (127.0.0.1, ::1)
            if (IPAddress.IsLoopback(remoteIp))
            {
                return true;
            }

            // Same machine (remote == local)
            IPAddress? localIp = context.Connection.LocalIpAddress;
            if (localIp != null)
            {
                if (localIp.IsIPv4MappedToIPv6)
                {
                    localIp = localIp.MapToIPv4();
                }

                if (remoteIp.Equals(localIp))
                {
                    return true;
                }
            }

            // Private/LAN ranges: 10.x, 172.16-31.x, 192.168.x
            byte[] bytes = remoteIp.GetAddressBytes();
            if (bytes.Length == 4)
            {
                if (bytes[0] == 10)
                {
                    return true;
                }

                if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                {
                    return true;
                }

                if (bytes[0] == 192 && bytes[1] == 168)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
