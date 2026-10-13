using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using VuStudio.Session;
using VuStudio.Session.Service;

namespace VuStudio.Tray;

/// <summary>
/// The VU Studio window: the service's web UI in WebView2 (user data in <c>%LocalAppData%\VU Studio\WebView2</c>).
/// Navigation stays on the local service; other links open in the default browser. Closing hides the window
/// to the tray; size and position are remembered. Without the WebView2 runtime, or while the service is
/// down, an explanatory panel replaces the page.
/// </summary>
internal sealed class MainForm : Form
{
    public const string WebView2DownloadUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

    private static readonly Color Background = Color.FromArgb(0x16, 0x18, 0x1D);
    private static readonly Color Foreground = Color.FromArgb(0xE9, 0xE9, 0xEA);
    private static readonly Color Muted = Color.FromArgb(0xA3, 0xA5, 0xAB);
    private static readonly Color Amber = Color.FromArgb(0xFF, 0xB5, 0x47);

    private readonly TrayText _text;
    private readonly ILogger _log;
    private readonly Action _startService;
    private readonly Panel _overlay;
    private readonly Label _title;
    private readonly Label _hint;
    private readonly Button _primary;
    private readonly Button _secondary;
    private Uri _appUri;
    private WebView2? _web;
    private bool _initializing;
    private bool _runtimeMissing;
    private bool _online;
    private bool _loadedOnce;

