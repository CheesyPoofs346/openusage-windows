using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace OpenUsageTray;

// OpenUsage system-tray host for Windows. A NotifyIcon in the tray; a left-click pops a borderless,
// rounded WebView2 window (web/index.html) rendering the same popover UI as the macOS app, fed by the
// `openusage --ui` CLI (the ported Swift core). Right-click gives Refresh / Open at login / Quit.
static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        // Headless end-to-end check: show the popover, fetch + render real data, write what the
        // WebView actually rendered to %TEMP%\openusage_selftest.json, then exit. Used to verify the
        // full path (window → CLI → data → DOM) without a human clicking the tray.
        if (args.Contains("--selftest"))
        {
            var form = new PopoverForm(selfTest: true);
            Application.Run();
            GC.KeepAlive(form);
            return;
        }

        if (args.Contains("--striptest"))
        {
            var strip = new StripForm { IsDark = () => true };
            strip.SetData(Cli.Fetch(force: false));
            strip.RenderToFile(Path.Combine(Path.GetTempPath(), "openusage_striptest_usage.png"), showPrice: false);
            strip.RenderToFile(Path.Combine(Path.GetTempPath(), "openusage_striptest_price.png"), showPrice: true);
            return;
        }

        using var mutex = new Mutex(true, "OpenUsageTray_SingleInstance", out bool isNew);
        if (!isNew) return; // already running

        using var app = new AppHost();
        Application.Run();
    }
}

// Owns the taskbar strip + the popover. No system-tray icon — the strip is the whole surface, matching
// the macOS menu-bar-pin experience (metrics on the bar, click to expand).
sealed class AppHost : IDisposable
{
    private readonly PopoverForm _popover;
    private readonly StripForm _strip;
    private readonly System.Windows.Forms.Timer _refresh;

    public AppHost()
    {
        _popover = new PopoverForm { ThemeProvider = Theme.Current };
        _strip = new StripForm
        {
            IsDark = () => Theme.Current() == "dark",
            OnClick = () => _popover.Toggle(),
            OnRefresh = () => RefreshAll(force: true),
            OnQuit = Quit,
            OnToggleTheme = ToggleTheme
        };
        _popover.OnThemeChanged = t => { Theme.Save(t); _strip.SetData(_lastJson); };
        _strip.Show();
        // Use the shared 5-minute cache (never force at launch — repeated forced reads are what trigger
        // the provider rate-limiting). The strip already shows last-known values from its own cache; the
        // spend fallback keeps a provider visible even while its live limits are momentarily unavailable.
        RefreshAll(force: false);

        _refresh = new System.Windows.Forms.Timer { Interval = 5 * 60 * 1000 };
        _refresh.Tick += (_, _) => RefreshAll(force: false);
        _refresh.Start();
    }

    private string _lastJson = "[]";

    private void RefreshAll(bool force)
    {
        Task.Run(() =>
        {
            string json = Cli.Fetch(force);
            _strip.BeginInvoke(() => { _lastJson = json; _strip.SetData(json); });
        });
        _popover.Refresh(force); // popover fetches its own copy for the rich UI
    }

    private void ToggleTheme()
    {
        string next = Theme.Current() == "dark" ? "light" : "dark";
        Theme.Save(next);
        _popover.SetTheme(next);
        _strip.SetData(_lastJson);
    }

    private void Quit()
    {
        _strip.Hide();
        Application.Exit();
    }

    public void Dispose() { _refresh.Dispose(); _strip.Dispose(); _popover.Dispose(); }
}

// Persisted light/dark preference, shared by the popover and the strip menu checkmark.
static class Theme
{
    private static readonly string File_ = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenUsage", "theme.txt");

    public static string Current()
    {
        try { return System.IO.File.Exists(File_) && System.IO.File.ReadAllText(File_).Trim() == "dark" ? "dark" : "light"; }
        catch { return "light"; }
    }

    public static void Save(string t)
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(File_)!); System.IO.File.WriteAllText(File_, t == "dark" ? "dark" : "light"); }
        catch { }
    }
}

sealed class PopoverForm : Form
{
    private readonly WebView2 _web = new();
    private bool _ready;
    private bool _pendingShow;
    private int _lastHeight = 640;
    private readonly bool _selfTest;

