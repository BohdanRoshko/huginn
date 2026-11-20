using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Huginn;

/// <summary>
/// Huginn's meeting window: the meeting page Odin shows in its drawer (title, properties, Summary / Transcript /
/// Meeting chat), in a window of its own with a tab per open meeting. It opens as wide as Odin's drawer, at the
/// right edge of the screen the recorder panel is on, and can be maximized. A second meeting opens as a new tab in
/// the same window. The page is MeetingViewer.html, shown by WebView2; snapshots load from the recordings folder.
/// </summary>
internal sealed class MeetingWindow : Form
{
    private const int DrawerWidth = 780; // Odin's --drawer-width
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = System.Drawing.Color.FromArgb(37, 37, 37) };
    private readonly List<string> _pending = new();
    private bool _ready;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    public MeetingWindow(System.Drawing.Rectangle near)
    {
        Text = "Meetings";
        BackColor = System.Drawing.Color.FromArgb(25, 25, 25);
        StartPosition = FormStartPosition.Manual;
        float k = DeviceDpi / 96f;
        MinimumSize = new System.Drawing.Size((int)(420 * k), (int)(360 * k));
        // Where it was last left (size, place, maximized), else the drawer's width and at most 880 px tall, at the
        // top right of the recorder panel's screen: a tall or portrait screen made a full-height window too tall.
        if (!LoadBounds())
        {
            var wa = Screen.FromRectangle(near).WorkingArea;
            int w = Math.Min(wa.Width, (int)(DrawerWidth * k)), h = Math.Min(wa.Height - (int)(32 * k), (int)(880 * k));
            Bounds = new System.Drawing.Rectangle(wa.Right - w - (int)(16 * k), wa.Top + (int)(16 * k), w, h);
        }
        FormClosing += (_, _) => SaveBounds();
        // Odin's icon with a lowercase "m" (meetings.ico, Bohdan 2026-11-20).
        try
        {
            using var ico = Assembly.GetExecutingAssembly().GetManifestResourceStream("Huginn.meetings.ico");
            if (ico != null) Icon = new System.Drawing.Icon(ico);
        }
        catch { /* the default icon */ }
        Controls.Add(_web);
        _ = InitAsync();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int dark = 1, caption = 0x00191919; // a dark title bar in the page's colour (COLORREF 0x00BBGGRR)
        DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
        DwmSetWindowAttribute(Handle, 35, ref caption, sizeof(int));
    }

    private async Task InitAsync()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, Paths.File("webview"));
            await _web.EnsureCoreWebView2Async(env);
            var core = _web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            // SmartScreen held every page load about 2 s before the page ran. The page is our own file, and any other
            // address is cancelled below and opened in the browser, so there is nothing for it to check.
            core.Settings.IsReputationCheckingRequired = false;
            // The page and the snapshots each get a made-up https host mapped to a folder on this computer.
            var pageDir = Paths.File("viewer");
            Directory.CreateDirectory(pageDir);
            File.WriteAllText(Path.Combine(pageDir, "index.html"), LoadPage());
            core.SetVirtualHostNameToFolderMapping("app.huginn", pageDir, CoreWebView2HostResourceAccessKind.Allow);
            Directory.CreateDirectory(Paths.MeetingsDir());
            core.SetVirtualHostNameToFolderMapping(HuginnEngine.MeetingShotHost, Paths.MeetingsDir(), CoreWebView2HostResourceAccessKind.Allow);
            core.WebMessageReceived += (_, a) =>
            {
                var msg = a.TryGetWebMessageAsString();
                if (msg == "ready") { HuginnLog.Write($"meeting window: page ready in {_clock.ElapsedMilliseconds} ms"); _ready = true; foreach (var j in _pending) Send(j); _pending.Clear(); }
                else if (msg == "close") Close();
            };
            // Links inside a summary open in the browser, never in this window.
            core.NewWindowRequested += (_, a) => { a.Handled = true; Open(a.Uri); };
            core.NavigationStarting += (_, a) => { if (!a.Uri.StartsWith("https://app.huginn/", StringComparison.Ordinal)) { a.Cancel = true; Open(a.Uri); } };
            core.Navigate("https://app.huginn/index.html");
        }
        catch (Exception ex)
        {
            HuginnLog.Write("meeting window: WebView2 could not start", ex);
            MessageBox.Show(this, "The meeting window needs the Microsoft Edge WebView2 runtime.", "Huginn", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Close();
        }
    }

    /// <summary>Opens a meeting as a tab, or brings its tab forward when it is already open.</summary>
    public void ShowMeeting(MeetingView view)
    {
        var json = view.ToJson();
        if (_ready) Send(json); else _pending.Add(json);
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        if (!Visible) Show();
        Activate();
    }

    private static string BoundsFile => Paths.File("meeting_window.txt");

    private void SaveBounds()
    {
        var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        try { File.WriteAllText(BoundsFile, $"{b.X},{b.Y},{b.Width},{b.Height},{(WindowState == FormWindowState.Maximized ? 1 : 0)}"); }
        catch { /* the next window opens at the default place */ }
    }

    private bool LoadBounds()
    {
        try
        {
            var p = File.ReadAllText(BoundsFile).Split(',').Select(int.Parse).ToArray();
            var b = new System.Drawing.Rectangle(p[0], p[1], p[2], p[3]);
            if (!Screen.AllScreens.Any(sc => sc.WorkingArea.IntersectsWith(new System.Drawing.Rectangle(b.X, b.Y, b.Width, 40)))) return false;
            Bounds = b;
            if (p.Length > 4 && p[4] == 1) WindowState = FormWindowState.Maximized;
            return true;
        }
        catch { return false; }
    }

    private void Send(string json) => _ = _web.CoreWebView2.ExecuteScriptAsync($"window.huginnOpen({json})");

    private static void Open(string uri)
    {
        if (!uri.StartsWith("http", StringComparison.OrdinalIgnoreCase) || uri.Contains(".huginn/")) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose(); }
        catch { /* no browser: nothing to do */ }
    }

    private static string LoadPage()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Huginn.MeetingViewer.html")
            ?? throw new InvalidOperationException("MeetingViewer.html is not embedded");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
