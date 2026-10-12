using System;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Stremio.Diagnostics;
using Windows.Data.Json;
using Windows.Gaming.Input;
using Windows.UI.Xaml;

namespace Stremio.Input
{
    /// <summary>A text box in the page that the user selected with the controller.</summary>
    internal sealed class TextEntryRequest
    {
        public TextEntryRequest(string label, string type, string value)
        {
            Label = label;
            Type = type;
            Value = value;
        }

        public string Label { get; }
        public string Type { get; }
        public string Value { get; }
    }

    /// <summary>
    /// Drives web.stremio.com with a controller by turning it into keyboard input. stremio-web
    /// already supports the keyboard everywhere (its spatial-navigation polyfill moves focus with
    /// the arrow keys, Enter activates, Escape closes menus). Its own gamepad support only covers
    /// some screens, and inside WebView2 on Xbox it never sees the A or B buttons.
    /// </summary>
    internal sealed class ControllerBridge : IDisposable
    {
        /// <summary>
        /// Hides the Gamepad API from the page so stremio-web's own gamepad navigation doesn't move
        /// focus a second time for every stick movement. Run before the page's scripts.
        /// </summary>
        public const string HideGamepadApiScript = @"(function () {
    if (window.top !== window) return;
    try {
        Object.defineProperty(Navigator.prototype, 'getGamepads', {
            configurable: true, writable: true, value: function () { return []; }
        });
    } catch (e) {}
    var swallow = function (e) { e.stopImmediatePropagation(); };
    window.addEventListener('gamepadconnected', swallow, true);
    window.addEventListener('gamepaddisconnected', swallow, true);
})();";

        /// <summary>
        /// Xbox only, run before the page's scripts. One press of B must go back exactly once, and
        /// ControllerBridge handles B, so drop the browser's own back actions (the console also
        /// turns B into browser back) and any Escape the bridge didn't send. Also hides the nav bar's
        /// fullscreen button, which does nothing on a console that is always full screen.
        /// </summary>
        public const string XboxPageScript = @"(function () {
    if (window.top !== window) return;
    var block = function (e) { e.preventDefault(); e.stopImmediatePropagation(); };
    window.addEventListener('keydown', function (e) {
        if (e.key === 'BrowserBack' || e.key === 'BrowserForward') return block(e);
        if (e.key === 'Escape') {
            if (window.__uwpAllowEscape && Date.now() - window.__uwpAllowEscape < 1000) { window.__uwpAllowEscape = 0; return; }
            block(e);
        }
    }, true);
    ['mousedown', 'mouseup', 'auxclick'].forEach(function (type) {
        window.addEventListener(type, function (e) { if (e.button === 3 || e.button === 4) block(e); }, true);
    });
    var style = document.createElement('style');
    style.textContent = '[class*=""horizontal-nav-bar""] [class*=""buttons-container""] > [class*=""button-container""]' +
        ':not([class*=""menu-button-container""]):not([class*=""back-button-container""]) { visibility: hidden !important; }';
    (document.head || document.documentElement).appendChild(style);
})();";

        private const string AllowEscapeScript = "window.__uwpAllowEscape = Date.now();";

        // Describes the focused element and whether a menu or the player is open. A focused text
        // box is tagged so the typed text can be put back into the same element afterwards.
        private const string PageStateScript = @"(function () {
    var old = document.querySelectorAll('[data-uwp-text-target]');
    for (var i = 0; i < old.length; i++) old[i].removeAttribute('data-uwp-text-target');
    var el = document.activeElement, state = { editable: false, overlay: false, player: location.hash.indexOf('#/player') === 0 };
    if (el && !el.disabled && !el.readOnly && (el.tagName === 'TEXTAREA' ||
        (el.tagName === 'INPUT' && /^(text|email|password|search|url|tel|number)$/i.test(el.type || 'text')))) {
        state.editable = true;
        state.type = (el.type || 'text').toLowerCase();
        state.label = el.getAttribute('placeholder') || el.getAttribute('aria-label') || '';
        state.value = state.type === 'password' ? '' : String(el.value || '');
        el.setAttribute('data-uwp-text-target', '1');
    }
    var modals = document.querySelectorAll('.modals-container');
    for (var j = 0; j < modals.length; j++) if (modals[j].children.length > 0) state.overlay = true;
    for (var n = el; !state.overlay && n && n !== document.body; n = n.parentElement) {
        if (typeof n.className === 'string' && /(popup|dropdown|bottom-sheet|context-menu|action-menu)/i.test(n.className)) state.overlay = true;
    }
    // The player's subtitles/audio/speed menus and side drawer.
    if (state.player && document.querySelector('[class*=""menu-layer""], [class*=""side-drawer-layer""]')) state.overlay = true;
    return state;
})()";

        // Sets the value through the native setter so React notices the change.
        private const string SetTextScript = @"(function (value) {
    var el = document.querySelector('[data-uwp-text-target]');
    if (!el) return false;
    el.removeAttribute('data-uwp-text-target');
    var proto = el.tagName === 'TEXTAREA' ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
    Object.getOwnPropertyDescriptor(proto, 'value').set.call(el, value);
    el.dispatchEvent(new Event('input', { bubbles: true }));
    el.dispatchEvent(new Event('change', { bubbles: true }));
    el.focus();
    return true;
})(VALUE)";

        private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(30);
        private static readonly TimeSpan FirstRepeatDelay = TimeSpan.FromMilliseconds(400);
        private static readonly TimeSpan RepeatInterval = TimeSpan.FromMilliseconds(110);

        private static readonly Key ArrowUp = new Key("ArrowUp", "ArrowUp", 38, null);
        private static readonly Key ArrowDown = new Key("ArrowDown", "ArrowDown", 40, null);
        private static readonly Key ArrowLeft = new Key("ArrowLeft", "ArrowLeft", 37, null);
        private static readonly Key ArrowRight = new Key("ArrowRight", "ArrowRight", 39, null);
        private static readonly Key Enter = new Key("Enter", "Enter", 13, "\r");
        private static readonly Key Escape = new Key("Escape", "Escape", 27, null);
        private static readonly Key Space = new Key(" ", "Space", 32, " ");

        private const string FocusDiagnosticsScript = @"(function () {
    var el = document.activeElement;
    return 'page focused: ' + document.hasFocus() + ', active element: ' +
        (el ? el.tagName + (typeof el.className === 'string' && el.className ? '.' + el.className.split(' ')[0] : '') : 'none');
})()";
        private const int DiagnosticPresses = 5;

        private readonly CoreWebView2 _webView;
        private readonly Func<TextEntryRequest, Task<string>> _requestText;
        private readonly Action _ensureFocus;
        private readonly DispatcherTimer _timer = new DispatcherTimer();
        private GamepadButtons _previousButtons;
        private Key _heldDirection;
        private DateTime _nextRepeat;
        private bool _busy;
        private int _loggedPresses;

        /// <param name="ensureFocus">Gives the web view keyboard focus if something else has it.</param>
        public ControllerBridge(CoreWebView2 webView, Func<TextEntryRequest, Task<string>> requestText, Action ensureFocus)
        {
            _webView = webView;
            _requestText = requestText;
            _ensureFocus = ensureFocus;
            Key ignored;
            ReadControllers(out _previousButtons, out ignored);
            _timer.Interval = PollInterval;
            _timer.Tick += OnTick;
            _timer.Start();
        }

        /// <summary>While true the controller is left to XAML, e.g. for the load error screen.</summary>
        public bool Paused { get; set; }

        public void Dispose()
        {
            _timer.Stop();
        }

        /// <summary>
        /// Makes the page behave as focused. Without the console's mouse-mode cursor nothing ever
        /// clicks into the page, so the browser treats it as unfocused and doesn't show which
        /// element keyboard navigation has selected. Call after each page load.
        /// </summary>
        public async void EmulatePageFocus()
        {
            try
            {
                await _webView.CallDevToolsProtocolMethodAsync("Emulation.setFocusEmulationEnabled", "{\"enabled\":true}");
                Log.Write("Page focus emulation on");
            }
            catch (Exception ex)
            {
                Log.Error("Page focus emulation failed", ex);
            }
        }

        private void OnTick(object sender, object e)
        {
            GamepadButtons buttons;
            Key direction;
            ReadControllers(out buttons, out direction);
            var pressed = buttons & ~_previousButtons;
            _previousButtons = buttons;
            if (Paused || _busy)
            {
                _heldDirection = null;
                return;
            }

            var now = DateTime.UtcNow;
            if (direction != _heldDirection)
            {
                _heldDirection = direction;
                if (direction != null)
                {
                    Press(direction);
                    _nextRepeat = now + FirstRepeatDelay;
                }
            }
            else if (direction != null && now >= _nextRepeat)
            {
                Press(direction);
                _nextRepeat = now + RepeatInterval;
            }

            if ((pressed & GamepadButtons.A) != 0)
            {
                OnSelect();
            }
            else if ((pressed & GamepadButtons.B) != 0)
            {
                OnBack();
            }
            else if ((pressed & GamepadButtons.X) != 0)
            {
                OnPlayPause();
            }
        }

        // A: activate the focused element, or type into a focused text box.
        private async void OnSelect()
        {
            _busy = true;
            try
            {
                var state = await GetPageStateAsync();
                if (state != null && state.GetNamedBoolean("editable", false))
                {
                    var text = await _requestText(new TextEntryRequest(
                        state.GetNamedString("label", string.Empty),
                        state.GetNamedString("type", "text"),
                        state.GetNamedString("value", string.Empty)));
                    if (text != null)
                    {
                        await _webView.ExecuteScriptAsync(SetTextScript.Replace("VALUE", JsonValue.CreateStringValue(text).Stringify()));
                    }
                    return;
                }
                await PressAsync(Enter);
            }
            catch (Exception ex)
            {
                Log.Error("Controller select failed", ex);
            }
            finally
            {
                _busy = false;
            }
        }

        // B: close the open menu, otherwise go back one page (out of the player: to the stream list).
        private async void OnBack()
        {
            _busy = true;
            try
            {
                var state = await GetPageStateAsync();
                if (state == null)
                {
                    return;
                }
                if (state.GetNamedBoolean("overlay", false))
                {
                    Log.Write("Controller back: closing the open menu");
                    await _webView.ExecuteScriptAsync(AllowEscapeScript);
                    await PressAsync(Escape);
                }
                else if (_webView.CanGoBack)
                {
                    Log.Write("Controller back: previous page");
                    _webView.GoBack();
                }
            }
            catch (Exception ex)
            {
                Log.Error("Controller back failed", ex);
            }
            finally
            {
                _busy = false;
            }
        }

        // X: play / pause in the player (stremio-web's Space shortcut).
        private async void OnPlayPause()
        {
            _busy = true;
            try
            {
                var state = await GetPageStateAsync();
                if (state != null && state.GetNamedBoolean("player", false) && !state.GetNamedBoolean("editable", false))
                {
                    await PressAsync(Space);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Controller play/pause failed", ex);
            }
            finally
            {
                _busy = false;
            }
        }

        private async void Press(Key key)
        {
            try
            {
                await PressAsync(key);
            }
            catch (Exception ex)
            {
                Log.Error("Controller key press failed", ex);
            }
        }

        // Sends a real (trusted) key press through the DevTools protocol, so default actions such
        // as following a focused link work too.
        private async Task PressAsync(Key key)
        {
            _ensureFocus();
            await _webView.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", key.ToEvent(key.Text != null ? "keyDown" : "rawKeyDown"));
            await _webView.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", key.ToEvent("keyUp"));
            if (_loggedPresses < DiagnosticPresses)
            {
                _loggedPresses++;
                var state = await _webView.ExecuteScriptAsync(FocusDiagnosticsScript);
                Log.Write("Controller sent " + key.Code + "; " + state);
            }
        }

        private async Task<JsonObject> GetPageStateAsync()
        {
            var result = await _webView.ExecuteScriptAsync(PageStateScript);
            JsonObject state;
            return JsonObject.TryParse(result, out state) ? state : null;
        }

        private static void ReadControllers(out GamepadButtons buttons, out Key direction)
        {
            buttons = GamepadButtons.None;
            direction = null;
            foreach (var gamepad in Gamepad.Gamepads)
            {
                var reading = gamepad.GetCurrentReading();
                buttons |= reading.Buttons;
                if (direction == null)
                {
                    direction = GetDirection(reading);
                }
            }
        }

        // D-pad only: on Xbox the left stick already moves the console's mouse-mode cursor.
        private static Key GetDirection(GamepadReading reading)
        {
            if ((reading.Buttons & GamepadButtons.DPadUp) != 0) return ArrowUp;
            if ((reading.Buttons & GamepadButtons.DPadDown) != 0) return ArrowDown;
            if ((reading.Buttons & GamepadButtons.DPadLeft) != 0) return ArrowLeft;
            if ((reading.Buttons & GamepadButtons.DPadRight) != 0) return ArrowRight;
            return null;
        }

        private sealed class Key
        {
            public Key(string name, string code, int virtualKey, string text)
            {
                Name = name;
                Code = code;
                VirtualKey = virtualKey;
                Text = text;
            }

            public string Name { get; }
            public string Code { get; }
            public int VirtualKey { get; }
            public string Text { get; }

            public string ToEvent(string type)
            {
                var args = new JsonObject();
                args.SetNamedValue("type", JsonValue.CreateStringValue(type));
                args.SetNamedValue("key", JsonValue.CreateStringValue(Name));
                args.SetNamedValue("code", JsonValue.CreateStringValue(Code));
                args.SetNamedValue("windowsVirtualKeyCode", JsonValue.CreateNumberValue(VirtualKey));
                args.SetNamedValue("nativeVirtualKeyCode", JsonValue.CreateNumberValue(VirtualKey));
                if (Text != null && type == "keyDown")
                {
                    args.SetNamedValue("text", JsonValue.CreateStringValue(Text));
                    args.SetNamedValue("unmodifiedText", JsonValue.CreateStringValue(Text));
                }
                return args.Stringify();
            }
        }
    }
}