    /// Supplies the persisted theme ("light"/"dark") to apply once the page loads.
    public Func<string>? ThemeProvider;
    /// Raised when the user flips the theme from the popover footer, so the host can persist it.
    public Action<string>? OnThemeChanged;

    private const int Width_ = 372;
    // Tall enough to show every provider without scrolling; the real limit is the screen work area
    // (MaxScreenHeight), so on a normal display the popover just grows to fit all content.
    private const int MaxHeight = 2000;
    private const int Margin_ = 12;

    public PopoverForm(bool selfTest = false)
    {
        _selfTest = selfTest;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = Color.White;
        DoubleBuffered = true;
        Size = new Size(Width_, _lastHeight);
        // NOTE: never set Opacity != 1 — it turns the form into a WS_EX_LAYERED window, and WebView2
        // (DirectComposition) renders BLANK in layered windows. That was the "blank popover" bug.

        _web.Dock = DockStyle.Fill;
        Controls.Add(_web);
        Deactivate += (_, _) => Hide();

        _ = InitWebAsync();
    }

    protected override bool ShowWithoutActivation => false;

    private async Task InitWebAsync()
    {
        var userData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenUsage", "webview");
        Directory.CreateDirectory(userData);
        var env = await CoreWebView2Environment.CreateAsync(null, userData);
        await _web.EnsureCoreWebView2Async(env);

        var core = _web.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        ApplyBackdrop(ThemeProvider?.Invoke() ?? "light");

        // Skip the file's built-in sample payload; the host injects the real data.
        await core.AddScriptToExecuteOnDocumentCreatedAsync("window.__NO_SAMPLE__ = true;");

        // Serve the web/ folder over a virtual host so the SVG icon masks resolve cleanly.
        var webDir = Path.Combine(AppContext.BaseDirectory, "web");
        core.SetVirtualHostNameToFolderMapping("openusage.local", webDir,
            CoreWebView2HostResourceAccessKind.Allow);

        core.WebMessageReceived += OnWebMessage;
        core.NavigationCompleted += async (_, _) =>
        {
            _ready = true;
            // Apply the host's persisted theme so the strip menu and the popover always agree.
            var theme = ThemeProvider?.Invoke() ?? "light";
            try { await core.ExecuteScriptAsync($"window.setTheme && window.setTheme('{theme}')"); } catch { }
            if (_selfTest) { ShowPopover(); return; }
            if (_pendingShow) { _pendingShow = false; Refresh(force: false); }
        };

        core.Navigate("https://openusage.local/index.html");
    }

