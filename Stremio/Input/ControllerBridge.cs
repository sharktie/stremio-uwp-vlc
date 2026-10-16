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

    /// <summary>What the user typed, and whether they finished with the keyboard's Enter key.</summary>
    internal sealed class TextEntryResult
    {
        public TextEntryResult(string text, bool submitted)
        {
            Text = text;
            Submitted = submitted;
        }

        public string Text { get; }
        public bool Submitted { get; }
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
    // The browser also turns the controller into a pointer (left stick moves it, A clicks) that a
    // page can't switch off. Unless the user turned the cursor on (View button), hide it and drop
    // its input before Stremio sees it, so A doesn't also click whatever is under it. Clicks from
    // code (el.click()) aren't trusted and clicks from Enter on a link have detail 0; both still work.
    document.documentElement.classList.add('uwp-no-pointer');
    ['pointerdown', 'pointerup', 'pointermove', 'mousedown', 'mouseup', 'mousemove',
        'click', 'dblclick', 'auxclick', 'contextmenu', 'wheel'].forEach(function (type) {
        window.addEventListener(type, function (e) {
            if (!e.isTrusted || !document.documentElement.classList.contains('uwp-no-pointer')) return;
            if (type === 'click' && e.detail === 0) return;
            block(e);
        }, { capture: true, passive: false });
    });
    var style = document.createElement('style');
    style.textContent = '[class*=""horizontal-nav-bar""] [class*=""buttons-container""] > [class*=""button-container""]' +
        ':not([class*=""menu-button-container""]):not([class*=""back-button-container""]) { visibility: hidden !important; }' +
        ' html.uwp-no-pointer, html.uwp-no-pointer * { cursor: none !important; }';
    (document.head || document.documentElement).appendChild(style);
})();";

        /// <summary>
        /// Xbox only, run before the page's scripts. TV-style focus navigation for the arrow keys the
        /// D-pad and left stick send, modelled on Nuvio UWP's focus.js. stremio-web's own keyboard
        /// navigation (a spatial-navigation polyfill) scrolls the page instead of moving to rows that
        /// aren't fully visible and skips the many buttons marked tabIndex -1, such as the sidebar.
        /// The highlighted element carries the uwp-focused class. In the player the arrow keys stay
        /// Stremio's (seek and volume) unless one of its menus is open.
        /// </summary>
        public const string XboxNavigationScript = @"(function () {
    if (window.top !== window) return;
    var SELECTOR = 'a[href], button, input, textarea, select, [tabindex], [role=""button""], [role=""link""], [role=""option""], [role=""menuitem""], [role=""tab""]';
    var TEXT = /^(text|search|email|url|password|tel|number)$/i;
    var DIRECTIONS = { ArrowUp: 'up', ArrowDown: 'down', ArrowLeft: 'left', ArrowRight: 'right' };
    var SIDEBAR = '[class*=""vertical-nav-bar""]';
    var lastContent = null;
    var reports = 0;

    // The first moves go to the app's log (stremio-vlc.log), to diagnose navigation on the console.
    function describe(el) {
        if (!el) return 'nothing';
        var r = el.getBoundingClientRect();
        return el.tagName + '.' + String(el.className).split(' ')[0] + ' ' + (el.getAttribute('href') || el.getAttribute('title') || (el.textContent || '').trim().slice(0, 24)) +
            ' @' + [Math.round(r.left), Math.round(r.top), Math.round(r.width), Math.round(r.height)].join(',');
    }
    function report(text) {
        if (reports >= 40) return;
        reports++;
        try { window.chrome.webview.postMessage(JSON.stringify({ uwpLog: text })); } catch (e) {}
    }

    function inPlayer() { return location.hash.indexOf('#/player') === 0; }
    function isText(el) { return !!el && (el.tagName === 'TEXTAREA' || (el.tagName === 'INPUT' && TEXT.test(el.type || 'text'))); }
    function shown(el) {
        if (!el || !el.isConnected || el.closest('[hidden], [aria-hidden=""true""], [inert]')) return false;
        var r = el.getBoundingClientRect();
        return r.width > 0 && r.height > 0 && getComputedStyle(el).visibility !== 'hidden';
    }
    function usable(el) {
        if (el.disabled || el.closest('[data-focus-guard]') || !shown(el)) return false;
        var r = el.getBoundingClientRect();
        // Skip tiny elements and focusable page-sized wrappers.
        return r.width >= 4 && r.height >= 4 && r.width * r.height < window.innerWidth * window.innerHeight * 0.6;
    }
    function inView(el) {
        var r = el.getBoundingClientRect();
        return r.bottom > 0 && r.top < window.innerHeight && r.right > 0 && r.left < window.innerWidth;
    }
    // Where the highlight may go: an open dialog, popup menu or player menu, otherwise the page.
    function scope() {
        var modals = document.querySelectorAll('.modals-container');
        for (var i = modals.length - 1; i >= 0; i--) if (modals[i].lastElementChild) return modals[i].lastElementChild;
        var menus = document.querySelectorAll('[class*=""portal-menu""], [class*=""label-container""] [class*=""menu-container""]');
        for (var j = menus.length - 1; j >= 0; j--) if (shown(menus[j])) return menus[j];
        if (inPlayer()) {
            var layers = document.querySelectorAll('[class*=""menu-layer""], [class*=""side-drawer-layer""]');
            for (var k = layers.length - 1; k >= 0; k--) if (shown(layers[k])) return layers[k];
        }
        return document.body;
    }
    function candidates(root) {
        var all = root.querySelectorAll(SELECTOR), list = [];
        for (var i = 0; i < all.length; i++) if (all[i] !== root && usable(all[i])) list.push(all[i]);
        // The clickable thing itself rather than a focusable wrapper around it. The list is in
        // document order, so an element has a candidate inside it exactly when the next one is.
        return list.filter(function (el, index) {
            return !(index + 1 < list.length && el.contains(list[index + 1]));
        });
    }
    function gap(a1, a2, b1, b2) { return b2 < a1 ? a1 - b2 : (b1 > a2 ? b1 - a2 : 0); }
    // Nuvio's findNext: the nearest element beyond this one's edge, staying in line first.
    function findNext(from, dir, list) {
        var a = from.getBoundingClientRect(), ax = a.left + a.width / 2, ay = a.top + a.height / 2;
        var best = null, bestScore = Infinity;
        for (var i = 0; i < list.length; i++) {
            var el = list[i];
            if (el === from || el.contains(from) || from.contains(el)) continue;
            var b = el.getBoundingClientRect(), bx = b.left + b.width / 2, by = b.top + b.height / 2;
            var sx = Math.min(a.width, b.width) * 0.25, sy = Math.min(a.height, b.height) * 0.25, primary, secondary;
            if (dir === 'right') { if (b.left < a.right - sx || bx <= ax) continue; primary = Math.max(0, b.left - a.right); secondary = gap(a.top, a.bottom, b.top, b.bottom); }
            else if (dir === 'left') { if (b.right > a.left + sx || bx >= ax) continue; primary = Math.max(0, a.left - b.right); secondary = gap(a.top, a.bottom, b.top, b.bottom); }
            else if (dir === 'down') { if (b.top < a.bottom - sy || by <= ay) continue; primary = Math.max(0, b.top - a.bottom); secondary = gap(a.left, a.right, b.left, b.right); }
            else { if (b.bottom > a.top + sy || by >= ay) continue; primary = Math.max(0, a.top - b.bottom); secondary = gap(a.left, a.right, b.left, b.right); }
            var horizontal = dir === 'left' || dir === 'right';
            var score = primary + secondary * (horizontal ? 6 : 2) + (horizontal ? Math.abs(by - ay) : Math.abs(bx - ax)) * 0.25;
            if (score < bestScore) { bestScore = score; best = el; }
        }
        return best;
    }
    function first(list) {
        var visible = list.filter(inView);
        var content = visible.filter(function (el) { return !el.closest('[class*=""nav-bar""]'); });
        var pool = content.length ? content : (visible.length ? visible : list);
        pool.sort(function (x, y) {
            var a = x.getBoundingClientRect(), b = y.getBoundingClientRect();
            return (a.top - b.top) || (a.left - b.left);
        });
        return pool[0];
    }
    function mark(el) {
        var previous = document.querySelectorAll('.uwp-focused');
        for (var i = 0; i < previous.length; i++) if (previous[i] !== el) previous[i].classList.remove('uwp-focused');
        el.classList.add('uwp-focused');
        if (!el.closest(SIDEBAR)) lastContent = el;
    }
    function focusOn(el, scroll) {
        mark(el);
        if (isText(el)) {
            // Text boxes take real focus only once typed into (ControllerBridge opens a keyboard for
            // them on A); focusing one would pop up the console's keyboard straight away.
            if (document.activeElement && document.activeElement !== document.body) document.activeElement.blur();
        } else {
            try { el.focus({ preventScroll: true }); } catch (e) {}
        }
        if (scroll) {
            try { el.scrollIntoView({ block: 'center', inline: 'nearest', behavior: 'smooth' }); } catch (e) {}
        }
    }
    function current(root) {
        var el = document.querySelector('.uwp-focused');
        return el && root.contains(el) && usable(el) ? el : null;
    }
    function move(dir, root) {
        var list = candidates(root);
        if (!list.length) return;
        var from = current(root);
        if (!from) { focusOn(first(list), true); return; }
        var next = null;
        // Back out of the sidebar to the card you left.
        if (dir === 'right' && from.closest(SIDEBAR) && lastContent && root.contains(lastContent) && usable(lastContent) && inView(lastContent)) next = lastContent;
        if (!next) next = findNext(from, dir, list);
        var detail = '';
        if (!next && dir === 'left') {
            var side = document.querySelectorAll(SIDEBAR + ' a');
            detail = ' (sidebar links: ' + side.length + ', usable: ' + list.filter(function (el) { return !!el.closest(SIDEBAR); }).length +
                ', first: ' + describe(side[0]) + ', viewport ' + window.innerWidth + 'x' + window.innerHeight + ')';
        }
        report(dir + ': ' + describe(from) + ' -> ' + describe(next) + detail);
        if (next) focusOn(next, true);
    }

    // Y on the controller: highlight the search box (ControllerBridge then opens the keyboard). Pages
    // without one go to Stremio's search page first.
    window.__uwpGoToSearch = function () {
        var input = document.querySelector('[class*=""search-bar""] input, input[class*=""search-input""]');
        if (input && usable(input)) { focusOn(input, true); return 'found'; }
        location.hash = '#/search';
        return 'navigated';
    };

    window.addEventListener('keydown', function (e) {
        var dir = DIRECTIONS[e.key];
        if (!dir || e.altKey || e.ctrlKey || e.metaKey || e.shiftKey) return;
        var root = scope();
        if (inPlayer() && root === document.body) return; // the player's own keys: seek and volume
        if (isText(document.activeElement) && (dir === 'left' || dir === 'right')) return; // move the caret
        e.preventDefault();
        e.stopImmediatePropagation();
        move(dir, root);
    }, true);

    // Keep the highlight on whatever the page itself focuses (e.g. the password box after the email).
    document.addEventListener('focusin', function (e) {
        var t = e.target;
        if (t && t !== document.body && t.matches && t.matches(SELECTOR)) mark(t);
    }, true);

    // Screens fill in as data arrives; whenever nothing has the highlight, give it to the first
    // thing on screen so there's always something to move from (not in the player).
    var pending = 0;
    function ensureHighlight() {
        pending = 0;
        if (inPlayer() || !document.body) return;
        var root = scope();
        if (current(root)) return;
        var list = candidates(root);
        if (list.length) focusOn(first(list), false);
    }
    function schedule() { if (!pending) pending = setTimeout(ensureHighlight, 150); }
    window.addEventListener('hashchange', schedule);
    document.addEventListener('DOMContentLoaded', function () {
        new MutationObserver(schedule).observe(document.body, { childList: true, subtree: true });
        schedule();
    });

    var style = document.createElement('style');
    style.textContent = '.uwp-focused { outline: 3px solid #ffffff !important; outline-offset: -3px !important; }';
    (document.head || document.documentElement).appendChild(style);
})();";

        /// <summary>Script that turns the page's handling of the console's cursor on or off.</summary>
        public static string SetPagePointerScript(bool enabled)
        {
            return "document.documentElement.classList.toggle('uwp-no-pointer', " + (enabled ? "false" : "true") + ");";
        }

        private const string AllowEscapeScript = "window.__uwpAllowEscape = Date.now();";

        private const string ClickTargetScript = @"(function () {
    var el = document.querySelector('[data-uwp-click-target]');
    if (!el) return false;
    el.removeAttribute('data-uwp-click-target');
    el.click();
    return true;
})()";

        // Describes the focused element and whether a menu or the player is open. A focused text
        // box is tagged so the typed text can be put back into the same element afterwards.
        private const string PageStateScript = @"(function () {
    var old = document.querySelectorAll('[data-uwp-text-target], [data-uwp-click-target]');
    for (var i = 0; i < old.length; i++) { old[i].removeAttribute('data-uwp-text-target'); old[i].removeAttribute('data-uwp-click-target'); }
    // On Xbox the highlight (XboxNavigationScript) decides; text boxes are highlighted without focus.
    var marked = document.querySelector('.uwp-focused');
    if (marked && (!marked.isConnected || !marked.getClientRects().length)) marked = null;
    var el = marked || document.activeElement, state = { editable: false, overlay: false, player: location.hash.indexOf('#/player') === 0 };
    if (el && !el.disabled && !el.readOnly && (el.tagName === 'TEXTAREA' ||
        (el.tagName === 'INPUT' && /^(text|email|password|search|url|tel|number)$/i.test(el.type || 'text')))) {
        state.editable = true;
        state.type = (el.type || 'text').toLowerCase();
        state.label = el.getAttribute('placeholder') || el.getAttribute('aria-label') || '';
        state.value = state.type === 'password' ? '' : String(el.value || '');
        el.setAttribute('data-uwp-text-target', '1');
    } else if (marked && marked !== document.activeElement) {
        // Highlighted but not focusable, so Enter wouldn't reach it: click it instead.
        state.needsClick = true;
        marked.setAttribute('data-uwp-click-target', '1');
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
        private const double StickThreshold = 0.6;

        private static readonly Key ArrowUp = new Key("ArrowUp", "ArrowUp", 38, null);
        private static readonly Key ArrowDown = new Key("ArrowDown", "ArrowDown", 40, null);
        private static readonly Key ArrowLeft = new Key("ArrowLeft", "ArrowLeft", 37, null);
        private static readonly Key ArrowRight = new Key("ArrowRight", "ArrowRight", 39, null);
        private static readonly Key Enter = new Key("Enter", "Enter", 13, "\r");
        private static readonly Key Escape = new Key("Escape", "Escape", 27, null);
        private static readonly Key Space = new Key(" ", "Space", 32, " ");
        private static readonly Key SubtitlesMenuKey = new Key("s", "KeyS", 83, "s");
        private static readonly Key AudioMenuKey = new Key("a", "KeyA", 65, "a");

        private const string FocusDiagnosticsScript = @"(function () {
    var el = document.activeElement;
    return 'page focused: ' + document.hasFocus() + ', active element: ' +
        (el ? el.tagName + (typeof el.className === 'string' && el.className ? '.' + el.className.split(' ')[0] : '') : 'none');
})()";
        private const int DiagnosticPresses = 5;
        private const int MaxChainedTextBoxes = 3;
        private static readonly TimeSpan ChainedTextBoxDelay = TimeSpan.FromMilliseconds(250);
        private static readonly TimeSpan SearchPageDelay = TimeSpan.FromMilliseconds(800);

        private readonly CoreWebView2 _webView;
        private readonly Func<TextEntryRequest, Task<TextEntryResult>> _requestText;
        private readonly Action _ensureFocus;
        private readonly DispatcherTimer _timer = new DispatcherTimer();
        private GamepadButtons _previousButtons;
        private Key _heldDirection;
        private DateTime _nextRepeat;
        private bool _busy;
        private int _loggedPresses;

        /// <param name="ensureFocus">Gives the web view keyboard focus if something else has it.</param>
        public ControllerBridge(CoreWebView2 webView, Func<TextEntryRequest, Task<TextEntryResult>> requestText, Action ensureFocus)
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

        /// <summary>
        /// Whether the left stick moves the highlight like the D-pad. Off while the console's cursor
        /// is on, because the stick then moves the cursor.
        /// </summary>
        public bool StickMovesHighlight { get; set; } = true;

        /// <summary>Raised on the UI thread when the View button is pressed.</summary>
        public event Action ViewPressed;

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
                OnSelect(!StickMovesHighlight);
            }
            else if ((pressed & GamepadButtons.B) != 0)
            {
                OnBack();
            }
            else if ((pressed & GamepadButtons.X) != 0)
            {
                OnPlayerKey(Space);
            }
            else if ((pressed & GamepadButtons.Y) != 0)
            {
                OnY();
            }
            else if ((pressed & GamepadButtons.Menu) != 0)
            {
                OnPlayerKey(AudioMenuKey);
            }
            else if ((pressed & GamepadButtons.View) != 0)
            {
                ViewPressed?.Invoke();
            }
        }

        // A: activate the focused element, or type into a focused text box. With the console's cursor
        // on, A already clicks under the cursor, so only the text box part applies.
        private async void OnSelect(bool cursorOn)
        {
            _busy = true;
            try
            {
                await SelectAsync(cursorOn);
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

        // Y: in the player the subtitles menu; elsewhere the search box, with the keyboard open.
        private async void OnY()
        {
            _busy = true;
            try
            {
                var state = await GetPageStateAsync();
                if (state != null && state.GetNamedBoolean("player", false))
                {
                    await PressAsync(SubtitlesMenuKey);
                    return;
                }
                var result = await _webView.ExecuteScriptAsync("window.__uwpGoToSearch ? window.__uwpGoToSearch() : ''");
                if (result.Contains("navigated"))
                {
                    await Task.Delay(SearchPageDelay);
                    result = await _webView.ExecuteScriptAsync("window.__uwpGoToSearch ? window.__uwpGoToSearch() : ''");
                }
                if (result.Contains("found"))
                {
                    await SelectAsync(false);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Controller search failed", ex);
            }
            finally
            {
                _busy = false;
            }
        }

        private async Task SelectAsync(bool cursorOn)
        {
            var state = await GetPageStateAsync();
            var boxes = 0;
            while (state != null && state.GetNamedBoolean("editable", false) && boxes < MaxChainedTextBoxes)
            {
                boxes++;
                var result = await _requestText(new TextEntryRequest(
                    state.GetNamedString("label", string.Empty),
                    state.GetNamedString("type", "text"),
                    state.GetNamedString("value", string.Empty)));
                if (result == null)
                {
                    return;
                }
                await _webView.ExecuteScriptAsync(SetTextScript.Replace("VALUE", JsonValue.CreateStringValue(result.Text).Stringify()));
                if (!result.Submitted)
                {
                    return;
                }
                // Finished with the keyboard's Enter: press Enter in the page too, like a real
                // keyboard. On the sign-in form that moves to the password box (then signs in),
                // so open the keyboard again if a text box now has focus.
                await PressAsync(Enter);
                await Task.Delay(ChainedTextBoxDelay);
                state = await GetPageStateAsync();
            }
            if (boxes == 0 && !cursorOn)
            {
                if (state != null && state.GetNamedBoolean("needsClick", false))
                {
                    await _webView.ExecuteScriptAsync(ClickTargetScript);
                }
                else
                {
                    await PressAsync(Enter);
                }
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

        // Player shortcuts (stremio-web's keyboard ones): X play / pause (Space), Y subtitles menu (S),
        // Menu audio menu (A).
        private async void OnPlayerKey(Key key)
        {
            _busy = true;
            try
            {
                var state = await GetPageStateAsync();
                if (state != null && state.GetNamedBoolean("player", false) && !state.GetNamedBoolean("editable", false))
                {
                    await PressAsync(key);
                }
            }
            catch (Exception ex)
            {
                Log.Error("Controller player shortcut failed", ex);
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

        private void ReadControllers(out GamepadButtons buttons, out Key direction)
        {
            buttons = GamepadButtons.None;
            direction = null;
            foreach (var gamepad in Gamepad.Gamepads)
            {
                var reading = gamepad.GetCurrentReading();
                buttons |= reading.Buttons;
                if (direction == null)
                {
                    direction = GetDirection(reading, StickMovesHighlight);
                }
            }
        }

        private static Key GetDirection(GamepadReading reading, bool useStick)
        {
            if ((reading.Buttons & GamepadButtons.DPadUp) != 0) return ArrowUp;
            if ((reading.Buttons & GamepadButtons.DPadDown) != 0) return ArrowDown;
            if ((reading.Buttons & GamepadButtons.DPadLeft) != 0) return ArrowLeft;
            if ((reading.Buttons & GamepadButtons.DPadRight) != 0) return ArrowRight;
            if (!useStick)
            {
                return null;
            }
            var x = reading.LeftThumbstickX;
            var y = reading.LeftThumbstickY;
            if (Math.Abs(x) < StickThreshold && Math.Abs(y) < StickThreshold)
            {
                return null;
            }
            if (Math.Abs(y) >= Math.Abs(x))
            {
                return y > 0 ? ArrowUp : ArrowDown;
            }
            return x > 0 ? ArrowRight : ArrowLeft;
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
