using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace WebTop;

internal static class Program
{
    internal const string WindowTitle = "webtop";
    internal const int WM_APP_SUMMON = 0x8001;

    [STAThread]
    private static void Main()
    {
        using var once = new Mutex(true, @"Local\webtop-single-instance", out bool isPrimary);
        if (!isPrimary)
        {
            // повторный запуск exe = тот же вызов, что и горячая клавиша
            IntPtr h = FindWindow(null, WindowTitle);
            if (h != IntPtr.Zero) PostMessage(h, WM_APP_SUMMON, IntPtr.Zero, IntPtr.Zero);
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new Shell());
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? cls, string? window);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
}

/// <summary>
/// Фоновая утилита: окна нет, пока не нажата глобальная клавиша.
/// Тогда появляется стеклянная карточка, а найденный сайт открывается в браузере пользователя.
/// </summary>
internal sealed class Shell : Form
{
    private readonly WebView2 _ui = new();
    private readonly Dictionary<string, Hit> _history = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Threading.Timer _flush;
    private readonly string _root;

    private NotifyIcon? _tray;
    private ToolStripMenuItem? _autostart;
    private IntPtr _iconHandle = IntPtr.Zero;
    private bool _ready;
    private bool _dirty;
    private bool _allowVisible;
    private bool _hotkeyTaken;
    private bool _exiting;
    private string _label = "Ctrl + Space";

    private const int HotkeyId = 1;
    private static readonly Size CardCss = new(720, 300);
    private const double CardAlpha = 0.9;

    private sealed class Hit
    {
        public int N { get; set; }
        public long T { get; set; }
    }

    public Shell()
    {
        _root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WebTop");
        _flush = new System.Threading.Timer(_ => SaveHistory(), null!, 3000, 3000);

        Text = Program.WindowTitle;
        BackColor = Color.FromArgb(10, 11, 13);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        MinimizeBox = false;
        MaximizeBox = false;
        Opacity = CardAlpha;
        ClientSize = new Size(Scale(CardCss.Width), Scale(CardCss.Height));

        _ui.DefaultBackgroundColor = Color.FromArgb(10, 11, 13);
        Controls.Add(_ui);

        LoadHistory();
        _ = Handle; // стартуем скрытыми, но хэндл, горячая клавиша и WebView2 нужны сразу
    }

    /* ---------------- фоновый режим ---------------- */

    protected override void SetVisibleCore(bool value)
    {
        base.SetVisibleCore(_allowVisible && value);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RegisterHotkey();
        BuildTray();
        _ = InitAsync();
    }

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
            await _ui.EnsureCoreWebView2Async(env);

            var core = _ui.CoreWebView2 ?? throw new InvalidOperationException("WebView2 не инициализирован");
            core.Settings.AreDefaultContextMenusEnabled = DevTools;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDevToolsEnabled = DevTools;
            core.WebMessageReceived += OnMessage;
            core.NavigationCompleted += (_, a) =>
            {
                if (!a.IsSuccess) Log("start.html: " + a.WebErrorStatus);
            };

            _ready = true;
            string start = new Uri(Path.Combine(www, "start.html")).AbsoluteUri;
            string? selftest = Environment.GetEnvironmentVariable("WEBTOP_SELFTEST");
            if (selftest == "1") start += "#selftest";
            else if (selftest == "2") start += "#selftest-go";
            core.Navigate(start);

