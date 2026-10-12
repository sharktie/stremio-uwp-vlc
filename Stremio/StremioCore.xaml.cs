using System;
using System.Threading.Tasks;
using LibVLCSharp.Platforms.Windows;
using Microsoft.Web.WebView2.Core;
using Stremio.Diagnostics;
using Stremio.Input;
using Stremio.Playback;
using Stremio.Shell;
using Windows.System;
using Windows.System.Profile;
using Windows.UI.Core;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Markup;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

namespace Stremio
{
    /// <summary>
    /// Core Display Page for Stremio Content. web.stremio.com runs in a WebView2 on top of a VLC
    /// video surface; the web player's playback commands are played by VLC instead of Edge.
    /// </summary>
    public sealed partial class MainPage : Page
    {
        private static readonly Uri WebAppUri = new Uri("https://web.stremio.com/");

        private readonly VlcShellPlayer _player;
        private ShellHost _shell;
        private BuiltInStreamingServer _streamingServer;
        private ControllerBridge _controller;
        private ulong _appNavigationId;
        private bool _videoViewRequested;
        private bool _videoViewStarting;

        public MainPage()
        {
            Log.Write("Main page starting");
            this.InitializeComponent();
            _player = new VlcShellPlayer(Dispatcher);
            _player.MediaPlayerCreated += mediaPlayer =>
            {
                if (VideoView != null)
                {
                    VideoView.MediaPlayer = mediaPlayer;
                }
            };
            _player.VideoVisibleChanged += visible =>
            {
                if (VideoView != null)
                {
                    VideoView.Opacity = visible ? 1 : 0;
                }
            };
            PreviewKeyDown += OnPreviewKeyDown;
            Loaded += OnLoaded;
            StartHeartbeat();
        }

        // Logs that the UI thread is still running for the first half minute, so a freeze shows up
        // in the log as missing heartbeats.
        private void StartHeartbeat()
        {
            var beats = 0;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            timer.Tick += (s, e) =>
            {
                beats++;
                Log.Write("Still responsive (" + beats * 5 + "s)");
                if (beats >= 6)
                {
                    timer.Stop();
                }
            };
            timer.Start();
        }

