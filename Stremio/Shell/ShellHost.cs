using System;
using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using Stremio.Diagnostics;
using Stremio.Playback;
using Windows.ApplicationModel;
using Windows.Data.Json;
using Windows.System;
using Windows.System.Profile;
using Windows.UI.Core;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;

namespace Stremio.Shell
{
    /// <summary>
    /// Native side of the transport stremio-web uses to talk to its desktop shell
    /// (stremio-web src/common/Platform/shell/useShell.ts, stremio-shell-ng src/stremio_app/ipc.rs).
    /// stremio-web switches to it whenever window.chrome.webview exists, so every WebView2 host
    /// gets its player commands; this one forwards them to VLC.
    /// </summary>
    internal sealed class ShellHost : IDisposable
    {
        private const int SignalMessage = 1;
        private const int InitMessage = 3;
        // stremio-web treats a window state of 9 as "minimized" (pauses if pauseOnMinimize is on).
        private const int MinimizedWindowState = 9;

        private readonly CoreWebView2 _webView;
        private readonly VlcShellPlayer _player;
        // The console is always full screen; tell stremio-web so, which also makes its Escape
        // (sent for B) close player menus without leaving the player.
        private static readonly bool IsXbox = AnalyticsInfo.VersionInfo.DeviceFamily == "Windows.Xbox";

        private readonly Uri _trustedOrigin;
        private bool _isFullScreen;

        public ShellHost(CoreWebView2 webView, VlcShellPlayer player, Uri trustedOrigin)
        {
            _webView = webView;
            _player = player;
            _trustedOrigin = trustedOrigin;
            _isFullScreen = IsXbox || ApplicationView.GetForCurrentView().IsFullScreenMode;
            _webView.WebMessageReceived += OnWebMessageReceived;
            _player.SignalRaised += SendSignal;
            Window.Current.VisibilityChanged += OnWindowVisibilityChanged;
            Window.Current.SizeChanged += OnWindowSizeChanged;
        }

        public void Dispose()
        {
            _webView.WebMessageReceived -= OnWebMessageReceived;
            _player.SignalRaised -= SendSignal;
            Window.Current.VisibilityChanged -= OnWindowVisibilityChanged;
            Window.Current.SizeChanged -= OnWindowSizeChanged;
        }

        private void OnWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            if (!IsTrustedSource(e.Source))
            {
                return;
            }
            try
            {
                JsonObject message;
                if (!JsonObject.TryParse(e.TryGetWebMessageAsString(), out message))
                {
                    return;
                }
                // Diagnostics from the page scripts (e.g. XboxNavigationScript).
                var pageLog = message.GetNamedValue("uwpLog", JsonValue.CreateNullValue());
                if (pageLog.ValueType == JsonValueType.String)
                {
                    Log.Write("Page: " + pageLog.GetString());
                    return;
                }
                var args = message.GetNamedValue("args", JsonValue.CreateNullValue());
                if (args.ValueType != JsonValueType.Array || args.GetArray().Count == 0)
                {
                    if ((int)message.GetNamedNumber("type", 0) == InitMessage)
                    {
                        Log.Write("Stremio web connected to the shell");
                        SendHandshake();
                    }
                    return;
                }
                var argList = args.GetArray();
                if (argList[0].ValueType != JsonValueType.String)
                {
                    return;
                }
                var method = argList[0].GetString();
                var parameter = argList.Count > 1 ? argList[1] : null;
                if (method.StartsWith("mpv-", StringComparison.Ordinal))
                {
                    _player.HandleShellMethod(method, parameter);
                }
                else
                {
                    Log.Write("Shell method: " + method);
                    HandleAppMethod(method, parameter);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Shell message failed", ex);
            }
        }

        private void HandleAppMethod(string method, IJsonValue parameter)
        {
            switch (method)
            {
                case "app-ready":
                    Log.Write("Stremio web is ready");
                    SendVisibility();
                    break;
                case "win-set-visibility":
                    if (parameter != null && parameter.ValueType == JsonValueType.Object)
                    {
                        SetFullScreen(parameter.GetObject().GetNamedBoolean("fullscreen", _isFullScreen));
                    }
                    break;
                case "open-external":
                    if (parameter != null && parameter.ValueType == JsonValueType.String)
                    {
                        OpenExternal(parameter.GetString());
                    }
                    break;
                case "quit":
                    Application.Current.Exit();
                    break;
                // media.status, discord-*, autoupdater-*, etc. only apply to the desktop shell.
            }
        }

