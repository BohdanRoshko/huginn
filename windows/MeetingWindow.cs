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

    public MeetingWindow(System.Drawing.Rectangle near)
    {
        Text = "Meetings";
        BackColor = System.Drawing.Color.FromArgb(25, 25, 25);
        StartPosition = FormStartPosition.Manual;
        var wa = Screen.FromRectangle(near).WorkingArea;
        float k = DeviceDpi / 96f;
        int w = Math.Min(wa.Width, (int)(DrawerWidth * k)), h = wa.Height - (int)(32 * k);
        Bounds = new System.Drawing.Rectangle(wa.Right - w - (int)(16 * k), wa.Top + (int)(16 * k), w, h);
        MinimumSize = new System.Drawing.Size((int)(420 * k), (int)(360 * k));
        try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { /* the default icon */ }
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
                if (msg == "ready") { _ready = true; foreach (var j in _pending) Send(j); _pending.Clear(); }
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