        /// <summary>
        /// Called for an unhandled exception. If it came from the VLC video surface while it was
        /// starting, drop the surface and keep Stremio running without VLC playback.
        /// </summary>
        internal bool TryRecoverFromVideoFailure(Exception error)
        {
            if (!_videoViewStarting)
            {
                return false;
            }
            _videoViewStarting = false;
            Log.Write("The VLC video surface failed to start; continuing without VLC playback");
            try
            {
                if (VideoView != null)
                {
                    XamlMarkupHelper.UnloadObject(VideoView);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Removing the VLC video surface failed", ex);
            }
            _player.SetUnavailable("the video surface failed to start (" + error.Message + ")");
            return true;
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            SystemNavigationManager.GetForCurrentView().BackRequested -= OnBackRequested;
            _controller?.Dispose();
            _streamingServer?.Dispose();
            _shell?.Dispose();
            _player.Dispose();
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            Loaded -= OnLoaded;
            Log.Write("Main page loaded, starting WebView2");
            SystemNavigationManager.GetForCurrentView().BackRequested += OnBackRequested;
            try
            {
                await WebView.EnsureCoreWebView2Async();
            }
            catch (Exception ex)
            {
                Log.Error("WebView2 failed to start", ex);
                ShowLoadError("The web view could not start. " + ex.Message);
                return;
            }
            Log.Write("WebView2 ready");

            var core = WebView.CoreWebView2;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            // No browser shortcuts such as the back key: B is handled by ControllerBridge only.
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
#if !DEBUG
            core.Settings.AreDevToolsEnabled = false;
#endif
            core.NavigationStarting += OnNavigationStarting;
            core.NavigationCompleted += OnNavigationCompleted;
            core.NewWindowRequested += OnNewWindowRequested;
            core.ProcessFailed += OnProcessFailed;
            // Must be listening before the page loads: stremio-web says hello as soon as it starts.
            _shell = new ShellHost(core, _player, WebAppUri);
            _streamingServer = new BuiltInStreamingServer(core);
            try
            {
                await core.AddScriptToExecuteOnDocumentCreatedAsync(ControllerBridge.HideGamepadApiScript);
                if (AnalyticsInfo.VersionInfo.DeviceFamily == "Windows.Xbox")
                {
                    await core.AddScriptToExecuteOnDocumentCreatedAsync(ControllerBridge.XboxPageScript);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Injecting the controller script failed", ex);
            }
            _controller = new ControllerBridge(core, RequestTextAsync, EnsureWebViewFocus);
            _controller.EmulatePageFocus();
            core.Navigate(WebAppUri.AbsoluteUri);
            WebView.Focus(FocusState.Programmatic);
        }

        private void EnsureWebViewFocus()
        {
            if (LoadErrorPanel.Visibility != Visibility.Collapsed)
            {
                return;
            }
            for (var element = FocusManager.GetFocusedElement() as DependencyObject; element != null; element = VisualTreeHelper.GetParent(element))
            {
                if (element == WebView)
                {
                    return;
                }
            }
            Log.Write("Giving the web view focus back");
            WebView.Focus(FocusState.Programmatic);
        }

        // Lets the user type into a text box in the page with the console's on-screen keyboard,
        // which reliably works with a XAML text box.
        private async Task<TextEntryResult> RequestTextAsync(TextEntryRequest request)
        {
            var isPassword = request.Type == "password";
            var passwordBox = new PasswordBox();
            var textBox = new TextBox { Text = request.Value ?? string.Empty };
            if (request.Type == "email")
            {
                var scope = new InputScope();
                scope.Names.Add(new InputScopeName(InputScopeNameValue.EmailSmtpAddress));
                textBox.InputScope = scope;
            }
            Control input = isPassword ? (Control)passwordBox : textBox;

            var dialog = new ContentDialog
            {
                Title = string.IsNullOrWhiteSpace(request.Label) ? "Enter text" : request.Label,
                Content = input,
                PrimaryButtonText = "OK",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
            };
            var submitted = false;
            // Accepting the on-screen keyboard sends Enter: treat that as OK, and let the page
            // move on to its next box (see ControllerBridge).
            input.KeyDown += (s, e) =>
            {
                if (e.Key == VirtualKey.Enter)
                {
                    e.Handled = true;
                    submitted = true;
                    dialog.Hide();
                }
            };
            // Ask for the keyboard whenever the box gets focus, and again shortly after the dialog
            // opens: a password box isn't always ready for it straight away.
            input.GotFocus += (s, e) => ShowOnScreenKeyboard();
            dialog.Opened += async (s, e) =>
            {
                input.Focus(FocusState.Keyboard);
                await Task.Delay(TimeSpan.FromMilliseconds(200));
                if (input.FocusState == FocusState.Unfocused)
                {
                    input.Focus(FocusState.Keyboard);
                }
                ShowOnScreenKeyboard();
            };

            var result = await dialog.ShowAsync();
            WebView.Focus(FocusState.Programmatic);
            if (result != ContentDialogResult.Primary && !submitted)
            {
                return null;
            }
            return new TextEntryResult(isPassword ? passwordBox.Password : textBox.Text, submitted);
        }

        private static void ShowOnScreenKeyboard()
        {
            try
            {
                InputPane.GetForCurrentView().TryShow();
            }
            catch (Exception ex)
            {
                Log.Error("Showing the on-screen keyboard failed", ex);
            }
        }

        private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            // ControllerBridge handles the controller. Stop XAML from also using its buttons to move
            // focus out of the web page, except on the load error screen where they work its button.
            var key = (int)e.Key;
            var isController = (key >= (int)VirtualKey.GamepadA && key <= (int)VirtualKey.GamepadRightThumbstickLeft) ||
                (key >= (int)VirtualKey.NavigationView && key <= (int)VirtualKey.NavigationCancel);
            if (isController && LoadErrorPanel.Visibility == Visibility.Collapsed)
            {
                e.Handled = true;
            }
        }

        // Realizes the x:Load="False" VideoView. Done after the web app loads so that a problem in
        // VLC's video surface can't keep Stremio from starting.
        private void CreateVideoView()
        {
            if (_videoViewRequested)
            {
                return;
            }
            _videoViewRequested = true;
            try
            {
                Log.Write("Creating the VLC video surface");
                _videoViewStarting = true;
                FindName(nameof(VideoView));
                VideoView.Initialized += VideoView_Initialized;
            }
            catch (Exception ex)
            {
                _videoViewStarting = false;
                Log.Error("Creating the VLC video surface failed", ex);
                _player.SetUnavailable("the video surface could not be created (" + ex.Message + ")");
            }
        }

        private void VideoView_Initialized(object sender, InitializedEventArgs e)
        {
            _videoViewStarting = false;
            Log.Write("VLC video surface ready");
            _player.Initialize(e.SwapChainOptions);
        }

        private void VideoView_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            _player.SetViewSize(e.NewSize.Width, e.NewSize.Height);
        }

