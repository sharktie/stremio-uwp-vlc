using System;
using System.Collections.Generic;
using Microsoft.Web.WebView2.Core;
using Stremio.Diagnostics;
using Windows.Storage.Streams;

namespace Stremio.Shell
{
    /// <summary>
    /// Answers stremio-web's streaming server requests for Stremio's default server address inside
    /// the WebView, so direct links (debrid services, HTTP addons) play without a real server.
    /// </summary>
    /// <remarks>
    /// In shell mode stremio-video refuses every stream ("Stream is not supported") unless a
    /// streaming server is available, because the desktop app always runs one. The Xbox can't run
    /// it, so this reports a server with no torrent support: stream probes and subtitle conversion
    /// return 404, which stremio-video handles by playing the link directly and fetching subtitles
    /// from the addon. Torrents still need a real Stremio server, selected in Settings > Streaming.
    /// </remarks>
    internal sealed class BuiltInStreamingServer : IDisposable
    {
        /// <summary>stremio-web's default streaming server URL.</summary>
        public const string BaseUrl = "http://127.0.0.1:11470";

        private const string TorrentMessage =
            "Torrent streams need a Stremio streaming server. Run Stremio on a PC on the same network and add its address " +
            "in Settings > Streaming, or use a debrid service such as Real-Debrid with your addon.";

        private const string SettingsJson =
            "{\"baseUrl\":\"" + BaseUrl + "\",\"options\":[],\"values\":{" +
            "\"serverVersion\":\"built-in (direct links only)\",\"appPath\":\"\",\"cacheRoot\":\"\",\"cacheSize\":0," +
            "\"btMaxConnections\":0,\"btHandshakeTimeout\":0,\"btRequestTimeout\":0,\"btDownloadSpeedSoftLimit\":0," +
            "\"btDownloadSpeedHardLimit\":0,\"btMinPeersForStable\":0,\"remoteHttps\":\"\",\"localAddonEnabled\":false," +
            "\"proxyStreamsEnabled\":false,\"transcodeProfile\":null}}";

        // CORS headers: requests come from https://web.stremio.com and its core worker.
        private const string CorsHeaders =
            "Access-Control-Allow-Origin: *\n" +
            "Access-Control-Allow-Methods: GET, POST, OPTIONS\n" +
            "Access-Control-Allow-Headers: *\n" +
            "Access-Control-Allow-Private-Network: true\n" +
            "Cache-Control: no-store";

        private readonly CoreWebView2 _webView;

