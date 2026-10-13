using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using VuStudio.Core.Providers;
using VuStudio.Session;
using VuStudio.Session.Service;
using VuStudio.Tray.Collectors;

namespace VuStudio.Tray;

/// <summary>
/// The tray application: icon and menu, service discovery and status, the main window, notifications, the
/// session collectors and the user-session provider host. Everything UI runs on the UI thread; network and
/// collectors run in the background and marshal back with <see cref="Post"/>.
/// </summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private const string RepositoryUrl = "https://github.com/blinkone/vudials";

    private readonly TrayOptions _options;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger _log;
    private readonly SingleInstance _instance;
    private readonly TrayText _text = new();
    private readonly SynchronizationContext _ui;
    private readonly ContextMenuStrip _menu = new();
    private readonly NotifyIcon _icon;
    private readonly BroadcastWindow _broadcast = new();
    private readonly VuStudioApiClient _api;
    private readonly SessionClient _session;
    private readonly UserSessionProviderHost _host;
    private readonly SemaphoreSlim _hostGate = new(1, 1);
    private readonly List<Collector> _collectors = [];
    private readonly AccentCollector _accent;
    private readonly AudioCollector _audio;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _pollNow = new(0, 1);
    private readonly Task _monitor;
    private readonly string _version;

    private readonly ToolStripMenuItem _openItem;
    private readonly ToolStripMenuItem _browserItem;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _startServiceItem;
    private readonly ToolStripSeparator _startServiceSeparator = new();
    private readonly ToolStripMenuItem _profilesItem;
    private readonly ToolStripMenuItem _pauseItem;
    private readonly ToolStripMenuItem _refreshItem;
    private readonly ToolStripMenuItem _startupItem;

    private MainForm? _window;
    private int _port;
    private bool _online;
    private ServiceInfo? _info;
    private EngineStatus? _engine;
    private IReadOnlyList<ProfileSummary> _profiles = [];
    private long _profilesRevision = -1;
    private DateTimeOffset _fastPollUntil;
    private bool _exiting;

    public TrayApplicationContext(TrayOptions options, ILoggerFactory loggers, SingleInstance instance)
    {
        _options = options;
        _loggers = loggers;
        _log = loggers.CreateLogger("Tray");
        _instance = instance;
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _version = typeof(TrayApplicationContext).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";
        _port = ServiceLocator.ResolvePort(options.Port);
        _log.LogInformation("VU Studio desktop app {Version} starting; service port {Port}", _version, _port);
        _api = new VuStudioApiClient(ServiceLocator.AppUri(_port));

        _openItem = new ToolStripMenuItem(_text["Open"], null, (_, _) => ShowWindow()) { Font = new Font(_menu.Font, FontStyle.Bold) };
        _browserItem = new ToolStripMenuItem(_text["OpenInBrowser"], null, (_, _) => OpenInBrowser());
        _statusItem = new ToolStripMenuItem(_text["ServiceOffline"]) { Enabled = false };
        _startServiceItem = new ToolStripMenuItem(_text["StartService"], null, (_, _) => StartService());
        _profilesItem = new ToolStripMenuItem(_text["Profiles"]);
        _pauseItem = new ToolStripMenuItem(_text["Pause"], null, (_, _) => Run(() => _api.SetPausedAsync(!(_engine?.Paused ?? false))));
        _refreshItem = new ToolStripMenuItem(_text["RefreshDials"], null, (_, _) => Run(() => _api.RefreshDialsAsync()));
        _startupItem = new ToolStripMenuItem(_text["StartWithWindows"], null, (_, _) => ToggleStartup());
        _menu.Items.AddRange(
        [
            _openItem, _browserItem, _startServiceSeparator, _statusItem, _startServiceItem, new ToolStripSeparator(),
            _profilesItem, _pauseItem, _refreshItem, new ToolStripSeparator(),
            _startupItem, new ToolStripMenuItem(_text["About"], null, (_, _) => ShowAbout()),
            new ToolStripMenuItem(_text["Exit"], null, (_, _) => ExitThread()),
        ]);
        _menu.Opening += (_, _) =>
        {
            _startupItem.Checked = SafeStartupState();
            RequestPoll();
        };

        _icon = new NotifyIcon { Icon = AppIcon.Load(SystemInformation.SmallIconSize), ContextMenuStrip = _menu, Text = _text["TooltipOffline"], Visible = true };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                ShowWindow();
            }
        };

        _session = new SessionClient(SessionClient.EndpointFor(_port), new SessionClientOptions { Version = _version }, loggers.CreateLogger("Session"));
        _session.NotificationReceived += (_, n) => Post(() => ShowNotification(n));
        _session.ProvidersReceived += (_, list) => _ = ApplyProvidersAsync(list);
        _host = new UserSessionProviderHost(_session, UserSessionProviderHost.DiscoverFactories(), new SessionServices(loggers), loggers.CreateLogger("UserProviders"));

        var foreground = new ForegroundCollector(_session, loggers.CreateLogger("Foreground"));
        _accent = new AccentCollector(_session, loggers.CreateLogger("Accent"));
        _audio = new AudioCollector(_session, loggers.CreateLogger("Audio"));
        _collectors.AddRange(
        [
            new LockStateCollector(_session, loggers.CreateLogger("Lock")),
            new IdleCollector(_session, loggers.CreateLogger("Idle")),
            foreground,
            _accent,
            _audio,
            new MediaCollector(_session, loggers.CreateLogger("Media")),
            new GameCollector(_session, loggers.CreateLogger("Games"), () => foreground.ForegroundPid),
        ]);
        foreach (var collector in _collectors)
        {
            try
            {
                collector.Start(); // on the UI thread: SystemEvents handlers are raised on it
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Could not start the {Collector} collector", collector.Name);
            }
        }

        _broadcast.ColorsChanged += (_, _) => _accent.Trigger();
        _broadcast.SessionEnding += (_, _) => Shutdown(fast: true);
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        _session.Start();
        _monitor = Task.Run(() => MonitorLoopAsync(_stop.Token));
        instance.ListenForShow(() => Post(ShowWindow));
        ApplyServiceState();
        if (!options.Background)
        {
            Post(ShowWindow);
        }
    }

    protected override void ExitThreadCore()
    {
        Shutdown(fast: false);
        base.ExitThreadCore();
    }

    private Uri AppUri => ServiceLocator.AppUri(_port);

    // ---- service discovery ------------------------------------------------------------------------

    private async Task MonitorLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var info = await _api.TryGetInfoAsync(ct).ConfigureAwait(false);
                if (info is null)
                {
                    // The port may have changed in config.json meanwhile.
                    var port = ServiceLocator.ResolvePort(_options.Port);
                    if (port != _port)
                    {
                        _log.LogInformation("Service port changed to {Port}", port);
                        _port = port;
                        _api.BaseUri = ServiceLocator.AppUri(port);
                        _session.SetEndpoint(SessionClient.EndpointFor(port));
                        info = await _api.TryGetInfoAsync(ct).ConfigureAwait(false);
                    }
                }

                EngineStatus? engine = null;
                var profiles = _profiles;
                if (info is not null)
                {
                    try
                    {
                        engine = await _api.GetEngineStatusAsync(ct).ConfigureAwait(false);
                        if (engine.Revision != Interlocked.Read(ref _profilesRevision))
                        {
                            profiles = await _api.GetProfilesAsync(ct).ConfigureAwait(false);
                            Interlocked.Exchange(ref _profilesRevision, engine.Revision);
                        }
                    }
                    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
                    {
                        _log.LogDebug(ex, "Reading the engine state failed");
                    }
                }

                var cameOnline = info is not null && !_online;
                Post(() =>
                {
                    if (info is not null && !_online)
                    {
                        _log.LogInformation("Service {Version} is reachable on port {Port}", info.Version, _port);
                    }
                    else if (info is null && _online)
                    {
                        _log.LogWarning("Service is not reachable on port {Port}", _port);
                    }

                    _online = info is not null;
                    _info = info ?? _info;
                    _engine = engine;
                    _profiles = profiles;
                    ApplyServiceState();
                });
                if (cameOnline)
                {
                    _session.Nudge();
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Service monitor failed");
            }

            var interval = DateTimeOffset.UtcNow < _fastPollUntil ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(5);
            try
            {
                await _pollNow.WaitAsync(interval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void RequestPoll()
    {
        try
        {
            if (_pollNow.CurrentCount == 0)
            {
                _pollNow.Release();
            }
        }
        catch (SemaphoreFullException)
        {
        }
    }

    /// <summary>UI thread: tooltip, menu and window reflect the service state.</summary>
    private void ApplyServiceState()
    {
        if (_exiting)
        {
            return;
        }

        var tooltip = !_online ? _text["TooltipOffline"] : _engine?.Paused == true ? _text["TooltipPaused"] : _text["TooltipConnected"];
        if (_online && _engine?.ActiveProfileName is { Length: > 0 } profile)
        {
            tooltip += $" · {profile}";
        }

        _icon.Text = tooltip.Length > 63 ? tooltip[..63] : tooltip;
        _statusItem.Visible = _startServiceItem.Visible = _startServiceSeparator.Visible = !_online;
        _browserItem.Enabled = _online;
        _profilesItem.Enabled = _pauseItem.Enabled = _refreshItem.Enabled = _online;
        _pauseItem.Text = _engine?.Paused == true ? _text["Resume"] : _text["Pause"];
        _pauseItem.Checked = false;
        BuildProfilesMenu();
        _window?.SetService(_online, AppUri);
    }

    private void BuildProfilesMenu()
    {
        _profilesItem.DropDownItems.Clear();
        var manual = _engine?.ManualProfileId;
        var automaticText = manual is null && _engine?.ActiveProfileName is { Length: > 0 } active
            ? _text.Format("AutomaticActive", active)
            : _text["Automatic"];
        _profilesItem.DropDownItems.Add(new ToolStripMenuItem(automaticText, null, (_, _) => SelectProfile(null)) { Checked = manual is null });
        _profilesItem.DropDownItems.Add(new ToolStripSeparator());
        var enabled = _profiles.Where(p => p.Enabled).ToList();
        if (enabled.Count == 0)
        {
            _profilesItem.DropDownItems.Add(new ToolStripMenuItem(_text["NoProfiles"]) { Enabled = false });
        }

        foreach (var profile in enabled)
        {
            var id = profile.Id;
            _profilesItem.DropDownItems.Add(new ToolStripMenuItem(profile.Name, null, (_, _) => SelectProfile(id)) { Checked = manual == id });
        }
    }

    private void SelectProfile(string? id) => Run(() => _api.SetProfileAsync(id));

    /// <summary>Runs a REST action off the UI thread; failures become a balloon; the state is re-read right after.</summary>
    private void Run(Func<Task> action) => _ = Task.Run(async () =>
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _log.LogWarning(ex, "Service action failed");
            Post(() => Balloon(_text["App"], _text.Format("ActionFailed", ex.Message), ToolTipIcon.Warning));
        }

        RequestPoll();
    });

    private void StartService()
    {
        Balloon(_text["App"], _text["ServiceStarting"], ToolTipIcon.Info);
        _ = Task.Run(async () =>
        {
            try
            {
                var (result, code) = await ServiceStarter.StartAsync(_stop.Token).ConfigureAwait(false);
                _log.LogInformation("Start service: {Result} ({Code})", result, code);
                var message = result switch
                {
                    ServiceStartResult.Cancelled => _text["ServiceStartCancelled"],
                    ServiceStartResult.NotInstalled => _text["ServiceNotInstalled"],
                    ServiceStartResult.Failed => _text.Format("ServiceStartFailed", code),
                    _ => null,
                };
                if (message is not null)
                {
                    Post(() => Balloon(_text["App"], message, ToolTipIcon.Warning));
                }
                else
                {
                    _fastPollUntil = DateTimeOffset.UtcNow.AddSeconds(30);
                    RequestPoll();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Starting the service failed");
                Post(() => Balloon(_text["App"], _text.Format("ServiceStartFailed", ex.Message), ToolTipIcon.Warning));
            }
        });
    }

    // ---- window, browser, about, startup ---------------------------------------------------------------

    private void ShowWindow()
    {
        if (_exiting)
        {
            return;
        }

        if (_window is null || _window.IsDisposed)
        {
            _window = new MainForm(AppUri, _text, _loggers.CreateLogger("Window"), StartService);
            _window.SetService(_online, AppUri);
        }

        _window.ShowAndActivate();
    }

    private void OpenInBrowser()
    {
        try
        {
            Process.Start(new ProcessStartInfo(AppUri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not open the browser");
        }
    }

    private void ShowAbout()
    {
        var service = _info is { } info && _online ? $"{info.Version} ({AppUri.AbsoluteUri})" : _text["NotConnected"];
        MessageBox.Show(_text.Format("AboutText", _version, service, RepositoryUrl), _text["About"] + " — " + _text["App"], MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void ToggleStartup()
    {
        try
        {
            if (StartupRegistration.InCurrentUser)
            {
                StartupRegistration.Disable();
                if (StartupRegistration.InLocalMachine)
                {
                    Balloon(_text["App"], _text["StartupManagedByInstaller"], ToolTipIcon.Info);
                }
            }
            else if (StartupRegistration.InLocalMachine)
            {
                Balloon(_text["App"], _text["StartupManagedByInstaller"], ToolTipIcon.Info);
            }
            else
            {
                StartupRegistration.Enable();
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Balloon(_text["App"], _text.Format("StartupFailed", ex.Message), ToolTipIcon.Warning);
        }

        _startupItem.Checked = SafeStartupState();
    }

    private bool SafeStartupState()
    {
        try
        {
            return StartupRegistration.IsEnabled;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return false;
        }
    }

    // ---- notifications, providers, power ---------------------------------------------------------------

    private void ShowNotification(SessionNotification n)
    {
        var icon = n.Icon?.ToLowerInvariant() switch
        {
            null => ToolTipIcon.Info,
            var i when i.Contains("octagon") || i.Contains("error") || i.Contains("fire") => ToolTipIcon.Error,
            var i when i.Contains("alert") || i.Contains("warn") || i.Contains("thermometer") => ToolTipIcon.Warning,
            _ => ToolTipIcon.Info,
        };
        Balloon(n.Title, n.Message, icon);
    }

    private void Balloon(string title, string message, ToolTipIcon icon)
    {
        if (_exiting)
        {
            return;
        }

        // ShowBalloonTip rejects an empty text.
        _icon.ShowBalloonTip(5000, string.IsNullOrWhiteSpace(title) ? _text["App"] : title, string.IsNullOrWhiteSpace(message) ? " " : message, icon);
    }

    private async Task ApplyProvidersAsync(IReadOnlyList<ProviderInstanceConfig> providers)
    {
        // Messages can arrive back to back: apply them in order.
        await _hostGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_exiting)
            {
                _log.LogInformation("Service asks for {Count} user-session provider(s)", providers.Count);
                await _host.ApplyAsync(providers).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Applying user-session providers failed");
        }
        finally
        {
            _hostGate.Release();
        }
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            _log.LogInformation("Resumed from sleep");
            _audio.RequestReacquire();
            _session.Nudge();
            _fastPollUntil = DateTimeOffset.UtcNow.AddSeconds(30);
            RequestPoll();
        }
    }

    private void Post(Action action)
    {
        _ui.Post(_ =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "UI action failed");
            }
        }, null);
    }

    // ---- shutdown ------------------------------------------------------------------------------------

    /// <summary>Stops everything in order. <paramref name="fast"/> (Windows is ending the session) uses short timeouts.</summary>
    private void Shutdown(bool fast)
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        _log.LogInformation("Shutting down{Fast}", fast ? " (session ending)" : "");
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _stop.Cancel();

        if (_window is { IsDisposed: false } window)
        {
            window.AllowClose = true;
            window.Close();
            window.Dispose();
        }

        _icon.Visible = false;

        // Nothing below needs the UI thread (every await uses ConfigureAwait(false)), so waiting here cannot deadlock.
        var background = Task.Run(async () =>
        {
            await Task.WhenAll(_collectors.Select(c => c.StopAsync())).ConfigureAwait(false);
            await _host.StopAllAsync().ConfigureAwait(false);
            await _session.StopAsync().ConfigureAwait(false);
            try
            {
                await _monitor.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        });
        try
        {
            if (!background.Wait(fast ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(8)))
            {
                _log.LogWarning("Shutdown did not complete in time");
            }
        }
        catch (AggregateException ex)
        {
            _log.LogWarning(ex, "Shutdown failed");
        }

        _icon.Dispose();
        _menu.Dispose();
        _broadcast.Dispose();
        _api.Dispose();
        _instance.Dispose();
        if (fast)
        {
            ExitThread();
        }
    }
}