        private void SendHandshake()
        {
            var version = Package.Current.Id.Version;
            var properties = new JsonArray
            {
                new JsonArray(),
                Property("shellVersion", string.Format("{0}.{1}.{2} (UWP, VLC)", version.Major, version.Minor, version.Build)),
                Property("gpuVideoProcessing", "false"),
                Property("cacheDirectoryPicker", "false"),
                // Empty: the user picks a streaming server in Stremio's settings.
                Property("streamingServerUrl", string.Empty),
                Property("nativeInterfaceScale", "false"),
                // Subtitles from addons are drawn by the web page over the video.
                Property("nativeAssSubtitles", "false"),
            };
            var onEvent = new JsonArray { JsonValue.CreateStringValue("onEvent"), JsonValue.CreateStringValue(string.Empty) };
            var transport = new JsonObject();
            transport.SetNamedValue("properties", properties);
            transport.SetNamedValue("signals", new JsonArray());
            transport.SetNamedValue("methods", new JsonArray { onEvent });
            var data = new JsonObject();
            data.SetNamedValue("transport", transport);

            var message = new JsonObject();
            message.SetNamedValue("id", JsonValue.CreateNumberValue(0));
            message.SetNamedValue("object", JsonValue.CreateStringValue("transport"));
            message.SetNamedValue("type", JsonValue.CreateNumberValue(InitMessage));
            message.SetNamedValue("data", data);
            Post(message);
        }

        private void SendSignal(string name, IJsonValue payload)
        {
            var args = new JsonArray { JsonValue.CreateStringValue(name) };
            if (payload != null)
            {
                args.Add(payload);
            }
            var message = new JsonObject();
            message.SetNamedValue("id", JsonValue.CreateNumberValue(1));
            message.SetNamedValue("object", JsonValue.CreateStringValue("transport"));
            message.SetNamedValue("type", JsonValue.CreateNumberValue(SignalMessage));
            message.SetNamedValue("args", args);
            Post(message);
        }

        private void Post(JsonObject message)
        {
            try
            {
                // useShell.ts JSON.parses event.data, so this must be a string message.
                _webView.PostWebMessageAsString(message.Stringify());
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Shell post failed: " + ex.Message);
            }
        }

        private void SendVisibility()
        {
            var payload = new JsonObject();
            payload.SetNamedValue("visible", JsonValue.CreateBooleanValue(true));
            payload.SetNamedValue("visibility", JsonValue.CreateNumberValue(1));
            payload.SetNamedValue("isFullscreen", JsonValue.CreateBooleanValue(_isFullScreen));
            SendSignal("win-visibility-changed", payload);
        }

        private void OnWindowVisibilityChanged(object sender, VisibilityChangedEventArgs e)
        {
            // Never report the window as closed: with "quit on close" enabled stremio-web would ask
            // the app to exit every time the user switched away from it.
            var payload = new JsonObject();
            payload.SetNamedValue("state", JsonValue.CreateNumberValue(e.Visible ? 0 : MinimizedWindowState));
            SendSignal("win-state-changed", payload);
        }

        private void OnWindowSizeChanged(object sender, WindowSizeChangedEventArgs e)
        {
            if (IsXbox)
            {
                return;
            }
            var isFullScreen = ApplicationView.GetForCurrentView().IsFullScreenMode;
            if (isFullScreen != _isFullScreen)
            {
                _isFullScreen = isFullScreen;
                SendVisibility();
            }
        }

        private void SetFullScreen(bool fullScreen)
        {
            if (IsXbox)
            {
                SendVisibility();
                return;
            }
            var view = ApplicationView.GetForCurrentView();
            if (fullScreen)
            {
                view.TryEnterFullScreenMode();
            }
            else
            {
                view.ExitFullScreenMode();
            }
            _isFullScreen = view.IsFullScreenMode;
            SendVisibility();
        }

        private bool IsTrustedSource(string source)
        {
            Uri uri;
            return Uri.TryCreate(source, UriKind.Absolute, out uri) &&
                uri.Scheme == _trustedOrigin.Scheme &&
                string.Equals(uri.Host, _trustedOrigin.Host, StringComparison.OrdinalIgnoreCase);
        }

        internal static void OpenExternal(string url)
        {
            Uri uri;
            if (Uri.TryCreate(url, UriKind.Absolute, out uri) &&
                (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
            {
                var ignored = Launcher.LaunchUriAsync(uri);
            }
        }

        private static JsonArray Property(string name, string value)
        {
            return new JsonArray
            {
                JsonValue.CreateStringValue(string.Empty),
                JsonValue.CreateStringValue(name),
                JsonValue.CreateStringValue(string.Empty),
                JsonValue.CreateStringValue(value),
            };
        }
    }
}