        public BuiltInStreamingServer(CoreWebView2 webView)
        {
            _webView = webView;
            var filter = BaseUrl + "/*";
            try
            {
                // stremio-core talks to the server from a web worker; include every request source.
                webView.AddWebResourceRequestedFilter(filter, CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
            }
            catch (Exception ex)
            {
                Log.Error("Request source filtering is unsupported, intercepting document requests only", ex);
                webView.AddWebResourceRequestedFilter(filter, CoreWebView2WebResourceContext.All);
            }
            webView.WebResourceRequested += OnWebResourceRequested;
        }

        public void Dispose()
        {
            _webView.WebResourceRequested -= OnWebResourceRequested;
        }

        /// <summary>
        /// Turns a media URL stremio-video built against this server back into one VLC can open.
        /// Returns null (with a message) for streams that really need a Stremio server.
        /// </summary>
        public static string ResolveMediaUrl(string url, List<string> mediaOptions, out string error)
        {
            error = null;
            var prefix = BaseUrl + "/";
            if (!url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return url;
            }
            var rest = url.Substring(prefix.Length);
            if (!rest.StartsWith("proxy/", StringComparison.Ordinal))
            {
                // /{infoHash}/{fileIdx}: a torrent served by the streaming server.
                error = TorrentMessage;
                return null;
            }

            // /proxy/d=<origin>&h=<name>:<value>/<path>?<query> (stremio-video buildProxyUrl), used
            // for streams that need extra request headers.
            rest = rest.Substring("proxy/".Length);
            var slash = rest.IndexOf('/');
            var proxyOptions = slash >= 0 ? rest.Substring(0, slash) : rest;
            var pathAndQuery = slash >= 0 ? rest.Substring(slash) : "/";
            string origin = null;
            foreach (var pair in proxyOptions.Split('&'))
            {
                var equals = pair.IndexOf('=');
                if (equals <= 0)
                {
                    continue;
                }
                var key = pair.Substring(0, equals);
                var value = Uri.UnescapeDataString(pair.Substring(equals + 1).Replace('+', ' '));
                if (key == "d")
                {
                    origin = value;
                }
                else if (key == "h")
                {
                    var colon = value.IndexOf(':');
                    if (colon <= 0)
                    {
                        continue;
                    }
                    var name = value.Substring(0, colon).Trim();
                    var headerValue = value.Substring(colon + 1).Trim();
                    // LibVLC 3 can only set these two request headers.
                    if (name.Equals("User-Agent", StringComparison.OrdinalIgnoreCase))
                    {
                        mediaOptions.Add(":http-user-agent=" + headerValue);
                    }
                    else if (name.Equals("Referer", StringComparison.OrdinalIgnoreCase))
                    {
                        mediaOptions.Add(":http-referrer=" + headerValue);
                    }
                }
            }
            if (string.IsNullOrEmpty(origin))
            {
                error = "This stream's link could not be read.";
                return null;
            }
            return origin.TrimEnd('/') + pathAndQuery;
        }

        private async void OnWebResourceRequested(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            Uri uri;
            if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out uri) || uri.Authority != new Uri(BaseUrl).Authority)
            {
                return;
            }
            var method = e.Request.Method.ToUpperInvariant();
            var path = uri.AbsolutePath.TrimEnd('/');
            int status;
            string body;
            if (method == "OPTIONS")
            {
                status = 204;
                body = string.Empty;
            }
            else
            {
                switch (path)
                {
                    case "/settings":
                        status = 200;
                        body = method == "POST" ? "{\"success\":true}" : SettingsJson;
                        break;
                    case "/network-info":
                        status = 200;
                        body = "{\"availableInterfaces\":[]}";
                        break;
                    case "/device-info":
                        status = 200;
                        body = "{\"availableHardwareAccelerations\":[]}";
                        break;
                    case "/casting":
                        status = 200;
                        body = "[]";
                        break;
                    default:
                        if (path.StartsWith("/local-addon/stream/", StringComparison.Ordinal))
                        {
                            // The server's "local files" addon: there are no local files here.
                            status = 200;
                            body = "{\"streams\":[]}";
                        }
                        else
                        {
                            status = 404;
                            body = "{\"error\":\"Not available in the built-in streaming server\"}";
                        }
                        break;
                }
                Log.Write("Built-in streaming server: " + method + " " + path + " -> " + status);
            }

            // The body must be a native WinRT stream that is fully written before it is handed over.
            // A .NET stream wrapped with AsRandomAccessStream is read back through this (UI) thread
            // and froze the whole app.
            var deferral = e.GetDeferral();
            try
            {
                InMemoryRandomAccessStream content = null;
                if (body.Length > 0)
                {
                    content = new InMemoryRandomAccessStream();
                    var writer = new DataWriter(content) { UnicodeEncoding = UnicodeEncoding.Utf8 };
                    writer.WriteString(body);
                    await writer.StoreAsync();
                    writer.DetachStream();
                    writer.Dispose();
                    content.Seek(0);
                }
                var headers = "Content-Type: application/json\n" + CorsHeaders;
                e.Response = sender.Environment.CreateWebResourceResponse(content, status, status == 404 ? "Not Found" : "OK", headers);
            }
            catch (Exception ex)
            {
                Log.Error("Built-in streaming server response failed", ex);
            }
            finally
            {
                deferral.Complete();
            }
        }
    }
}
