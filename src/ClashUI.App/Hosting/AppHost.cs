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
    private bool _disposed;
    public CoreOrchestrator Orchestrator => _orch ?? throw new InvalidOperationException("Host not started");
    public IPlatformPolicy Platform => _platform ?? throw new InvalidOperationException("Host not started");
    public ISettingsStore Store => _store;
    public PolicyOps LegacyPolicy { get; private set; } = null!;
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

        _orch = new CoreOrchestrator(_store, _composer, _runtime, _watcher, _time, _apiFactory);
        _platform = new PlatformIntegration(_store, _proxy, _autoStart, _elevationOps, () => _orch.IsCoreRunning);
        _orch.Initialize();
        _platform.BindSettings(_orch.Settings);
        LegacyPolicy = new PolicyOps(_store, _proxy, _autoStart, _elevationOps);
        LegacyPolicy.BindSettings(_orch.Settings);
        _platform.ReconcileOnStartup(Environment.ProcessPath ?? "");

        _orch.Notification += msg => _dispatcher.TryEnqueue(() => App.ShowGlobalNotification(msg));
        _orch.CrashLoop += count => _dispatcher.TryEnqueue(() => App.ShowGlobalNotification($"核心连续异常退出（订阅 provider 拉取失败时会出现），请在面板日志页查看详情 ({count})"));
        _platform.Notification += msg => _dispatcher.TryEnqueue(() => App.ShowGlobalNotification(msg));
        _orch.StateChanged += state => _platform.OnCoreStateChanged(state.CoreState);


        _bridge.StartWatcher(() => _dispatcher.TryEnqueue(args.ShowWindow));

        StartSilent = args.HasSilentArg || _orch.Settings.SilentStart;

        _view = new WinUiTrayView(args.ShowWindow, args.ToggleWindow, args.IconPath, cmd => { });
        _presenter = new TrayPresenter(_view, _orch, _platform, _dispatcher, args.ShowWindow);
        _presenter.Start();

        if (!StartSilent) args.ShowWindow();
        _orch.StartOnLaunch();
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _presenter?.Dispose(); } catch { }
        try { _view?.Dispose(); } catch { }
        try { _orch?.Stop(); } catch { }
        try { _orch?.Dispose(); } catch { }
        try { _runtime.Dispose(); } catch { }
        try { _guard.Dispose(); } catch { }
        try { _bridge.Dispose(); } catch { }
    }
}