    // The page posts its measured content height so the window hugs the content like the Mac popover.
    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var json = e.WebMessageAsJson;
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("height", out var h))
            {
                int height = (int)Math.Ceiling(h.GetDouble());
                _lastHeight = Math.Clamp(height, 120, MaxScreenHeight());
                Height = _lastHeight;
                Reposition();
                ApplyRoundedRegion();
            }
            if (doc.RootElement.TryGetProperty("theme", out var th))
            {
                var t = th.GetString() == "dark" ? "dark" : "light";
                ApplyBackdrop(t);
                OnThemeChanged?.Invoke(t);
            }
        }
        catch { /* ignore malformed messages */ }
    }

    public void Toggle()
    {
        if (Visible) { Hide(); return; }
        ShowPopover();
    }

    private void ShowPopover()
    {
        Reposition();
        ApplyRoundedRegion();
        Show();
        TopMost = true;
        Activate();
        NativeActivate();
        PlayOpen();
        if (_ready) Refresh(force: false);
        else _pendingShow = true;
    }

    /// Replay the spring-open animation inside the page each time the popover is shown.
    private async void PlayOpen()
    {
        if (_web.CoreWebView2 == null) return;
        try { await _web.CoreWebView2.ExecuteScriptAsync("window.playOpen && window.playOpen()"); } catch { }
    }

    /// Match the window/WebView backdrop to the theme so the spring-open never flashes a white box.
    private void ApplyBackdrop(string theme)
    {
        var c = theme == "dark" ? Color.FromArgb(0x1c, 0x1c, 0x1e) : Color.White;
        BackColor = c;
        if (_web.CoreWebView2 != null) _web.DefaultBackgroundColor = c;
    }

    /// Force a theme on the live page (used by the strip's right-click "Dark mode" item).
    public async void SetTheme(string theme)
    {
        ApplyBackdrop(theme);
        if (_web.CoreWebView2 == null) return;
        try { await _web.CoreWebView2.ExecuteScriptAsync($"window.setTheme && window.setTheme('{theme}')"); } catch { }
    }

    /// Fetch data from the CLI on a background thread and inject it into the page.
    public void Refresh(bool force)
    {
        if (!_ready) { _pendingShow = true; return; }
        Task.Run(() =>
        {
            string payload = Cli.Fetch(force);
            BeginInvoke(() => Inject(payload));
        });
    }

    private async void Inject(string payload)
    {
        if (_web.CoreWebView2 == null) return;
        // payload is a JSON array (or error text). Pass it straight to renderData, then report height.
        string safe = string.IsNullOrWhiteSpace(payload) ? "[]" : payload;
        string script =
            "try{window.renderData(" + safe + ");}catch(e){}" +
            "window.chrome.webview.postMessage({height: Math.min(document.body.scrollHeight, " + MaxHeight + ")});";
        try { await _web.CoreWebView2.ExecuteScriptAsync(script); } catch { }

        if (_selfTest)
        {
            string probe = await _web.CoreWebView2.ExecuteScriptAsync(
                "JSON.stringify({sections:document.querySelectorAll('.section').length," +
                "meters:document.querySelectorAll('.meter').length," +
                "spend:!!document.querySelector('.spend')," +
                "center:(document.querySelector('.donut .center')||{}).textContent||null," +
                "height:document.body.scrollHeight,textLen:document.body.innerText.length})");
            try
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "openusage_selftest.json"),
                    probe ?? "null");
                // Capture what the WebView actually paints, so the popover can be inspected headlessly.
                var png = Path.Combine(Path.GetTempPath(), "openusage_selftest.png");
                using (var fs = File.Create(png))
                {
                    await _web.CoreWebView2.CapturePreviewAsync(
                        CoreWebView2CapturePreviewImageFormat.Png, fs);
                }
            }
            catch { }
            Application.Exit();
        }
    }

    private int MaxScreenHeight()
    {
        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        return Math.Min(MaxHeight, wa.Height - 2 * Margin_);
    }

    private void Reposition()
    {
        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        int x = wa.Right - Width_ - Margin_;
        int y = wa.Bottom - Height - Margin_;
        Location = new Point(Math.Max(wa.Left + Margin_, x), Math.Max(wa.Top + Margin_, y));
    }

    private void ApplyRoundedRegion()
    {
        using var path = new GraphicsPath();
        int r = 20;
        var rect = new Rectangle(0, 0, Width, Height);
        path.AddArc(rect.X, rect.Y, r, r, 180, 90);
        path.AddArc(rect.Right - r, rect.Y, r, r, 270, 90);
        path.AddArc(rect.Right - r, rect.Bottom - r, r, r, 0, 90);
        path.AddArc(rect.X, rect.Bottom - r, r, r, 90, 90);
        path.CloseFigure();
        Region = new Region(path);
    }

    // Give the borderless window a soft drop shadow, like the floating macOS panel.
    protected override CreateParams CreateParams
    {
        get
        {
            const int CS_DROPSHADOW = 0x20000;
            const int WS_EX_TOOLWINDOW = 0x80; // keep it out of Alt-Tab
            var cp = base.CreateParams;
            cp.ClassStyle |= CS_DROPSHADOW;
            cp.ExStyle |= WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    private void NativeActivate() => SetForegroundWindow(Handle);
}

/// Runs the ported `openusage` CLI and returns its `--ui` JSON payload.
static class Cli
{
    private static string ExePath()
    {
        // Prefer a copy shipped next to the tray exe, then the installed location, then PATH.
        string local = Path.Combine(AppContext.BaseDirectory, "openusage.exe");
        if (File.Exists(local)) return local;
        string installed = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenUsage", "bin", "openusage.exe");
        if (File.Exists(installed)) return installed;
        return "openusage"; // rely on PATH
    }

    public static string Fetch(bool force)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ExePath(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            psi.ArgumentList.Add("--ui");
            if (force) psi.ArgumentList.Add("--force");

            using var p = Process.Start(psi)!;
            string outp = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit(30000);
            return string.IsNullOrWhiteSpace(outp) ? "[]" : outp.Trim();
        }
        catch
        {
            return "[]";
        }
    }
}