    public MainForm(Uri appUri, TrayText text, ILogger log, Action startService)
    {
        _appUri = appUri;
        _text = text;
        _log = log;
        _startService = startService;

        Text = text["App"];
        Icon = AppIcon.Load();
        BackColor = Background;
        MinimumSize = new Size(720, 480);
        StartPosition = FormStartPosition.Manual;
        RestorePlacement();

        _title = new Label { AutoSize = true, ForeColor = Foreground, Font = new Font(Font.FontFamily, 14f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 8) };
        _hint = new Label { AutoSize = true, ForeColor = Muted, MaximumSize = new Size(560, 0), Margin = new Padding(0, 0, 0, 16) };
        _primary = MakeButton(primary: true);
        _secondary = MakeButton(primary: false);
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Background };
        buttons.Controls.AddRange([_primary, _secondary]);
        var stack = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Background, Anchor = AnchorStyles.None };
        stack.Controls.AddRange([_title, _hint, buttons]);
        var center = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 1, BackColor = Background };
        center.Controls.Add(stack, 0, 0);
        _overlay = new Panel { Dock = DockStyle.Fill, BackColor = Background, Visible = false };
        _overlay.Controls.Add(center);
        Controls.Add(_overlay);
    }

    /// <summary>Set to let the form really close (application exit, Windows shutdown).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool AllowClose { get; set; }

    public void ShowAndActivate()
    {
        if (!Visible)
        {
            Show();
        }

        if (WindowState == FormWindowState.Minimized)
        {
            WindowState = FormWindowState.Normal;
        }

        Activate();
        BringToFront();
        _ = InitializeWebViewAsync();
    }

    /// <summary>Called by the tray when the service becomes reachable or unreachable, or its port changes.</summary>
    public void SetService(bool online, Uri appUri)
    {
        var moved = appUri != _appUri;
        var cameOnline = online && !_online;
        _appUri = appUri;
        _online = online;
        if (_web?.CoreWebView2 is { } core && online && (cameOnline || moved || !_loadedOnce))
        {
            core.Navigate(_appUri.AbsoluteUri);
        }

        UpdateOverlay();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _ = InitializeWebViewAsync();
    }

    /// <summary>
    /// Closing only hides the window, but the page behind it would keep animating the dial previews and
    /// holding its live connection. Suspend it while hidden (scripts, timers and rendering stop, the page
    /// and any unsaved edits stay) and resume it when the window is shown again.
    /// </summary>
    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible)
        {
            ResumePage();
        }
        else
        {
            _ = SuspendPageAsync();
        }
    }

    private async Task SuspendPageAsync()
    {
        if (_web?.CoreWebView2 is not { IsSuspended: false } core)
        {
            return;
        }

        try
        {
            // Needs the control to be invisible already: the WinForms control follows the form.
            await core.TrySuspendAsync();
            _log.LogDebug("The page is suspended while the window is hidden");
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ObjectDisposedException)
        {
            _log.LogDebug(ex, "Could not suspend the page");
        }

        // Shown again while the suspend was still pending.
        if (Visible)
        {
            ResumePage();
        }
    }

    private void ResumePage()
    {
        if (_web?.CoreWebView2 is not { IsSuspended: true } core)
        {
            return;
        }

        try
        {
            core.Resume();
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ObjectDisposedException)
        {
            _log.LogDebug(ex, "Could not resume the page");
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        SavePlacement();
        if (!AllowClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _web?.Dispose();
            _web = null;
        }

        base.Dispose(disposing);
    }

    private async Task InitializeWebViewAsync()
    {
        if (_web is not null || _initializing || IsDisposed)
        {
            return;
        }

        _initializing = true;
        try
        {
            try
            {
                CoreWebView2Environment.GetAvailableBrowserVersionString();
                _runtimeMissing = false;
            }
            catch (WebView2RuntimeNotFoundException)
            {
                _runtimeMissing = true;
                _log.LogWarning("The WebView2 runtime is not installed");
                UpdateOverlay();
                return;
            }

            Directory.CreateDirectory(TrayPaths.WebView2);
            var environment = await CoreWebView2Environment.CreateAsync(null, TrayPaths.WebView2);
            var web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Background };
            Controls.Add(web);
            _overlay.BringToFront();
            await web.EnsureCoreWebView2Async(environment);

            var core = web.CoreWebView2;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
#if !DEBUG
            core.Settings.AreDevToolsEnabled = false;
#endif
            core.NavigationStarting += OnNavigationStarting;
            core.FrameNavigationStarting += OnFrameNavigationStarting;
            core.NewWindowRequested += OnNewWindowRequested;
            core.NavigationCompleted += (_, e) => _loadedOnce |= e.IsSuccess;
            core.ProcessFailed += OnProcessFailed;
            core.DocumentTitleChanged += (_, _) => Text = string.IsNullOrWhiteSpace(core.DocumentTitle) ? _text["App"] : core.DocumentTitle;
            _web = web;

            if (_online)
            {
                core.Navigate(_appUri.AbsoluteUri);
            }

            UpdateOverlay();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "WebView2 initialisation failed");
            ShowOverlay(_text.Format("WebView2Failed", ex.Message), _text["WebView2MissingHint"], (_text["OpenInBrowser"], OpenInBrowser), null);
        }
        finally
        {
            _initializing = false;
        }
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) || NavigationPolicy.IsAppUri(uri, _appUri) || NavigationPolicy.IsBenign(uri))
        {
            return;
        }

        e.Cancel = true;
        OpenExternally(uri);
    }

    private void OnFrameNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && !NavigationPolicy.IsAppUri(uri, _appUri) && !NavigationPolicy.IsBenign(uri))
        {
            e.Cancel = true;
        }
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri))
        {
            return;
        }

        if (NavigationPolicy.IsAppUri(uri, _appUri))
        {
            _web?.CoreWebView2?.Navigate(uri.AbsoluteUri);
        }
        else
        {
            OpenExternally(uri);
        }
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        _log.LogWarning("WebView2 process failed: {Kind}", e.ProcessFailedKind);
        if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
        {
            // The control is unusable: recreate it.
            var web = _web;
            _web = null;
            web?.Dispose();
            BeginInvoke(() => _ = InitializeWebViewAsync());
        }
        else if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.RenderProcessExited or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
        {
            _web?.CoreWebView2?.Reload();
        }
    }

    private void OpenExternally(Uri uri)
    {
        if (!NavigationPolicy.CanOpenExternally(uri))
        {
            _log.LogInformation("Blocked navigation to {Uri}", uri);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not open {Uri}", uri);
        }
    }

    private void OpenInBrowser() => OpenExternally(_appUri);

    private void UpdateOverlay()
    {
        if (_runtimeMissing)
        {
            ShowOverlay(_text["WebView2Missing"], _text["WebView2MissingHint"], (_text["WebView2Download"], () => OpenExternally(new Uri(WebView2DownloadUrl))), (_text["OpenInBrowser"], OpenInBrowser));
        }
        else if (!_online)
        {
            ShowOverlay(_text["ServiceOffline"], _text["ServiceOfflineHint"], (_text["StartService"], _startService), null);
        }
        else
        {
            _overlay.Visible = false;
        }
    }

    private void ShowOverlay(string title, string hint, (string Text, Action Action)? primary, (string Text, Action Action)? secondary)
    {
        _title.Text = title;
        _hint.Text = hint;
        Configure(_primary, primary);
        Configure(_secondary, secondary);
        _overlay.Visible = true;
        _overlay.BringToFront();

        static void Configure(Button button, (string Text, Action Action)? spec)
        {
            button.Visible = spec is not null;
            button.Text = spec?.Text ?? "";
            button.Tag = spec?.Action;
        }
    }

    private Button MakeButton(bool primary)
    {
        var button = new Button
        {
            AutoSize = true,
            FlatStyle = FlatStyle.Flat,
            BackColor = primary ? Amber : Background,
            ForeColor = primary ? Background : Foreground,
            Padding = new Padding(12, 4, 12, 4),
            Margin = new Padding(0, 0, 8, 0),
            UseVisualStyleBackColor = false,
        };
        button.FlatAppearance.BorderColor = primary ? Amber : Muted;
        button.Click += (_, _) => (button.Tag as Action)?.Invoke();
        return button;
    }

    private void RestorePlacement()
    {
        var saved = WindowSettings.Load();
        var bounds = saved is { Width: > 0, Height: > 0 } ? new Rectangle(saved.X, saved.Y, saved.Width, saved.Height) : Rectangle.Empty;
        if (bounds.IsEmpty || !Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(bounds)))
        {
            var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
            var size = new Size(Math.Min(1280, area.Width - 80), Math.Min(840, area.Height - 80));
            bounds = new Rectangle(area.Left + ((area.Width - size.Width) / 2), area.Top + ((area.Height - size.Height) / 2), size.Width, size.Height);
        }

        Bounds = bounds;
        if (saved?.Maximized == true)
        {
            WindowState = FormWindowState.Maximized;
        }
    }

    private void SavePlacement()
    {
        var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        new WindowSettings
        {
            X = bounds.X,
            Y = bounds.Y,
            Width = bounds.Width,
            Height = bounds.Height,
            Maximized = WindowState == FormWindowState.Maximized,
        }.Save();
    }
}