        private void OnNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs e)
        {
            if (IsWebApp(e.Uri))
            {
                _appNavigationId = e.NavigationId;
                Log.Write("Loading " + Log.DescribeUrl(e.Uri));
                return;
            }
            Log.Write("Opening outside the app: " + Log.DescribeUrl(e.Uri));
            e.Cancel = true;
            ShellHost.OpenExternal(e.Uri);
        }

        private void OnNavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (e.NavigationId != _appNavigationId)
            {
                return;
            }
            Log.Write("Web app load finished: " + (e.IsSuccess ? "success" : e.WebErrorStatus.ToString()));
            if (e.IsSuccess)
            {
                LoadErrorPanel.Visibility = Visibility.Collapsed;
                if (_controller != null)
                {
                    _controller.Paused = false;
                    _controller.EmulatePageFocus();
                }
                CreateVideoView();
            }
            else if (e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
            {
                ShowLoadError("Check the console's internet connection. (" + e.WebErrorStatus + ")");
            }
        }

        private void OnNewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            e.Handled = true;
            if (IsWebApp(e.Uri))
            {
                sender.Navigate(e.Uri);
            }
            else
            {
                ShellHost.OpenExternal(e.Uri);
            }
        }

        private void OnProcessFailed(CoreWebView2 sender, CoreWebView2ProcessFailedEventArgs e)
        {
            Log.Write("WebView2 process failed: " + e.ProcessFailedKind);
            if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited ||
                e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
            {
                sender.Reload();
            }
            else if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
            {
                ShowLoadError("The web view stopped unexpectedly.");
            }
        }

        private void OnBackRequested(object sender, BackRequestedEventArgs e)
        {
            // ControllerBridge already handles B; handling it here too would go back twice.
            if (LoadErrorPanel.Visibility == Visibility.Collapsed)
            {
                e.Handled = true;
            }
        }

        private void RetryButton_Click(object sender, RoutedEventArgs e)
        {
            // A fresh page also recovers from a crashed browser process.
            Frame.Navigate(typeof(MainPage));
            Frame.BackStack.Clear();
        }

        private void ShowLoadError(string message)
        {
            LoadErrorText.Text = message;
            LoadErrorPanel.Visibility = Visibility.Visible;
            if (_controller != null)
            {
                _controller.Paused = true;
            }
            RetryButton.Focus(FocusState.Programmatic);
        }

        private static bool IsWebApp(string url)
        {
            Uri uri;
            return Uri.TryCreate(url, UriKind.Absolute, out uri) &&
                uri.Scheme == WebAppUri.Scheme &&
                string.Equals(uri.Host, WebAppUri.Host, StringComparison.OrdinalIgnoreCase);
        }
    }
}
