using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace WebTop;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var once = new Mutex(true, @"Local\webtop-single-instance", out bool isPrimary);
        if (!isPrimary) return;

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new Shell());
    }
}

/// <summary>
/// Окно-оболочка: снизу — страница, сверху — интерфейс поиска.
/// Адресной строки нет: интерфейс разворачивается на всё окно, а потом сворачивается в плавающую панель.
/// </summary>
internal sealed class Shell : Form
{
    private readonly WebView2 _page = new();
    private readonly WebView2 _shell = new();
    private readonly Dictionary<string, Hit> _history = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Threading.Timer _flush;
    private readonly Shortcuts _shortcuts;
    private readonly string _root;

    private bool _ready;
    private bool _dirty;
    private string _mode = "search";

    private sealed class Hit
    {
        public int N { get; set; }
        public long T { get; set; }
    }

    public Shell()
    {
        _root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WebTop");
        _flush = new System.Threading.Timer(_ => SaveHistory(), null!, 3000, 3000);

        Text = "webtop";
        BackColor = Color.FromArgb(10, 11, 13);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(560, 420);
        ClientSize = new Size(1180, 760);
        DoubleBuffered = true;

        _page.DefaultBackgroundColor = Color.FromArgb(10, 11, 13);
        _shell.DefaultBackgroundColor = Color.Transparent;
        Controls.Add(_page);
        Controls.Add(_shell);

        LoadHistory();
        _shortcuts = new Shortcuts(this);
        Application.AddMessageFilter(_shortcuts);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _ = InitAsync();
    }

    /* ---------------- startup ---------------- */

