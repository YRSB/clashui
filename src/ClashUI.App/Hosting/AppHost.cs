using ClashUI.App.Services;
using ClashUI.App.Tray;
using ClashUI.Core;
using Microsoft.UI.Dispatching;
namespace ClashUI.App.Hosting;
public sealed record HostStartArgs(Action ShowWindow, Action ToggleWindow, string IconPath, bool HasSilentArg);
public sealed class AppHost : IDisposable
{
    private readonly ISingleInstanceGuard _guard;
    private readonly IActivationBridge _bridge;
    private readonly IElevationGate _elevationGate;
    private readonly IDispatcher _dispatcher;
    private readonly ISettingsStore _store;
    private readonly IConfigComposer _composer;
    private readonly CoreRuntime _runtime;
    private readonly IProfileWatcher _watcher;
    private readonly ITimeSource _time;
    private readonly Func<string, string, IMihomoApiClient> _apiFactory;
    private readonly ISystemProxy _proxy;
    private readonly IAutoStartOps _autoStart;
    private readonly IElevationOps _elevationOps;

    private CoreOrchestrator? _orch;
    private IPlatformPolicy? _platform;
    private TrayPresenter? _presenter;
    private ITrayView? _view;
    private PanelStaticServer? _panels;
    private CancellationTokenSource? _provisionCts;
    private bool _disposed;
    public int PanelPort => _panels?.ActualPort ?? 0;
    public CoreOrchestrator Orchestrator => _orch ?? throw new InvalidOperationException("Host not started");
    public IPlatformPolicy Platform => _platform ?? throw new InvalidOperationException("Host not started");
    public ISettingsStore Store => _store;
    public bool StartSilent { get; private set; }


    public AppHost(
        ISingleInstanceGuard? guard = null,
        IActivationBridge? bridge = null,
        IElevationGate? elevationGate = null,
        IDispatcher? dispatcher = null,
        ISettingsStore? store = null,
        IConfigComposer? composer = null,
        CoreRuntime? runtime = null,
        IProfileWatcher? watcher = null,
        ITimeSource? time = null,
        Func<string, string, IMihomoApiClient>? apiFactory = null,
        ISystemProxy? proxy = null,
        IAutoStartOps? autoStart = null,
        IElevationOps? elevationOps = null)
    {
        _guard = guard ?? new MutexSingleInstanceGuard();
        _bridge = bridge ?? new MmfEventActivationBridge();
        _elevationGate = elevationGate ?? new ElevationGate(elevationOps);
        _dispatcher = dispatcher ?? new DispatcherQueueAdapter(DispatcherQueue.GetForCurrentThread());
        _store = store ?? new FileSettingsStore();
        _composer = composer ?? new DefaultConfigComposer();
        _runtime = runtime ?? new CoreRuntime();
        _watcher = watcher ?? new FileProfileWatcher();
        _time = time ?? new SystemTimeSource();
        _apiFactory = apiFactory ?? ((addr, secret) => new MihomoApiClient(addr, secret));
        _proxy = proxy ?? new SystemProxyAdapter();
        _autoStart = autoStart ?? new AutoStartAdapter();
        _elevationOps = elevationOps ?? new ElevationAdapter();
    }

    public bool AcquireSingleInstance() => _guard.Acquire();

    public void ForwardActivation() => _bridge.Forward();

    public bool Start(HostStartArgs args)
    {
        AppLog.Info($"启动：pid={Environment.ProcessId} silentArg={args.HasSilentArg} elevated={_elevationGate.IsElevated}");
        var earlyStore = _store;
        var earlySettings = earlyStore.Load();
        if (earlySettings.TunEnabled && !_elevationGate.IsElevated)
        {
            AppLog.Info("TUN 模式需要管理员权限，正在以管理员身份重新启动…");
            if (_elevationGate.RelaunchElevated())
            {
                return false;
            }
            earlySettings.TunEnabled = false;
            earlyStore.Save(earlySettings);
            AppLog.Info("已取消提权，TUN 已临时关闭，下次可在托盘中重新开启");
        }
        _orch = new CoreOrchestrator(_store, _composer, _runtime, _watcher, _time, _apiFactory, earlySettings);
        _platform = new PlatformIntegration(_store, _proxy, _autoStart, _elevationOps, () => _orch.IsCoreRunning);
        _orch.Initialize();
        _platform.BindSettings(_orch.Settings);
        _orch.Notification += msg => _dispatcher.TryEnqueue(() => App.ShowGlobalNotification(msg));
        _orch.CrashLoop += count => _dispatcher.TryEnqueue(() => App.ShowGlobalNotification($"核心连续异常退出（订阅 provider 拉取失败时会出现），请在面板日志页查看详情 ({count})"));
        _orch.ElevationRequested += () => _dispatcher.TryEnqueue(Shutdown);
        _platform.Notification += msg => _dispatcher.TryEnqueue(() => App.ShowGlobalNotification(msg));
        _orch.StateChanged += state => _platform.OnCoreStateChanged(state.CoreState);
        _platform.ReconcileOnStartup(Environment.ProcessPath ?? "");
        StartPanelServer();
        _orch.StartOnLaunch();
        StartPanelProvisioning();
        _bridge.StartWatcher(() => _dispatcher.TryEnqueue(args.ShowWindow));
        StartSilent = args.HasSilentArg || _orch.Settings.SilentStart;
        if (!StartSilent) args.ShowWindow();
        StartTrayWithRetry(args);
        return true;
    }