            FirstRunHint();
        }
        catch (Exception ex)
        {
            if (_allowVisible)
            {
                MessageBox.Show(this, "webtop не запустился:\n\n" + ex.Message +
                    "\n\nНужен WebView2 Runtime — он ставится вместе с Edge.", "webtop",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                Close();
            }
            else Log("старт не удался: " + ex.Message);
        }
    }

    private void FirstRunHint()
    {
        string marker = Path.Combine(_root, "ran.txt");
        if (File.Exists(marker))
        {
            Log("запуск в фоне, окно скрыто до " + _label);
            return;
        }
        try { File.WriteAllText(marker, DateTime.Now.ToString("O")); } catch { }
        ShowOverlay();
        _tray?.ShowBalloonTip(5000, "webtop теперь в фоне",
            $"Ищите по {_label}. Найденное открывается в вашем браузере. Выйти — через меню значка в трее.",
            ToolTipIcon.Info);
    }

    private static bool DevTools => Environment.GetEnvironmentVariable("WEBTOP_DEV") == "1";

    private void Log(string line)
    {
        try { File.AppendAllText(Path.Combine(_root, "log.txt"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}\n"); }
        catch { }
    }

    /* ---------------- значок в трее ---------------- */

    private void BuildTray()
    {
        _iconHandle = MakeIcon();
        _autostart = new ToolStripMenuItem("Запускать с Windows", null, (_, _) =>
        {
            _autostart!.Checked = TrySetAutostart(!_autostart.Checked);
        })
        { Checked = AutostartOn() };

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem($"Найти сайт  ·  {_label}", null, (_, _) => ShowOverlay())
            { Font = new Font("Segoe UI", 9f, FontStyle.Bold) });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_autostart);
        menu.Items.Add(new ToolStripMenuItem("Показать подсказку", null, (_, _) =>
            _tray?.ShowBalloonTip(4000, "webtop", $"Вызвать поиск — {_label}.", ToolTipIcon.Info)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Выход", null, (_, _) => ExitApp()));

        _tray = new NotifyIcon
        {
            Icon = Icon.FromHandle(_iconHandle),
            Text = $"webtop — поиск по {_label}",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => ShowOverlay();
    }

    private void ExitApp()
    {
        _exiting = true;
        Close();
    }

    /// <summary>Значок рисуем кодом: тёмная плитка со светлой плашкой поиска.</summary>
    private static IntPtr MakeIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            var tile = new Rectangle(1, 1, 30, 30);
            using (var back = Rounded(tile, 7))
            {
                using var fill = new LinearGradientBrush(tile, Color.FromArgb(40, 46, 58), Color.FromArgb(15, 17, 22), 60f);
                g.FillPath(fill, back);
                using var edge = new Pen(Color.FromArgb(90, 255, 255, 255));
                g.DrawPath(edge, back);
            }
            using var bar = new SolidBrush(Color.FromArgb(237, 240, 245));
            using var pill = Rounded(new Rectangle(7, 13, 18, 7), 3);
            g.FillPath(bar, pill);
        }
        return bmp.GetHicon();
    }

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;
        if (d <= 0 || d > Math.Min(r.Width, r.Height))
        {
            path.AddRectangle(r);
            return path;
        }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /* ---------------- автозапуск ---------------- */

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private static bool AutostartOn()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(Program.WindowTitle) is string s && s.Contains("webtop", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static bool TrySetAutostart(bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (on) k.SetValue(Program.WindowTitle, "\"" + Environment.ProcessPath + "\"");
            else k.DeleteValue(Program.WindowTitle, false);
            return true;
        }
        catch { return AutostartOn(); }
    }

    /* ---------------- глобальная клавиша ---------------- */

    private void RegisterHotkey()
    {
        foreach (var (mod, vk, label) in new (uint, uint, string)[]
                 {
                     (MOD_CONTROL, VK_SPACE, "Ctrl + Space"),
                     (MOD_MENU, VK_SPACE, "Alt + Space"),
                     (MOD_CONTROL | MOD_MENU, VK_K, "Ctrl + Alt + K"),
                 })
        {
            if (RegisterHotKey(Handle, HotkeyId, mod | MOD_NOREPEAT, vk))
            {
                _label = label;
                _hotkeyTaken = true;
                Log("горячая клавиша: " + label);
                return;
            }
        }
        Log("все горячие клавиши заняты — вызов через значок в трее или повторный запуск webtop.exe");
    }

    private void ToggleOverlay()
    {
        if (Visible && ContainsFocus) HideOverlay();
        else ShowOverlay();
    }

    private void ShowOverlay()
    {
        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        int w = Math.Min(Scale(CardCss.Width), wa.Width - Scale(32));
        int h = Scale(CardCss.Height);
        Location = new Point(wa.Left + (wa.Width - w) / 2, wa.Top + Math.Max(Scale(40), (int)(wa.Height * 0.16)));
        ClientSize = new Size(w, h);
        _allowVisible = true;
        Show();
        Activate();
        BringToFront();
        _ = _ui.CoreWebView2?.ExecuteScriptAsync("window.__show&&window.__show()");
        Log("окно показано");
    }

    private void HideOverlay()
    {
        Hide();
        SaveHistory();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (Visible && !_exiting) HideOverlay();
    }

    protected override void OnClientSizeChanged(EventArgs e)
    {
        base.OnClientSizeChanged(e);
        _ui.Bounds = new Rectangle(Point.Empty, ClientSize);
        ApplyRegion();
    }

    private void ApplyRegion()
    {
        using var path = Rounded(new Rectangle(0, 0, ClientSize.Width, ClientSize.Height), Scale(20));
        Region = new Region(path);
    }

    /* ---------------- сообщения интерфейса ---------------- */

    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string type = "";
        string? url = null;
        string? text = null;
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("type", out var t)) type = t.GetString() ?? "";
            if (root.TryGetProperty("url", out var u)) url = u.GetString();
            if (root.TryGetProperty("text", out var tx)) text = tx.GetString();
        }
        catch { return; }

        switch (type)
        {
            case "ready": _ = InjectAsync(); break;
            case "go":
                if (!IsHttp(url)) break;
                RecordHit(url!);
                OpenExternal(url!);
                HideOverlay();
                break;
            case "hide": HideOverlay(); break;
            case "quit": ExitApp(); break;
            case "autostart":
                if (_autostart is not null) _autostart.Checked = TrySetAutostart(!_autostart.Checked);
                break;
            case "log": Log(text ?? ""); break;
        }
    }

    private void RecordHit(string url)
    {
        string host = HostOf(url);
        if (host.Length == 0) return;
        string key = "https://" + host;
        if (!_history.TryGetValue(key, out var hit)) _history[key] = hit = new Hit();
        hit.N++;
        hit.T = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _dirty = true;
    }

    private void OpenExternal(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            Log("открыто в браузере: " + url);
        }
        catch (Exception ex)
        {
            Log("браузер не открылся: " + ex.Message);
            _tray?.ShowBalloonTip(4000, "webtop", "Не удалось открыть браузер: " + ex.Message, ToolTipIcon.Warning);
        }
    }

    private async Task InjectAsync()
    {
        var core = _ui.CoreWebView2;
        if (!_ready || core is null) return;

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
        string json = JsonSerializer.Serialize(new { history = snapshot, hotkey = _label });
        try { await core.ExecuteScriptAsync("window.__ready&&window.__ready(" + json + ")"); } catch { }
    }

    /* ---------------- история ---------------- */

    private string HistoryPath => Path.Combine(_root, "history.json");

    private void LoadHistory()
    {
        try
        {
            if (!File.Exists(HistoryPath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(HistoryPath));
            foreach (var row in doc.RootElement.EnumerateArray())
            {
                if (!row.TryGetProperty("u", out var uu)) continue;
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

    /* ---------------- сообщения окна ---------------- */

    protected override void WndProc(ref Message m)
    {
        if ((m.Msg == WM_HOTKEY && m.WParam == (IntPtr)HotkeyId) || m.Msg == Program.WM_APP_SUMMON)
        {
            ToggleOverlay();
            m.Result = IntPtr.Zero;
            return;
        }
        base.WndProc(ref m);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        if (_hotkeyTaken) UnregisterHotKey(Handle, HotkeyId);
        _tray?.Dispose();
        if (_iconHandle != IntPtr.Zero) DestroyIcon(_iconHandle);
        _flush.Dispose();
        SaveHistory();
        base.OnFormClosed(e);
    }

    /* ---------------- helpers ---------------- */

    private int Scale(int css) => (int)Math.Round(css * DeviceDpi / 96f);

    private static bool IsHttp(string? s) =>
        s is not null && (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                          s.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

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

    private const int
        WM_HOTKEY = 0x0400,
        MOD_MENU = 0x0001, MOD_CONTROL = 0x0002, MOD_NOREPEAT = 0x4000,
        VK_SPACE = 0x20, VK_K = 0x4B;

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr h, int id, uint mod, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr h, int id);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr h);
}
