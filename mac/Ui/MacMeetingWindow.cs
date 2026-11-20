using System.Reflection;
using AppKit;
using CoreGraphics;
using Foundation;
using WebKit;

namespace Huginn;

/// <summary>
/// Huginn's meeting window on the Mac, the port of the Windows MeetingWindow: the same page, MeetingViewer.html (title,
/// properties, Summary / Transcript / Meeting chat, a tab per open meeting), in a WKWebView instead of WebView2. It opens
/// as wide as Odin's drawer at the top right of the main screen, and remembers where it was left. A second meeting opens
/// as a new tab. The page and the snapshots are files under Huginn's data folder; links open in the browser.
/// </summary>
internal sealed class MacMeetingWindow
{
    private const double DrawerWidth = 780; // Odin's --drawer-width
    private readonly NSWindow _window;
    private readonly WKWebView _web;
    private readonly WKUserContentController _content;
    private readonly List<string> _pending = new();
    private readonly Bridge _bridge;
    private readonly Navigation _navigation = new();
    private readonly Popups _popups = new();
    private bool _ready;

    /// <summary>The window closed; the next meeting makes a new one.</summary>
    public event Action? Closed;

    public MacMeetingWindow()
    {
        var vf = (MacUi.MainScreen() ?? NSScreen.Screens[0]).VisibleFrame;
        double sx = vf.X, sy = vf.Y, sw = vf.Width, sh = vf.Height;
        // At most 880 points tall: a tall or portrait screen made a full-height window too tall (as on Windows).
        double w = Math.Min(sw, DrawerWidth), h = Math.Min(sh - 32, 880);
        _window = new NSWindow(MacUi.R(sx + sw - w - 16, sy + sh - h - 16, w, h),
            NSWindowStyle.Titled | NSWindowStyle.Closable | NSWindowStyle.Resizable | NSWindowStyle.Miniaturizable,
            NSBackingStore.Buffered, false)
        {
            Title = "Meetings",
            ReleasedWhenClosed = false,
            MinSize = new CGSize(420, 360),
            // A dark title bar in the page's colour, as on Windows.
            Appearance = NSAppearance.GetAppearance(NSAppearance.NameDarkAqua),
            TitlebarAppearsTransparent = true,
            BackgroundColor = MacUi.Rgb(25, 25, 25),
        };
        _window.SetFrameAutosaveName("HuginnMeetingWindow"); // where it was last left, else the place above

        _bridge = new Bridge(OnMessage);
        var config = new WKWebViewConfiguration();
        _content = config.UserContentController;
        _content.AddScriptMessageHandler(_bridge, "huginn");
        // The page talks to WebView2 (window.chrome.webview); here the same calls reach this window.
        _content.AddUserScript(new WKUserScript(
            new NSString("window.chrome = { webview: { postMessage: function (m) { window.webkit.messageHandlers.huginn.postMessage(m); } } };"),
            WKUserScriptInjectionTime.AtDocumentStart, true));
        _web = new WKWebView(_window.ContentView!.Bounds, config)
        {
            AutoresizingMask = NSViewResizingMask.WidthSizable | NSViewResizingMask.HeightSizable,
            NavigationDelegate = _navigation,
            UIDelegate = _popups,
        };
        _web.SetValueForKey(NSNumber.FromBoolean(false), new NSString("drawsBackground")); // no white flash before the page
        _window.ContentView!.AddSubview(_web);
        _window.WillClose += (_, _) =>
        {
            _content.RemoveScriptMessageHandler("huginn");
            Closed?.Invoke();
        };

        // The page is written next to the recordings, and may read the whole data folder (the snapshots).
        var pageDir = Paths.File("viewer");
        Directory.CreateDirectory(pageDir);
        Directory.CreateDirectory(Paths.MeetingsDir());
        var page = Path.Combine(pageDir, "index.html");
        File.WriteAllText(page, LoadPage());
        _web.LoadFileUrl(NSUrl.FromFilename(page), NSUrl.FromFilename(Paths.BaseDir()));
    }

    /// <summary>Opens a meeting as a tab, or brings its tab forward when it is already open.</summary>
    public void ShowMeeting(MeetingView view)
    {
        // Snapshots load from the recordings folder as file URLs (Windows maps a made-up host to it instead).
        var json = (view with { ShotPrefix = new Uri(Paths.MeetingsDir().TrimEnd('/') + "/").AbsoluteUri + Uri.EscapeDataString(view.Id) + "_" }).ToJson();
        if (_ready) Send(json); else _pending.Add(json);
        if (_window.IsMiniaturized) _window.Deminiaturize(null);
        NSApplication.SharedApplication.ActivateIgnoringOtherApps(true); // a menu bar app must step forward to show a window
        _window.MakeKeyAndOrderFront(null);
    }

    private void OnMessage(string msg)
    {
        if (msg == "ready")
        {
            _ready = true;
            foreach (var j in _pending) Send(j);
            _pending.Clear();
        }
        else if (msg == "close") _window.Close();
    }

    private void Send(string json) => _web.EvaluateJavaScript($"window.huginnOpen({json})", (_, _) => { });

    private static string LoadPage()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Huginn.MeetingViewer.html")
            ?? throw new InvalidOperationException("MeetingViewer.html is not embedded");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    private static void OpenInBrowser(NSUrl? url)
    {
        if (url?.Scheme is "http" or "https") NSWorkspace.SharedWorkspace.OpenUrl(url);
    }

    /// <summary>The page's postMessage calls.</summary>
    [Register("HuginnMeetingBridge")]
    private sealed class Bridge : NSObject, IWKScriptMessageHandler
    {
        private readonly Action<string> _on;
        public Bridge(Action<string> on) => _on = on;

        [Export("userContentController:didReceiveScriptMessage:")]
        public void DidReceiveScriptMessage(WKUserContentController userContentController, WKScriptMessage message) =>
            MacUi.OnMain(() => _on(message.Body?.ToString() ?? ""));
    }

    /// <summary>Only the page's own files load here; a link opens in the browser.</summary>
    [Register("HuginnMeetingNavigation")]
    private sealed class Navigation : WKNavigationDelegate
    {
        public override void DecidePolicy(WKWebView webView, WKNavigationAction navigationAction, Action<WKNavigationActionPolicy> decisionHandler)
        {
            var url = navigationAction.Request.Url;
            if (url == null || url.IsFileUrl || url.Scheme == "about") { decisionHandler(WKNavigationActionPolicy.Allow); return; }
            decisionHandler(WKNavigationActionPolicy.Cancel);
            OpenInBrowser(url);
        }
    }

    /// <summary>A link that asks for a new window opens in the browser, never in this window.</summary>
    [Register("HuginnMeetingPopups")]
    private sealed class Popups : WKUIDelegate
    {
        public override WKWebView? CreateWebView(WKWebView webView, WKWebViewConfiguration configuration, WKNavigationAction navigationAction, WKWindowFeatures windowFeatures)
        {
            OpenInBrowser(navigationAction.Request.Url);
            return null;
        }
    }
}