    private void StartPanelServer()
    {
        var server = new PanelStaticServer();
        if (server.Start())
        {
            _panels = server;
            if (_composer is DefaultConfigComposer defaults)
                defaults.PanelPort = server.ActualPort;
            return;
        }
        try { server.Dispose(); } catch { }
        AppLog.Error("本地面板服务启动失败，已回退旧 /ui 链路");
        _dispatcher.TryEnqueue(() => App.ShowGlobalNotification("本地面板服务启动失败，已回退旧面板链路"));
    }

    private void StartPanelProvisioning()
    {
        var orch = _orch;
        if (orch is null)
            return;
        _provisionCts?.Cancel();
        _provisionCts?.Dispose();
        var cts = new CancellationTokenSource();
        _provisionCts = cts;
        var ct = cts.Token;
        _ = Task.Run(async () =>
        {
            var delays = new[] { 30, 60, 120, 300 };
            for (var attempt = 0; ; attempt++)
            {
                if (ct.IsCancellationRequested)
                    return;
                Action<string>? notify = attempt == 0
                    ? msg => _dispatcher.TryEnqueue(() => App.ShowPanelNotification(msg))
                    : null;
                IReadOnlyList<string> available;
                try
                {
                    available = await PanelProvisioner.EnsureAllAsync(notify, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    AppLog.Error("面板后台供应失败", ex);
                    available = PanelProvisioner.GetAvailable();
                }
                try
                {
                    _dispatcher.TryEnqueue(() => orch.NotifyPanelsChanged());
                }
                catch
                {
                }
                if (available.Count >= PanelCatalog.All.Count)
                    return;
                var delaySeconds = delays[Math.Min(attempt, delays.Length - 1)];
                try
                {
                    await _time.Delay(TimeSpan.FromSeconds(delaySeconds), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }, ct);
    }

    private void StartTrayWithRetry(HostStartArgs args)
    {
        _ = StartTrayWithRetryAsync(args);
    }

    private async Task StartTrayWithRetryAsync(HostStartArgs args)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var view = new WinUiTrayView(args.ShowWindow, args.ToggleWindow, args.IconPath, cmd => { });
                var presenter = new TrayPresenter(view, _orch!, _platform!, _dispatcher, args.ShowWindow, Shutdown, msg => _dispatcher.TryEnqueue(() => App.ShowGlobalNotification(msg)));
                presenter.Start();
                if (_disposed)
                {
                    presenter.Dispose();
                    view.Dispose();
                    return;
                }
                _view = view;
                _presenter = presenter;
                return;
            }
            catch (Exception ex)
            {
                AppLog.Error($"托盘初始化失败（第 {attempt + 1} 次），稍后重试", ex);
                if (attempt >= 11) break;
                try { await Task.Delay(5000); } catch { return; }
            }
        }
        AppLog.Error("托盘初始化失败，核心继续运行（无托盘图标）");
    }

    public void Shutdown()
    {
        try
        {
            try { _guard.Dispose(); } catch { }
            try { _bridge.Dispose(); } catch { }
            ClearProxyIfOwned();
            Dispose();
        }
        catch (Exception ex)
        {
            AppLog.Error("退出清理失败", ex);
        }
        finally
        {
            Environment.Exit(0);
        }
    }

    private void ClearProxyIfOwned()
    {
        if (_orch is { } orch && (orch.Settings.SystemProxyEnabled || _proxy.IsSetTo(orch.Settings.MixedPort)))
        {
            try { _proxy.Clear(); } catch { }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _guard.Dispose(); } catch { }
        try { _bridge.Dispose(); } catch { }
        ClearProxyIfOwned();
        try { _provisionCts?.Cancel(); } catch { }
        try { _provisionCts?.Dispose(); } catch { }
        try { _presenter?.Dispose(); } catch { }
        try { _view?.Dispose(); } catch { }
        try { _panels?.Stop(); } catch { }
        try { _panels?.Dispose(); } catch { }
        try { _orch?.Stop(); } catch { }
        try { _orch?.Dispose(); } catch { }
        try { _runtime.Dispose(); } catch { }
    }
}