    private async Task InitAsync()
    {
        try
        {
            Directory.CreateDirectory(_root);
            string www = Path.Combine(AppContext.BaseDirectory, "www");
            if (!File.Exists(Path.Combine(www, "start.html")))
                throw new DirectoryNotFoundException("нет www/start.html рядом с " + AppContext.BaseDirectory);

            var opts = new CoreWebView2EnvironmentOptions(
                "--no-default-browser-check --disable-features=Translate,msSmartScreenProtection,msEdgeFI");
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(_root, "cache"), opts);

            await _page.EnsureCoreWebView2Async(env);
            await _shell.EnsureCoreWebView2Async(env);

            foreach (var w in new[] { _page, _shell })
            {
                var core = w.CoreWebView2 ?? throw new InvalidOperationException("WebView2 не инициализирован");
                core.SetVirtualHostNameToFolderMapping("appassets", www, CoreWebView2HostResourceAccessKind.Deny);
                core.Settings.AreDefaultContextMenusEnabled = DevTools;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.AreDevToolsEnabled = DevTools;
            }

            _shell.CoreWebView2!.WebMessageReceived += OnMessage;
            _shell.CoreWebView2.NavigationCompleted += (_, a) =>
            {
                if (!a.IsSuccess) Log("start.html: " + a.WebErrorStatus);
            };

            var core17 = _page.CoreWebView2!;
            core17.NavigationCompleted += (_, _) => OnPageMoved();
            core17.SourceChanged += (_, _) => OnPageMoved();
            core17.DocumentTitleChanged += (_, _) => SendNav();
            core17.NewWindowRequested += OnNewWindow;

            _ready = true;
            LayoutChildren();
            string start = new Uri(Path.Combine(www, "start.html")).AbsoluteUri;
            if (Environment.GetEnvironmentVariable("WEBTOP_SELFTEST") == "1") start += "#selftest";
            _shell.CoreWebView2.Navigate(start);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "webtop не запустился:\n\n" + ex.Message +
                "\n\nНужен WebView2 Runtime — он ставится вместе с Edge.", "webtop",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Close();
        }
    }

    private static bool DevTools => Environment.GetEnvironmentVariable("WEBTOP_DEV") == "1";

    private void Log(string line)
    {
        try { File.AppendAllText(Path.Combine(_root, "log.txt"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}\n"); }
        catch { }
    }

    /* ---------------- messages from the interface ---------------- */

    private async void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string type = "";
        string? url = null;
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            if (doc.RootElement.TryGetProperty("type", out var t)) type = t.GetString() ?? "";
            if (doc.RootElement.TryGetProperty("url", out var u)) url = u.GetString();
        }
        catch { return; }

        var core = _page.CoreWebView2;
        switch (type)
        {
            case "ready": await InjectAsync(); break;
            case "go":
                if (!IsHttp(url)) break;
                if (core is null) OpenExternal(url!);
                else core.Navigate(url!);
                SetMode("site");
                break;
            case "home": case "search": SetMode("search"); break;
            case "back": GoBack(); break;
            case "fwd": GoForward(); break;
            case "reload": core?.Reload(); break;
            case "stop": _ = core?.ExecuteScriptAsync(PauseScript); break;
            case "drag": BeginSystemDrag(); break;
            case "min": WindowState = FormWindowState.Minimized; break;
            case "max": WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized; break;
            case "close": Close(); break;
            case "external": if (IsHttp(url)) OpenExternal(url!); break;
            case "log": Log(JsonDocument.Parse(e.WebMessageAsJson).RootElement.GetProperty("text").GetString() ?? ""); break;
        }
    }

    private const string PauseScript =
        "(()=>{let n=0;document.querySelectorAll('video,audio').forEach(m=>{if(!m.paused){m.pause();n++}});" +
        "document.querySelectorAll('iframe').forEach(f=>{try{f.contentWindow&&f.contentWindow.postMessage('{\\\"__webtop\\\":\\\"pause\\\"}','*')}catch(e){}});" +
        "return n})()";

    private void OnNewWindow(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (IsHttp(e.Uri)) _page.CoreWebView2?.Navigate(e.Uri);
    }

    /* ---------------- navigation state ---------------- */

    private void OnPageMoved()
    {
        string? src = _page.CoreWebView2?.Source;
        if (IsHttp(src))
        {
            if (!_history.TryGetValue(src!, out var hit)) _history[src!] = hit = new Hit();
            hit.N++;
            hit.T = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _dirty = true;
        }
        SendNav();
    }

    private async void SendNav()
    {
        var shellCore = _shell.CoreWebView2;
        if (!_ready || shellCore is null) return;
        var page = _page.CoreWebView2;
        string json = JsonSerializer.Serialize(new
        {
            url = SafeUrl(page?.Source),
            title = Clean(page?.DocumentTitle),
            back = page?.CanGoBack ?? false,
            fwd = page?.CanGoForward ?? false,
        });
        try { await shellCore.ExecuteScriptAsync("window.__nav&&window.__nav(" + json + ")"); } catch { }
    }

    private async Task InjectAsync()
    {
        var shellCore = _shell.CoreWebView2;
        if (!_ready || shellCore is null) return;

        var byHost = new Dictionary<string, Hit>(StringComparer.OrdinalIgnoreCase);
        foreach (var (url, hit) in _history)
        {
            string host = HostOf(url);
            if (host.Length == 0) continue;
            if (!byHost.TryGetValue(host, out var agg)) byHost[host] = agg = new Hit();
            agg.N += hit.N;
            agg.T = Math.Max(agg.T, hit.T);
        }
        var snapshot = byHost.OrderByDescending(p => p.Value.T).Take(200)
            .Select(p => new { host = p.Key, n = p.Value.N, t = p.Value.T });
        var page = _page.CoreWebView2;
        string json = JsonSerializer.Serialize(new
        {
            history = snapshot,
            mode = _mode,
            url = SafeUrl(page?.Source),
            title = Clean(page?.DocumentTitle),
            back = page?.CanGoBack ?? false,
            fwd = page?.CanGoForward ?? false,
        });
        try { await shellCore.ExecuteScriptAsync("window.__ready&&window.__ready(" + json + ")"); } catch { }
    }

    private void SetMode(string mode)
    {
        bool changed = _mode != mode;
        _mode = mode;
        if (changed)
        {
            LayoutChildren();
            _ = _shell.CoreWebView2?.ExecuteScriptAsync("window.__mode&&window.__mode(\"" + mode + "\")");
        }
        if (mode == "search") _shell.Focus();
        else _page.Focus();
    }

    internal void ShowSearch() => SetMode("search");
    internal void GoBack() { if (_page.CoreWebView2?.CanGoBack == true) _page.CoreWebView2.GoBack(); else SetMode("search"); }
    internal void GoForward() => _page.CoreWebView2?.GoForward();
    internal void ReloadPage() => _page.CoreWebView2?.Reload();
    internal bool InSiteMode => _mode == "site";

    /* ---------------- layout ---------------- */

    private void LayoutChildren()
    {
        var full = new Rectangle(Point.Empty, ClientSize);
        _page.Bounds = full;
        _shell.Bounds = _mode == "search" ? full : PillRect();
        _shell.BringToFront();
    }

    private Rectangle PillRect()
    {
        int w = Math.Min(Scale(600), ClientSize.Width - Scale(16));
        int x = Math.Max(Scale(8), (ClientSize.Width - w) / 2);
        return new Rectangle(x, 0, w, Scale(72));
    }

    private int Scale(int css) => (int)Math.Round(css * DeviceDpi / 96f);

    protected override void OnClientSizeChanged(EventArgs e)
    {
        base.OnClientSizeChanged(e);
        LayoutChildren();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _flush.Dispose();
        SaveHistory();
        Application.RemoveMessageFilter(_shortcuts);
        base.OnFormClosed(e);
    }

    private void BeginSystemDrag()
    {
        ReleaseCapture();
        _ = SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
    }

    private void OpenExternal(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }

    /* ---------------- history file ---------------- */

    private string HistoryPath => Path.Combine(_root, "history.json");

    private void LoadHistory()
    {
        try
        {
            if (!File.Exists(HistoryPath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(HistoryPath));
            foreach (var row in doc.RootElement.EnumerateArray())
            {
                if (row.TryGetProperty("u", out var uu) is false) continue;
                string? u = uu.GetString();
                if (!IsHttp(u)) continue;
                _history[u!] = new Hit
                {
                    N = row.TryGetProperty("n", out var n) ? n.GetInt32() : 1,
                    T = row.TryGetProperty("t", out var t) ? t.GetInt64() : 0,
                };
            }
        }
        catch { _history.Clear(); }
    }

    private void SaveHistory()
    {
        if (!_dirty) return;
        _dirty = false;
        try
        {
            var trimmed = _history.OrderByDescending(p => p.Value.T).Take(400)
                .Select(p => new { u = p.Key, n = p.Value.N, t = p.Value.T });
            File.WriteAllText(HistoryPath, JsonSerializer.Serialize(trimmed));
        }
        catch { }
    }

    /* ---------------- window without a frame, but with snap and shadow ---------------- */

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style |= WS_CAPTION | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_THICKFRAME | WS_SYSMENU;
            return cp;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_NCCALCSIZE && m.WParam != IntPtr.Zero)
        {
            var info = Marshal.PtrToStructure<NCCALCULATE>(m.LParam);
            if (WindowState == FormWindowState.Maximized)
            {
                int pad = (int)Math.Round((GetSystemMetrics(SM_CXSIZEFRAME) + GetSystemMetrics(SM_CXPADDEDBORDER)) * DeviceDpi / 96f);
                info.Rc1.Top += pad; info.Rc1.Left += pad; info.Rc1.Right -= pad; info.Rc1.Bottom -= pad;
            }
            Marshal.StructureToPtr(info, m.LParam, true);
            m.Result = IntPtr.Zero;
            return;
        }
        if (m.Msg == WM_GETMINMAXINFO)
        {
            var mmi = Marshal.PtrToStructure<MINMAXINFO>(m.LParam);
            var mi = new MONITORINFO { CbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(MonitorFromWindow(Handle, MONITOR_DEFAULTTONEAREST), ref mi))
            {
                mmi.MaxPosition = new Point(mi.Work.Left - mi.Rect.Left, mi.Work.Top - mi.Rect.Top);
                mmi.MaxSize = new Point(mi.Work.Width, mi.Work.Height);
            }
            mmi.MinTrackSize = new Point(Scale(560), Scale(420));
            Marshal.StructureToPtr(mmi, m.LParam, true);
            m.Result = IntPtr.Zero;
            return;
        }
        if (m.Msg == WM_NCHITTEST && WindowState == FormWindowState.Normal)
        {
            base.WndProc(ref m);
            int raw = unchecked((int)(long)m.LParam);
            var pt = PointToClient(new Point(raw & 0xFFFF, unchecked((int)((raw >> 16) & 0xFFFF))));
            int e = Scale(7);
            bool left = pt.X < e, right = pt.X > ClientSize.Width - e;
            bool top = pt.Y < e, bottom = pt.Y > ClientSize.Height - e;
            m.Result = (left && top) ? HTTOPLEFT : (right && top) ? HTTOPRIGHT
                : (left && bottom) ? HTBOTTOMLEFT : (right && bottom) ? HTBOTTOMRIGHT
                : left ? HTLEFT : right ? HTRIGHT : top ? HTTOP : bottom ? HTBOTTOM : m.Result;
            return;
        }
        base.WndProc(ref m);
    }

    /* ---------------- shortcuts, even while a site has focus ---------------- */

    private sealed class Shortcuts(Shell owner) : IMessageFilter
    {
        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg != WM_KEYDOWN || !owner.ContainsFocus) return false;
            var key = (Keys)(int)m.WParam;
            bool ctrl = (Control.ModifierKeys & Keys.Control) != 0;
            bool alt = (Control.ModifierKeys & Keys.Alt) != 0;
            if (ctrl && key == Keys.L) { owner.ShowSearch(); return true; }
            if (key == Keys.Escape && owner.InSiteMode) { owner.ShowSearch(); return true; }
            if (alt && key == Keys.Left) { owner.GoBack(); return true; }
            if (alt && key == Keys.Right) { owner.GoForward(); return true; }
            if (key == Keys.F5) { owner.ReloadPage(); return true; }
            return false;
        }
    }

    /* ---------------- helpers + win32 ---------------- */

    private static bool IsHttp(string? s) =>
        s is not null && (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                          s.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

    private static string? Clean(string? s)
    {
        if (s is null) return null;
        s = s.Trim();
        return s.Length == 0 ? null : s.Length > 120 ? s[..120] : s;
    }

    private static string? SafeUrl(string? s)
    {
        if (s is null) return null;
        foreach (char c in s) if (c is < ' ' or > '~') return null;
        return Uri.IsWellFormedUriString(s, UriKind.Absolute) ? s : null;
    }

    private static readonly string[] TwoLevel =
        ["co.uk", "org.uk", "com.br", "com.au", "co.jp", "co.kr", "co.in", "com.mx", "com.tr", "co.nz", "com.ua"];

    /// <summary>youtube.com из www.youtube.com, bbc.co.uk из www.bbc.co.uk.</summary>
    internal static string HostOf(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return "";
        var parts = u.Host.ToLowerInvariant().Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "";
        if (parts.Length <= 2) return string.Join(".", parts);
        string tail = parts[^2] + "." + parts[^1];
        return Array.IndexOf(TwoLevel, tail) >= 0 ? string.Join(".", parts[^3..]) : tail;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; public readonly int Width => Right - Left; public readonly int Height => Bottom - Top; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NCCALCULATE { public RECT Rc1, Rc2, Rc3; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO { public Point Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int CbSize; public RECT Rect; public RECT Work; public uint Flags; }

    private const int
        WM_KEYDOWN = 0x0100, WM_NCCALCSIZE = 0x0083, WM_GETMINMAXINFO = 0x0024, WM_NCHITTEST = 0x0084,
        WM_NCLBUTTONDOWN = 0x00A1, WS_CAPTION = 0x00C00000, WS_SYSMENU = 0x00080000,
        WS_THICKFRAME = 0x00040000, WS_MINIMIZEBOX = 0x00020000, WS_MAXIMIZEBOX = 0x00010000,
        HTTOPLEFT = 13, HTTOPRIGHT = 14, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17,
        HTCAPTION = 2, HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTBOTTOM = 15,
        SM_CXSIZEFRAME = 32, SM_CXPADDEDBORDER = 92, MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr h, ref MONITORINFO info);
}
