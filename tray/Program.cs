using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
            OnClick = OpenPopover,
            OnRefresh = () => RefreshAll(force: true),
            OnQuit = Quit,
            OnToggleTheme = ToggleTheme
        };
        _popover.OnThemeChanged = t => { Theme.Save(t); _strip.SetData(_lastJson); };
        _strip.Show();
        // Show the persisted last-known payload right away — meters/spend are on screen before the
        // first (potentially slow) scan finishes.
        _strip.SetData(_lastJson);
        _popover.SetData(_lastJson);
        // Use the shared 5-minute cache (never force at launch — repeated forced reads are what trigger
        // the provider rate-limiting). The strip already shows last-known values from its own cache; the
        // spend fallback keeps a provider visible even while its live limits are momentarily unavailable.
        RefreshAll(force: false);

        _refresh = new System.Windows.Forms.Timer { Interval = 5 * 60 * 1000 };
        _refresh.Tick += (_, _) => RefreshAll(force: false);
        _refresh.Start();
    }

    private string _lastJson = LoadLastPayload(); // last-known payload, so meters show instantly at launch

    // Strip-click: prime the popover with the last-known data so it opens INSTANTLY (never waits on the
    // all-time scan), then freshen in the background.
    private void OpenPopover()
    {
        if (_popover.IsShown) { _popover.Toggle(); return; } // second click closes it
        _popover.SetData(_lastJson);
        _popover.Toggle();
        RefreshAll(force: false);
    }

    // One shared fetch feeds both the strip and the popover — no redundant CLI scans. The result is
    // merged with the last payload so meters survive a rate-limited refresh, then persisted so they're
    // on screen immediately at next launch too.
    private void RefreshAll(bool force)
    {
        Task.Run(() =>
        {
            string fetched = Cli.Fetch(force);
            string merged = UsageMerge.Merge(_lastJson, fetched);
            _strip.BeginInvoke(() =>
            {
                _lastJson = merged;
                SaveLastPayload(merged);
                _strip.SetData(merged);
                _popover.SetData(merged);
            });
        });
    }

    private static readonly string LastPayloadFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenUsage", "last_ui.json");

    private static void SaveLastPayload(string json)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LastPayloadFile)!);
            File.WriteAllText(LastPayloadFile, json);
        }
        catch { }
    }

    private static string LoadLastPayload()
    {
        try { return File.Exists(LastPayloadFile) ? File.ReadAllText(LastPayloadFile) : "[]"; }
        catch { return "[]"; }
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

        // NOT docked: the WebView keeps a FIXED size and stays pinned to the window's bottom-right while
        // the window itself grows during the open animation. That way the whole panel (background +
        // rounded corners + content) scales out of the corner instead of the background popping in at
        // full size, and the page never reflows mid-animation.
        _web.Dock = DockStyle.None;
        Controls.Add(_web);
        Deactivate += (_, _) => { if (!_growing) Hide(); };

        _grow = new System.Windows.Forms.Timer { Interval = 16 };
        _grow.Tick += (_, _) => GrowTick();

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
            if (_pendingShow) { _pendingShow = false; Inject(_data); }
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
                // Re-target the final bounds. While the open animation is still running it will land on
                // the new size by itself; otherwise snap straight to it.
                _finalBounds = ComputeFinalBounds();
                _web.Size = new Size(_finalBounds.Width, _finalBounds.Height);
                if (!_growing) ApplyGrow(1f);
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

    public bool IsShown => Visible;

    public void Toggle()
    {
        if (Visible) { Hide(); return; }
        ShowPopover();
    }

    private void ShowPopover()
    {
        _finalBounds = ComputeFinalBounds();
        _web.Size = new Size(_finalBounds.Width, _finalBounds.Height);
        StartGrow();                 // window starts small at the corner and grows to _finalBounds
        Show();
        TopMost = true;
        Activate();
        NativeActivate();
        PlayOpen();
        // Render the last-known data INSTANTLY (no waiting on a scan) — stale-while-revalidate. The host
        // triggers a background refresh separately, which calls SetData again when fresh data arrives.
        if (_selfTest) { Refresh(force: false); return; } // selftest fetches its own data
        if (_ready) Inject(_data);
        else _pendingShow = true;
    }

    // ---- open animation: the WHOLE window (background, rounded corners and all) scales out of the
    // bottom-right corner, so nothing pops in at full size. Runs in lockstep with the page's CSS spring.
    private readonly System.Windows.Forms.Timer _grow;
    private bool _growing;
    private float _growT;
    private Rectangle _finalBounds;
    private const float GrowStart = 0.55f;
    private const float GrowMs = 440f;

    private Rectangle ComputeFinalBounds()
    {
        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        int h = Math.Clamp(_lastHeight, 120, MaxScreenHeight());
        int x = Math.Max(wa.Left + Margin_, wa.Right - Width_ - Margin_);
        int y = Math.Max(wa.Top + Margin_, wa.Bottom - h - Margin_);
        return new Rectangle(x, y, Width_, h);
    }

    private void StartGrow()
    {
        _growing = true;
        _growT = 0f;
        ApplyGrow(GrowStart);
        _grow.Start();
    }

    private void GrowTick()
    {
        _growT += 16f / GrowMs;
        if (_growT >= 1f) { _growT = 1f; _growing = false; _grow.Stop(); }
        float e = 1f - (float)Math.Pow(1 - _growT, 5); // easeOutQuint ≈ the page's cubic-bezier(.16,1,.3,1)
        ApplyGrow(GrowStart + (1f - GrowStart) * e);
    }

    private void ApplyGrow(float scale)
    {
        int w = Math.Max(8, (int)(_finalBounds.Width * scale));
        int h = Math.Max(8, (int)(_finalBounds.Height * scale));
        // Pin the bottom-right corner (nearest the strip) so it grows outward from there.
        base.SetBounds(_finalBounds.Right - w, _finalBounds.Bottom - h, w, h, BoundsSpecified.All);
        // Keep the fixed-size WebView glued to that same corner so the page never reflows.
        _web.Location = new Point(ClientSize.Width - _finalBounds.Width, ClientSize.Height - _finalBounds.Height);
        ApplyRoundedRegion();
    }

    private string _data = "[]";

    /// Feed the popover the latest payload (from the host's shared fetch). Re-renders live if visible.
    public void SetData(string json)
    {
        _data = string.IsNullOrWhiteSpace(json) ? "[]" : json;
        if (_ready && Visible) Inject(_data);
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

/// Keeps usage meters on screen at ALL times. A provider's live-limits API can be briefly rate-limited,
/// in which case the refresh comes back with spend rows but no `progress` (meter) lines at all — the
/// Session/Weekly bars would just vanish. This carries the last-known meters forward into such a payload
/// (stale-while-revalidate, the same thing the macOS app does), so the bars stay visible until real
/// values return.
static class UsageMerge
{
    public static string Merge(string previousJson, string currentJson)
    {
        try
        {
            var current = JsonNode.Parse(currentJson);
            var previous = JsonNode.Parse(previousJson);
            var currentProviders = current?["providers"]?.AsArray();
            var previousProviders = previous?["providers"]?.AsArray();
            if (currentProviders is null || previousProviders is null) return currentJson;

            foreach (var providerNode in currentProviders)
            {
                var id = providerNode?["providerId"]?.GetValue<string>();
                var lines = providerNode?["lines"]?.AsArray();
                if (id is null || lines is null) continue;
                if (lines.Any(l => l?["type"]?.GetValue<string>() == "progress")) continue; // already has meters

                var previousLines = previousProviders
                    .FirstOrDefault(p => p?["providerId"]?.GetValue<string>() == id)?["lines"]?.AsArray();
                if (previousLines is null) continue;

                var carried = previousLines
                    .Where(l => l?["type"]?.GetValue<string>() == "progress")
                    .Select(l => l!.ToJsonString())
                    .ToList();
                // Meters lead the card, so re-insert them at the front in their original order.
                for (int i = carried.Count - 1; i >= 0; i--) lines.Insert(0, JsonNode.Parse(carried[i]));
            }
            return current!.ToJsonString();
        }
        catch { return currentJson; }
    }
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
