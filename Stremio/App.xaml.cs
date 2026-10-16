using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Stremio.Diagnostics;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.UI;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

namespace Stremio
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    sealed partial class App : Application
    {
        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            Log.Start();
            Log.Write("App starting, version " + VersionText());
            this.UnhandledException += OnUnhandledException;
            TaskScheduler.UnobservedTaskException += (s, e) => Log.Error("Unobserved task exception", e.Exception);

            // WebView2 reads these when it starts its browser process, so they must be set before the
            // first page loads. A transparent background lets the VLC video under the page show through.
            Environment.SetEnvironmentVariable("WEBVIEW2_DEFAULT_BACKGROUND_COLOR", "0");
            // Let web.stremio.com reach a streaming server on the local network over plain http
            // (e.g. http://192.168.1.10:11470), which browsers block on an https page.
            Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS",
                "--autoplay-policy=no-user-gesture-required --allow-running-insecure-content " +
                "--disable-features=BlockInsecurePrivateNetworkRequests,PrivateNetworkAccessSendPreflights," +
                "PrivateNetworkAccessRespectPreflightResults,LocalNetworkAccessChecks");

            this.InitializeComponent();
            // Xbox: no mouse-mode pointer; the controller drives the page's highlight (ControllerBridge).
            this.RequiresPointerMode = ApplicationRequiresPointerMode.WhenRequested;
            this.Suspending += OnSuspending;
        }

        /// <summary>
        /// Invoked when the application is launched normally by the end user.  Other entry points
        /// will be used such as when the application is launched to open a specific file.
        /// </summary>
        /// <param name="e">Details about the launch request and process.</param>
        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            // Xbox: use the whole screen instead of the TV-safe area so video fills the TV.
            ApplicationView.GetForCurrentView().SetDesiredBoundsMode(ApplicationViewBoundsMode.UseCoreWindow);

            Frame rootFrame = Window.Current.Content as Frame;

            // Do not repeat app initialization when the Window already has content,
            // just ensure that the window is active
            if (rootFrame == null)
            {
                // Create a Frame to act as the navigation context and navigate to the first page
                rootFrame = new Frame();

                rootFrame.NavigationFailed += OnNavigationFailed;

                if (e.PreviousExecutionState == ApplicationExecutionState.Terminated)
                {
                    //TODO: Load state from previously suspended application
                }

                // Place the frame in the current Window
                Window.Current.Content = rootFrame;
            }

            if (e.PrelaunchActivated == false)
            {
                if (rootFrame.Content == null)
                {
                    // When the navigation stack isn't restored navigate to the first page,
                    // configuring the new page by passing required information as a navigation
                    // parameter
                    try
                    {
                        rootFrame.Navigate(typeof(MainPage), e.Arguments);
                    }
                    catch (Exception ex)
                    {
                        Log.Error("Main page failed to load", ex);
                        ShowFatalError(ex);
                    }
                }
                // Ensure the current window is active
                Window.Current.Activate();
                Log.Write("Window activated");
            }
        }

        private void OnUnhandledException(object sender, Windows.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            Log.Write("Unhandled exception: " + e.Message + Environment.NewLine + e.Exception);
            e.Handled = true;
            var page = (Window.Current.Content as Frame)?.Content as MainPage;
            if (page == null || !page.TryRecoverFromVideoFailure(e.Exception))
            {
                ShowFatalError(e.Exception);
            }
        }

        // Shows what went wrong instead of leaving the console on the splash screen.
        private static void ShowFatalError(Exception error)
        {
            var text = new TextBlock
            {
                Text = "Stremio hit an error and can't continue. Please send this to the developer:" +
                    Environment.NewLine + Environment.NewLine + "Version " + VersionText() + Environment.NewLine + error,
                Foreground = new SolidColorBrush(Colors.White),
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                Margin = new Thickness(48),
            };
            Window.Current.Content = new ScrollViewer
            {
                Content = text,
                Background = new SolidColorBrush(Colors.Black),
            };
            Window.Current.Activate();
        }

        private static string VersionText()
        {
            var version = Package.Current.Id.Version;
            return string.Format("{0}.{1}.{2}.{3}", version.Major, version.Minor, version.Build, version.Revision);
        }

        /// <summary>
        /// Invoked when Navigation to a certain page fails
        /// </summary>
        /// <param name="sender">The Frame which failed navigation</param>
        /// <param name="e">Details about the navigation failure</param>
        void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            throw new Exception("Failed to load Page " + e.SourcePageType.FullName);
        }

        /// <summary>
        /// Invoked when application execution is being suspended.  Application state is saved
        /// without knowing whether the application will be terminated or resumed with the contents
        /// of memory still intact.
        /// </summary>
        /// <param name="sender">The source of the suspend request.</param>
        /// <param name="e">Details about the suspend request.</param>
        private void OnSuspending(object sender, SuspendingEventArgs e)
        {
            var deferral = e.SuspendingOperation.GetDeferral();
            //TODO: Save application state and stop any background activity
            deferral.Complete();
        }
    }
}
