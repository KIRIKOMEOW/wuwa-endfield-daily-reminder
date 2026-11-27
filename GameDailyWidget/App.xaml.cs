using System;
using System.Collections.Generic;
using System.Drawing;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using EndfieldDesktopWidget;

namespace GameDailyWidget;

public partial class App : Application
{
    public static bool IsPreview { get; private set; }
    private Mutex? _instance;
    private bool _ownsInstance;
    private Forms.NotifyIcon? _tray;
    private Icon? _icon;
    private MainWindow? _widget;
    private IGameController? _wuwa;
    private EndfieldController? _endfield;
    private bool _exiting;
    private DailyStreakTracker? _streaks;
    private EdgeAutoHideController? _edgeDock;
    private DispatcherTimer? _historyClock;
    private DailyResetRefreshCoordinator? _resetRefresh;
    private NativeDailyNotifications? _notifications;
    private LaunchReminderTimer? _launchReminderTimer;
    private UnifiedSettings? _settings;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly long _startedAt = Stopwatch.GetTimestamp();
    private readonly DateTimeOffset _startedAtUtc = DateTimeOffset.UtcNow;
    private EventWaitHandle? _showInstanceEvent;
    private RegisteredWaitHandle? _showInstanceWait;
    private string? _notificationGame;
    private bool _notificationActivated;
    private const string ShowInstanceEventName = "Local\\GameDailyWidget.ShowInstance.v1";

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var args = e.Args;
        using var network = args.Any(a => a is "--render-preview" or "--art-check" or "--integration-check")
            ? new PreviewNetworkCheck() : null;
        if (args.Contains("--art-check"))
        {
            IsPreview = true;
            var output = Value(args,"--art-check");
            if (output == null) { Shutdown(2); return; }
            try
            {
                var checks = await CheckRedesign();
                checks.Add(network!.Result());
                var failed = checks.Any(c => c.StartsWith("FAIL",StringComparison.Ordinal));
                WriteJson(output,new {status=failed ? "failed" : "passed",count=checks.Count,checks,network=network.Snapshot()});
                Shutdown(failed ? 1 : 0);
            }
            catch (Exception ex) { WriteJson(output,new {status="failed",assertion=ex.Message}); Shutdown(1); }
            return;
        }
        if (args.Contains("--event-check"))
        {
            IsPreview = true;
            var output = Value(args, "--event-check");
            if (output == null) { Shutdown(2); return; }
            try
            {
                var checks = EventChecks.Run().Concat(EventChecks.Progress()).Concat(EventChecks.Reminder()).Concat(await EventChecks.RunStorageAsync()).ToList();
                WriteJson(output, new { status = "passed", count = checks.Count, checks });
                Shutdown();
            }
            catch (Exception ex) { WriteJson(output, new { status = "failed", assertion = ex.Message }); Shutdown(1); }
            return;
        }
        if (args.Contains("--event-sync-probe"))
        {
            var output = Value(args, "--event-sync-probe");
            if (args.Length != 2 || output == null || File.Exists(output)) { Shutdown(2); return; }
            try
            {
                using var feed = new EventFeed();
                var store = new EventStore();
                var sync = new EventSyncCoordinator(store, feed, _lifetime.Token);
                await sync.SyncAsync(DateTimeOffset.UtcNow.ToOffset(EventParser.China));
                var fullPath = Path.GetFullPath(output);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                using var stream = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write);
                JsonSerializer.Serialize(stream, store.Data, EventStore.Json);
                Shutdown(store.Data.Sync.Values.All(v => v.Error == null) ? 0 : 1);
            }
            catch { Shutdown(1); }
            return;
        }
        if (args.Contains("--event-notification-probe"))
        {
            IsPreview = true;
            var output = Value(args, "--event-notification-probe");
            var verifyTag = Value(args, "--verify-tag");
            if (output == null || (args.Length != 2 && !(args.Length == 4 && args[2] == "--verify-tag" &&
                verifyTag != null && System.Text.RegularExpressions.Regex.IsMatch(verifyTag, @"^evt-[0-9a-fA-F]{12}$"))))
            { Shutdown(2); return; }
            try { await RunEventNotificationProbe(output,verifyTag); }
            catch (Exception ex) { WriteJson(output, new { status = "failed", errorType=ex.GetType().Name, code=ex.HResult }); Shutdown(1); }
            return;
        }
        var render = Value(args, "--render-preview");
        var check = Value(args, "--integration-check");
        var resetCheck = Value(args, "--reset-window-check");
        var lifecycle = Value(args, "--lifecycle-check");
        var animationFrames = Value(args, "--animation-frame-check");
        var notificationProbe = Value(args, "--notification-probe");
        var previewFlag = args.Contains("--reminder-times-preview") || args.Contains("--render-preview") || args.Contains("--integration-check") || args.Contains("--reset-window-check") || args.Contains("--lifecycle-check") || args.Contains("--notification-probe") || args.Contains("--animation-frame-check");
        if (previewFlag && render == null && check == null && resetCheck == null && lifecycle == null && notificationProbe == null && animationFrames == null)
        {

            Shutdown(2);
            return;
        }
        IsPreview = previewFlag || args.Contains("--demo") || args.Contains("--smoke-test");
        if (!IsPreview && args.Any(a => a is "--preview-art" or "--preview-focus" or "--preview-zoom" or "--background-preview" or "--account-menu-preview" or
            "--preview-failure" or "--edge-strip-preview" or "--skin-layout-check" or "--guide-menu-preview" or "--guide-authors-preview" or "--overview-state" or "--editor-overview" or "--preview-new-art"))
        {
            Shutdown(2);
            return;
        }
        EventActivation? coldEvent = null;
        if (!IsPreview && Microsoft.Toolkit.Uwp.Notifications.ToastNotificationManagerCompat.WasCurrentProcessToastActivated())
        {
            try
            {
                (_notificationGame,coldEvent) = await ReadColdActivation();
                if (coldEvent?.Demo == true)
                {
                    IsPreview = true;
                    if (coldEvent.ClaimKey == null && !PreviewDisplay.SelectSecondary()) { Shutdown(); return; }
                    if (coldEvent.ClaimKey != null)
                    {
                        var memory = EventPreviewData.Create(DateTimeOffset.UtcNow,false,false,false);
                        var sample = memory.Data.Events.FirstOrDefault(e => e.Key == coldEvent.ClaimKey);
                        if (sample != null) memory.Mark(sample,true,false,DateTimeOffset.UtcNow);
                        Shutdown(); return;
                    }
                }
            }
            catch { Shutdown(1); return; }
        }
        if (IsPreview && args.Contains("--test-secondary") && !PreviewDisplay.SelectSecondary()) { Shutdown(2); return; }
        var demo = IsPreview && !args.Contains("--empty");
        _instance = new Mutex(true, IsPreview ? "Local\\GameDailyWidget.Preview." + Environment.ProcessId : "Local\\GameDailyWidget.v1", out _ownsInstance);
        if (!_ownsInstance)
        {
            if (coldEvent != null) await ForwardEvent(coldEvent);
            else if (!args.Contains("--startup")) SignalExistingInstance();
            Shutdown();
            return;
        }
        if (coldEvent?.ClaimKey is {} claimKey)
        {
            var store = new EventStore(EventsPath);
            var item = store.Data.Events.FirstOrDefault(e => e.Game == coldEvent.Game && e.Key == claimKey);
            if (item != null) store.Mark(item,true,store.Data.Marks.GetValueOrDefault(claimKey)?.Ignored == true,DateTimeOffset.UtcNow);
            Shutdown(); return;
        }
        DispatcherUnhandledException += (_, error) =>
        {
            error.Handled = true;
            var report = check ?? lifecycle ?? animationFrames ?? resetCheck;
            if (IsPreview && report != null) WriteJson(report,new { status="failed", assertion=error.Exception.Message });
            else if (!IsPreview) MessageBox.Show("组件遇到无法继续处理的问题，请重新打开。登录信息不会写入错误记录。", "双游戏日常组件");
            ExitApplication(1);
        };
        try
        {
            if (resetCheck != null)
            {
                var checks = new List<string>();
                checks.AddRange(await DailyResetRefreshChecks.RunAsync());
                checks.AddRange(await DailyResetCoordinatorChecks.RunAsync());
                checks.AddRange(await EndfieldResetWindowChecks.RunAsync());
                if (checks.Any(item => item.StartsWith("FAIL", StringComparison.Ordinal)))
                {
                    WriteJson(resetCheck, new { status = "failed", count = checks.Count, checks, realCredentialsRead = false,
                        realNetworkUsed = false, backendLatencyMeasured = false });
                    ExitApplication(1);
                    return;
                }
                WriteJson(resetCheck, new { status = "passed", count = checks.Count, checks, realCredentialsRead = false,
                    realNetworkUsed = false, backendLatencyMeasured = false });
                ExitApplication();
                return;
            }
            if (check != null)
            {
                var checks = new List<string>();
                checks.AddRange(await CheckRedesign());
                checks.AddRange(EventChecks.Run());
                checks.AddRange(EventChecks.Progress());
                checks.AddRange(EventChecks.Reminder());
                checks.AddRange(await EventChecks.RunStorageAsync());
                checks.AddRange(await WuwaControllerChecks.RunAsync());
                checks.AddRange(await EndfieldControllerChecks.RunAsync());
                checks.AddRange(await DailyStreakChecks.RunAsync());
                checks.AddRange(await EdgeDockChecks.RunAsync());
                checks.AddRange(await StartupChecks.RunAsync());
                checks.AddRange(await StartupDailyReminderChecks.RunAsync());
                checks.AddRange(PreResetDailyReminderChecks.Run());
                checks.AddRange(DailyReminderScheduleChecks.Run());
                checks.AddRange(await LaunchReminderDelayChecks.RunAsync());
                checks.AddRange(await LaunchReminderTimerChecks.RunAsync());
                checks.AddRange(await ReminderDelayInputChecks.RunAsync());
                checks.AddRange(await DailyResetRefreshChecks.RunAsync());
                checks.AddRange(await DailyResetCoordinatorChecks.RunAsync());
                checks.AddRange(await EndfieldResetWindowChecks.RunAsync());
                var previewSettings = UnifiedSettings.Load(true);
                checks.AddRange(await FloatingModeSettingsChecks.RunAsync());
                previewSettings.Save();
                checks.Add("Preview settings use temporary preferences and do not persist");
                checks.Add(network!.Result());
                if (checks.Any(item => item.StartsWith("FAIL", StringComparison.Ordinal)))
                {
                    WriteJson(check, new { status = "failed", count = checks.Count, checks, realCredentialsRead = false });
                    ExitApplication(1);
                    return;
                }
                WriteJson(check, new { status = "passed", count = checks.Count, checks, realCredentialsRead = false,network=network.Snapshot() });
                ExitApplication();
                return;
            }
            if (notificationProbe != null)
            {
                var requestedDelay = Value(args, "--notification-delay-seconds");
                var delaySeconds = LaunchReminderDelay.DefaultSeconds;
                if (requestedDelay != null && !ReminderDelayWindow.TryReadSeconds(requestedDelay, out delaySeconds))
                    throw new ArgumentException("Notification probe delay must be an integer from 1 to 86400 seconds.");
                await RunNotificationProbe(notificationProbe, delaySeconds);
                return;
            }
            if (IsPreview)
            {
                var example = TimeZoneInfo.FindSystemTimeZoneById(Value(args,"--preview-time-zone") ?? "UTC");
                _displayZone = () => example;
            }
            var settings = UnifiedSettings.Load(IsPreview);
            _settings = settings;
            if (!IsPreview)
            {
                _showInstanceEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowInstanceEventName);
                _showInstanceWait = ThreadPool.RegisterWaitForSingleObject(_showInstanceEvent,
                    (_, _) => QueueNotificationActivation(null), null, Timeout.Infinite, false);
                _notifications = new NativeDailyNotifications(QueueNotificationActivation);
                _notifications.DeliveryFailed += OnNotificationDeliveryFailed;
                // Register before the window so a toast can launch/activate this app.
                _notifications.RegisterActivation();
            }
            _wuwa = new WuwaController(settings, IsPreview, demo,_displayZone);
            _endfield = new EndfieldController(settings, IsPreview, demo,_displayZone);
            _resetRefresh = new DailyResetRefreshCoordinator(_wuwa, _endfield, IsPreview);
            _streaks = new DailyStreakTracker(IsPreview);
            _widget = new MainWindow(settings, _wuwa, _endfield, IsPreview,_displayZone);
            MainWindow = _widget;
            _widget.ReminderTestRequested += SendTestReminder;
            _widget.StartupReminderDelayChanged += StartOrUpdateLaunchReminderTimer;
            _wuwa.Changed += ObserveDailyData;
            _endfield.Changed += ObserveDailyData;
            _widget.FaceStateRendered += UpdateStreakDisplay;
            _historyClock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _historyClock.Tick += (_, _) => UpdateStreakDisplay();
            _historyClock.Start();
            AttachEvents(settings,args);
            var offscreen = render != null && Value(args,"--menu-preview") is (null or "settings") &&
                !args.Any(a => a is "--login-preview" or "--token-preview" or "--reminder-delay-preview");
            if (!offscreen)
            {
                if (PreviewDisplay.Screen is {} monitor)
                {
                    settings.Left = monitor.WorkingArea.Left + 80;
                    settings.Top = monitor.WorkingArea.Top + 50;
                    PreviewDisplay.Place(_widget);
                }
                _widget.Show();
            }
            _edgeDock = new EdgeAutoHideController(_widget, settings, IsPreview);
            _widget.DragStarted += _edgeDock.OnDragStarted;
            _widget.DragFinished += _edgeDock.OnDragFinished;
            _widget.GeometryChanged += _edgeDock.OnGeometryChanged;
            _widget.EdgeAutoHideChanged += _edgeDock.SetEnabled;
            if (render == null) BuildTray();
            if (coldEvent != null) QueueEventActivation(coldEvent);
            if (args.Contains("--startup") && !IsPreview && !_notificationActivated) _widget.Hide();
            if (!IsPreview) StartOrUpdateLaunchReminderTimer();
            var initialization = Task.WhenAll(InitializeSafely(_wuwa), InitializeSafely(_endfield));
            if (!IsPreview)
            {
                _preReset = new PreResetDailyReminder(_wuwa,_endfield,settings,SendPreResetReminder,_displayZone);
                _resetRefresh.ClockTick += _preReset.Tick;
                _widget.ReminderTimesChanged += _preReset.SettingsChanged;
                Microsoft.Win32.SystemEvents.TimeChanged += SystemTimeChanged;
                Microsoft.Win32.SystemEvents.PowerModeChanged += PowerChanged;
                _preReset.Tick(DateTimeOffset.UtcNow);
            }
            await initialization;
            UpdateStreakDisplay();
            if (_exiting) return;
            if ((_notificationGame ?? Value(args,"--face")) is {} face) _widget.ShowGame(face,false);
            if(IsPreview && args.Contains("--preview-new-art")) _widget.PreviewFraming(Value(args,"--preview-new-art")=="endfield"?"endfield":Value(args,"--face")??"wuwa");
            if (IsPreview && Value(args,"--overview-state") is {} scenario) _widget.SetOverviewPreview(scenario);
            if (IsPreview && int.TryParse(Value(args, "--preview-account"), out var previewAccount))
            {
                var controller = _widget.CurrentController;
                var choices = controller.State.Accounts;
                if (previewAccount > 0 && previewAccount <= choices.Count)
                    await controller.SelectAccountAsync(choices[previewAccount - 1].Key);
            }
            if (args.Contains("--pools")) _widget.OpenPools();
            if (args.Contains("--events")) _widget.OpenEvents();
            if (IsPreview && double.TryParse(Value(args,"--preview-scale"),System.Globalization.CultureInfo.InvariantCulture,out var previewScale))
                _widget.SetScale(previewScale);
            if (args.Contains("--details")) _widget.ToggleDetails();
            if (IsPreview && Value(args,"--preview-art") is {} artPath)
            {
                var full = Path.GetFullPath(artPath);
                if (Directory.Exists(full))
                    await _widget.SetOverviewPreviewArtAsync(Path.Combine(full,"wuwa-3.8.png"),Path.Combine(full,"endfield-5979.jpg"));
                else await _widget.SetPreviewArtAsync(full,ReadPreviewCrop(args));
            }
            if (IsPreview && args.Contains("--preview-failure")) _widget.SetPreviewFailure();
            if (animationFrames != null)
            {
                WriteJson(animationFrames, await EdgeAnimationFrameChecks.RunAsync(_widget, _edgeDock));
                ExitApplication();
            }
            else if (lifecycle != null)
            {
                await RunLifecycleCheck(lifecycle);
                ExitApplication();
            }
            else if (render != null)
            {
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                if (args.Contains("--background-preview"))
                    await _widget.RenderArtEditorAsync(render,args.Contains("--editor-overview"));
                else if (args.Contains("--guide-authors-preview"))
                {
                    var dialog = new GuideAuthorsWindow(settings.GuideAuthors);
                    var body = (FrameworkElement)dialog.Content;
                    dialog.Content = null;
                    var surface = new Border { Width=dialog.Width,Background=dialog.Background,Child=body };
                    System.Windows.Documents.TextElement.SetForeground(surface,dialog.Foreground);
                    System.Windows.Documents.TextElement.SetFontFamily(surface,dialog.FontFamily);
                    RenderOffscreen(surface,render);
                    dialog.Close();
                }
                else if (args.Contains("--reminder-times-preview"))
                {
                    var mode = Value(args,"--reminder-times-preview") ?? "default";
                    var dialog = new ReminderTimesWindow(mode switch
                    {
                        "default" or "error" => null, "custom" => ["09:00","14:30","21:00"], "off" => [],
                        _ => throw new ArgumentException("无效的提醒时间预览模式")
                    },() => DateTimeOffset.UtcNow,_displayZone);
                    if (mode == "error") dialog.PreviewError();
                    var body = (FrameworkElement)dialog.Content;
                    dialog.Content = null;
                    var surface = new Border { Width=dialog.Width, Background=dialog.Background, Child=body };
                    System.Windows.Documents.TextElement.SetForeground(surface,dialog.Foreground);
                    System.Windows.Documents.TextElement.SetFontFamily(surface,dialog.FontFamily);
                    System.Windows.Documents.TextElement.SetFontSize(surface,dialog.FontSize);
                    RenderOffscreen(surface,render);
                    dialog.Close();
                }
                else if (Value(args, "--menu-preview") is { } menuPreview)
                    await RenderSettingsMenu(menuPreview, render);
                else if (args.Contains("--reminder-delay-preview"))
                {
                    var dialog = new ReminderDelayWindow(LaunchReminderDelay.DefaultSeconds) { Owner = _widget };
                    PreviewDisplay.Place(dialog);
                    dialog.Show();
                    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    RenderWindow(dialog, render);
                    dialog.Close();
                }
                else if (args.Contains("--login-preview") || args.Contains("--token-preview"))
                {
                    using var client = new SklandClient();
                    var login = new LoginWindow(client) { SizeToContent = SizeToContent.Height };
                    if (args.Contains("--token-preview")) login.ShowTokenPanel();
                    PreviewDisplay.Place(login);
                    login.Show();
                    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    RenderWindow(login, render);
                    login.Close();
                }
                else
                {
                    _widget.RenderAll();
                    var card = (FrameworkElement)_widget.Content;
                    var size = new System.Windows.Size(_widget.Width,_widget.Height);
                    _widget.Content = null;
                    var surface = new Grid { Width=size.Width, Height=size.Height, UseLayoutRounding = _widget.UseLayoutRounding };
                    surface.Children.Add(card);
                    surface.Measure(size);
                    surface.Arrange(new Rect(size));
                    if (args.Contains("--account-menu-preview") || args.Contains("--guide-menu-preview"))
                    {
                        var menu = args.Contains("--guide-menu-preview") ? _widget.GuideMenuForPreview() : _widget.AccountMenuForPreview();
                        var anchor = menu.PlacementTarget as FrameworkElement;
                        var position = anchor != null && anchor.IsDescendantOf(surface)
                            ? anchor.TranslatePoint(new System.Windows.Point(0,anchor.ActualHeight),surface)
                            : new System.Windows.Point(22,124);
                        menu.Measure(new System.Windows.Size(double.PositiveInfinity,double.PositiveInfinity));
                        var overlay = new Canvas();
                        menu.Arrange(new Rect(menu.DesiredSize));
                        menu.UpdateLayout();
                        var layer = new System.Windows.Controls.Image { Width=menu.ActualWidth,Height=menu.ActualHeight,
                            Source=CaptureElement(menu) };
                        Canvas.SetLeft(layer, Math.Clamp(position.X,0,Math.Max(0,size.Width-menu.DesiredSize.Width)));
                        Canvas.SetTop(layer, Math.Clamp(position.Y,0,Math.Max(0,size.Height-menu.DesiredSize.Height)));
                        overlay.Children.Add(layer);
                        surface.Children.Add(overlay);
                        RenderOffscreen(surface,render);
                    }
                    else if (args.Contains("--pools") && args.Contains("--preview-pool-content"))
                        RenderOffscreen(_widget.PoolContentsForPreview(),render);
                    else if (args.Contains("--edge-strip-preview"))
                    {
                        _widget.SetCollapsedSkinStrip(true);
                        surface.UpdateLayout();
                        const double width = EdgeDockGeometry.DefaultStripDip;
                        var strip = new Border { Width=width,Height=size.Height, Background=new VisualBrush(surface)
                        {
                            ViewboxUnits=BrushMappingMode.Absolute,Viewbox=new Rect(0,0,width,size.Height),
                            Stretch=Stretch.Fill
                        }};
                        RenderOffscreen(strip,render);
                    }
                    else RenderElement(surface,render);
                    if (Value(args,"--skin-layout-check") is {} layout) WriteJson(layout,new {geometry=_widget.IsOverview ? _widget.CheckOverviewLayout() : _widget.CheckSkinLayout(),network=network!.Snapshot()});
                    if (Value(args,"--event-layout-check") is {} geometry) WriteJson(geometry,_widget.CheckEventLayout());
                }
                network!.AssertOffline();
                ExitApplication();
            }
            else if (args.Contains("--smoke-test"))
            {
                await Task.Delay(1500);
                ExitApplication();
            }
        }
        catch (Exception ex)
        {
            if (check != null) WriteJson(check, new { status = "failed", assertion = ex.Message });
            else if (resetCheck != null) WriteJson(resetCheck, new { status = "failed", assertion = ex.Message,
                realCredentialsRead = false, realNetworkUsed = false, backendLatencyMeasured = false });
            else if (lifecycle != null) WriteJson(lifecycle, new { status = "failed", assertion = ex.Message });
            else if (animationFrames != null) WriteJson(animationFrames, new { status = "failed", assertion = ex.Message,
                realCredentialsRead = false, personalPreferencesSaved = false });
            else if (notificationProbe != null) WriteJson(notificationProbe, new { status = "failed", errorType = ex.GetType().Name, code = ex.HResult,
                diagnostics = ex.StackTrace }); // This explicit probe uses only fabricated progress, never credentials.
            else if (render != null) WriteJson(render + ".error.json", new { status="failed", assertion=ex.Message });
            else MessageBox.Show("组件未能启动，请保留原程序并查看使用说明中的环境要求。", "双游戏日常组件");
            ExitApplication(1);
        }
    }

    private static async Task InitializeSafely(IGameController controller)
    {
        // Initialization failures are handled inside each controller, preserving the
        // other game's UI and timers. There is no shared account or request state.
        await controller.InitializeAsync();
    }

    private static void SignalExistingInstance()
    {
        try { using var signal = EventWaitHandle.OpenExisting(ShowInstanceEventName); signal.Set(); }
        catch { /* An exiting instance must not cause a duplicate-window dialog. */ }
    }

    private void QueueNotificationActivation(string? game)
    {
        if (Dispatcher.HasShutdownStarted) { SignalExistingInstance(); return; }
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_exiting) return;
            _notificationActivated = true;
            if (game is "wuwa" or "endfield") _notificationGame = game;
            ShowWidget();
            if (game is "wuwa" or "endfield") _widget?.ShowGame(game, false);
        }));
    }

    private void StartOrUpdateLaunchReminderTimer()
    {
        if (IsPreview || _exiting || _settings == null) return;
        _launchReminderTimer ??= new LaunchReminderTimer(_startedAt, SendLaunchReminder,
            _lifetime.Token, onError: () => _widget?.Note("启动后日常提醒未能发送，请检查 Windows 通知设置"));
        _launchReminderTimer.UpdateDelay(_settings.StartupReminderDelaySeconds);
    }

    private PreResetDailyReminder? _preReset;
    private Func<TimeZoneInfo> _displayZone = () => TimeZoneInfo.Local;

    private void SystemTimeChanged(object? sender, EventArgs e) => RefreshSystemClock();
    private void PowerChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode == Microsoft.Win32.PowerModes.Resume) RefreshSystemClock();
    }

    private void RefreshSystemClock()
    {
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_exiting) return;
            TimeZoneInfo.ClearCachedData();
            _preReset?.InvalidateSchedule();
            _preReset?.Tick(DateTimeOffset.UtcNow);
            _widget?.TimesDialog?.Refresh();
            _widget?.RenderAll();
        }));
    }

    private void SendLaunchReminder()
    {
        try
        {
            if (_exiting || _lifetime.IsCancellationRequested || _settings == null || _notifications == null || _wuwa == null || _endfield == null) return;
            var now = DateTimeOffset.UtcNow;
            if (!StartupDailyReminder.ShouldSend(IsPreview, _settings.StartupReminderEnabled)) return;
            var message = StartupDailyReminder.Compose(_wuwa.State, _endfield.State, now, force: true);
            if (message == null) return;
            var result = _notifications.Show(message with { Title = "今日日常提醒", Body = message.Body + "\n刷新：" + DailyReminderSchedule.ResetText(now,_displayZone()) }, now);
            if (!result.Submitted) { _widget?.Note(result.Status); return; }
        }
        catch (OperationCanceledException) { }
        catch { if (!_exiting) _widget?.Note("启动后日常提醒未能发送，请检查 Windows 通知设置"); }
    }

    private bool SendPreResetReminder(DailyReminderMessage message, DateTimeOffset now, DateTimeOffset slot)
    {
        var result = _notifications!.Show(message, now, tag: "pr-" + slot.ToUnixTimeSeconds().ToString("x"));
        if (!result.Submitted) _widget?.Note(result.Status);
        return result.Submitted;
    }

    private void SendTestReminder()
    {
        if (IsPreview || _exiting || _notifications == null || _wuwa == null || _endfield == null) return;
        var now = DateTimeOffset.UtcNow;
        var message = StartupDailyReminder.Compose(_wuwa.State, _endfield.State, now, force: true);
        if (message != null) _widget?.Note(_notifications.Show(message with { Title = "日常提醒测试", Body = message.Body + "\n刷新：" + DailyReminderSchedule.ResetText(now,_displayZone()) }, now, test: true).Status);
    }

    private void OnNotificationDeliveryFailed(string day)
    {
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_exiting) return;
            _widget?.Note("Windows 未能显示日常提醒，请检查系统通知设置");
        }));
    }

    private async Task RunEventNotificationProbe(string path, string? verifyTag)
    {
        using var notifications = new NativeDailyNotifications(_ => { });
        if (verifyTag != null)
        {
            var found = notifications.IsInOwnNotificationHistory(verifyTag,EventNotification.Group);
            var prior = new NativeNotificationResult(found, found ? "已在本应用 Windows 活动通知历史中核实" : "未找到对应活动通知");
            WriteJson(path,EventNotification.ProbeReport(prior,found,false,verifyTag,verificationOnly:true));
            Shutdown(found ? 0 : 1);
            return;
        }
        var failed = 0;
        notifications.EventDeliveryFailed += _ => Interlocked.Exchange(ref failed, 1);
        var tag = "evt-" + Guid.NewGuid().ToString("N")[..12];
        var now = DateTimeOffset.UtcNow.ToOffset(EventParser.China);
        var sample = EventPreviewData.Probe(now);
        var notice = new EventNotice("限时活动 · 剩不到 3 小时", $"鸣潮「{sample.Name}」{sample.End:MM/dd HH:mm} 结束",
            sample.Game, sample.Key, tag, sample.End!.Value);
        var result = notifications.ShowEvent(notice, demo: true);
        var inHistory = false;
        for (var i = 0; result.Submitted && i < 12 && !inHistory && failed == 0; i++)
        {
            await Task.Delay(200);
            inHistory = notifications.IsInOwnNotificationHistory(tag, EventNotification.Group);
        }
        var passed = result.Submitted && inHistory && failed == 0;
        WriteJson(path, EventNotification.ProbeReport(result, inHistory, failed != 0, tag));
        Shutdown(passed ? 0 : 1);
    }

    private async Task RunNotificationProbe(string path, int delaySeconds)
    {
        _notifications = new NativeDailyNotifications(_ => { });
        _notifications.RegisterActivation();
        await LaunchReminderDelay.WaitAsync(_startedAt, _lifetime.Token, delaySeconds);
        var now = DateTimeOffset.UtcNow;
        var message = StartupDailyReminder.Compose(
            new GameViewState { GameKey = "wuwa", LoggedIn = true, UpdatedAt = now, Daily = 80, MaxDaily = 100, DailyState = "incomplete" },
            new GameViewState { GameKey = "endfield", LoggedIn = true, UpdatedAt = now, Daily = 100, MaxDaily = 100, DailyState = "complete" }, now, true)!;
        var failed = 0;
        _notifications.DeliveryFailed += _ => Interlocked.Exchange(ref failed, 1);
        var probeTag = "probe-" + Guid.NewGuid().ToString("N")[..10];
        var submittedAfterMilliseconds = Stopwatch.GetElapsedTime(_startedAt).TotalMilliseconds;
        var result = _notifications.Show(message with { Title = $"{delaySeconds}秒日常提醒测试（演示）" }, now, test: true, tag: probeTag);
        var inHistory = false;
        if (result.Submitted)
        {
            for (var attempt = 0; attempt < 10 && !inHistory && failed == 0; attempt++)
            {
                await Task.Delay(200);
                inHistory = _notifications.IsInOwnNotificationHistory(probeTag);
            }
        }
        var passed = result.Submitted && inHistory && failed == 0 && submittedAfterMilliseconds >= delaySeconds * 1000d;
        WriteJson(path, new { status = passed ? "passed" : "failed", result.Submitted, notificationStatus = result.Status,
            nativeToastInOwnWindowsHistory = inHistory, deliveryFailed = failed != 0, realCredentialsRead = false,
            personalPreferencesSaved = false, testData = true, startedAtUtc = _startedAtUtc,
            sentAtUtc = now, submittedAfterMilliseconds, delayMilliseconds = delaySeconds * 1000, uniqueNotificationTag = probeTag });
        ExitApplication(passed ? 0 : 1);
    }

    private void ObserveDailyData()
    {
        if (_exiting || _streaks == null) return;
        if (_wuwa != null) _streaks.Observe(_wuwa.State);
        if (_endfield != null) _streaks.Observe(_endfield.State);
        UpdateStreakDisplay();
    }

    private void UpdateStreakDisplay()
    {
        if (_exiting || _widget == null || _streaks == null) return;
        _widget.SetStreakText(_streaks.Describe(_widget.CurrentController.State));
    }

    private async Task RenderSettingsMenu(string kind, string path)
    {
        if (kind is not ("settings" or "wuwa" or "wuwa-refresh" or "endfield" or "endfield-refresh"))
            throw new ArgumentException("菜单预览仅支持 settings、wuwa、wuwa-refresh、endfield、endfield-refresh。");
        _edgeDock?.Suspend();
        var menu = _widget!.CreateSettingsMenuForPreview();
        if (kind == "settings")
        {
            RenderOffscreen(menu,path);
            _edgeDock?.Resume();
            return;
        }
        menu.PlacementTarget = _widget;
        menu.IsOpen = true;
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        FrameworkElement target = menu;
        MenuItem Find(IEnumerable<object> items, string word) => items.OfType<MenuItem>().First(item => Convert.ToString(item.Header)?.Contains(word, StringComparison.Ordinal) == true);
        if (kind != "settings")
        {
            var word = kind is "wuwa" or "wuwa-refresh" ? "鸣潮" : "终末地";
            var item = Find(menu.Items.Cast<object>(), word);
            item.IsSubmenuOpen = true;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (kind.EndsWith("-refresh", StringComparison.Ordinal))
            {
                item = Find(item.Items.Cast<object>(), "更新间隔");
                item.IsSubmenuOpen = true;
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            }
            item.ApplyTemplate();
            var popup = item.Template.FindName("PART_Popup", item) as Popup;
            target = popup?.Child as FrameworkElement ?? throw new InvalidOperationException("Menu popup visual was not available.");
        }
        target.UpdateLayout();
        RenderElement(target, path);
        menu.IsOpen = false;
        _edgeDock?.Resume();
    }

    private async Task RunLifecycleCheck(string path)
    {
        var window = _widget!;
        var checks = new List<string>();
        Assert(window.IsOverview,"startup opens twin overview");
        var overviewWidth = window.Width;
        window.OpenGamePage("wuwa","pools");
        Assert(!window.IsOverview && window.Width == overviewWidth,"timeline navigation preserves width");
        window.ShowOverview();
        Assert(window.IsOverview && !window.DetailsExpanded && window.Width == overviewWidth,"back to overview clears panels");
        window.ShowGame("wuwa", false);
        Assert(window.CurrentGameKey == "wuwa", "front face");
        window.ShowGame("endfield", false);
        Assert(window.CurrentGameKey == "endfield", "back face");
        checks.Add("Both game faces display in the same window");
        void CheckButtons(bool details, bool events)
        {
            var expected = (details ? "详情 ˄" : "详情 ˅", details ? "收起当前游戏的详情" : "展开当前游戏的详情",
                events ? "活动 ˄" : "活动 ˅", events ? "收起当前游戏的活动" : "展开当前游戏的活动");
            Assert(window.PanelButtonsForCheck.Equals(((object,object,object,object))expected), "panel button labels and tooltips");
        }
        CheckButtons(false,false);
        window.ToggleDetails();
        Assert(window.DetailsExpanded, "expanded details");
        CheckButtons(true,false);
        window.ToggleDetails();
        Assert(!window.DetailsExpanded, "collapsed details");
        checks.Add("Secondary details expand and collapse");
        window.OpenEvents();
        CheckButtons(false,true);
        checks.Add("PASS 两个面板三种状态的按钮文字和提示");
        Assert(window.EventsOpenForCheck && _edgeDock!.InteractionBlockedForCheck, "events pause edge collapse");
        window.ToggleDetails();
        Assert(window.DetailsExpanded && !window.EventsOpenForCheck, "details replaces events");
        window.OpenEvents();
        foreach (var scale in new[] { .5, 1, 2.5 })
        {
            window.SetScale(scale);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var dpi = VisualTreeHelper.GetDpi(window);
            var area = Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(window).Handle).WorkingArea;
            Assert(window.ActualWidth * dpi.DpiScaleX <= area.Width + 1 && window.ActualHeight * dpi.DpiScaleY <= area.Height + 1,
                $"events fit screen at {scale} ({window.ActualWidth}×{window.ActualHeight} DIP, requested {window.Height}, DPI {dpi.DpiScaleY}, work area {area.Width}×{area.Height})");
        }
        window.ToggleDetails();
        window.ToggleDetails();
        window.SetScale(1);
        checks.Add("PASS 活动与详情互斥、活动暂停贴边，50% / 100% / 250% 请求适应工作区");
        foreach (var scale in new[] { .75, 1, 1.25, 1.5 }) window.SetScale(scale);
        window.SetScale(.83);
        Assert(Math.Abs(window.Width - 560 * .83) < 1, "continuous scale below preset range");
        window.SetScale(1.37);
        Assert(Math.Abs(window.Width - 560 * 1.37) < 1, "continuous non-preset scale");
        window.SetScale(1);
        checks.Add("Resize calculation accepts a range of sizes without menu presets");
        checks.Add("Continuous resize supports arbitrary sizes");
        window.Hide();
        window.ShowWidget();
        Assert(window.IsVisible, "tray restore");
        checks.Add("Floating window survives hide and restore");
        Assert(_edgeDock?.ReadNativeState().IsDesktopChild == false, "host always remains a top-level window");
        window.SetAlwaysOnTop(true);
        Assert(window.Topmost, "always on top");
        window.SetAlwaysOnTop(false);
        window.Close();
        Assert(!window.IsVisible && window.IsLoaded, "close hides without stopping controllers");
        window.ShowWidget();
        Assert(window.IsVisible, "restore after close");
        checks.Add("Close hides; explicit application exit owns controller disposal");
        for (var restore = 0; restore < 5; restore++)
        {
            window.Hide();
            window.ShowWidget();
            Assert(window.IsVisible && !(_edgeDock?.ReadNativeState().IsDesktopChild ?? true), "repeated floating restore");
        }
        checks.Add("Repeated hide and restore keeps a visible top-level window");
        var menuHeaders = window.CreateSettingsMenuForPreview().Items.OfType<MenuItem>().Select(item => Convert.ToString(item.Header));
        Assert(!menuHeaders.Any(header => header?.Contains("固定在桌面", StringComparison.Ordinal) == true), "removed settings entry");
        Assert(_tray?.ContextMenuStrip?.Items.Cast<Forms.ToolStripItem>().All(item => !(item.Text?.Contains("固定在桌面", StringComparison.Ordinal) ?? false)) == true,
            "removed tray entry");
        checks.Add("Settings and tray contain no desktop attachment entry");
        var simplifiedMenu = window.CreateSettingsMenuForPreview();
        string[] removedEntries = ["组件大小", "透明度", "设置游戏路径…", "启动游戏", "隐藏到托盘（继续更新）", "退出整个组件"];
        IEnumerable<MenuItem> WalkMenu(ItemCollection items)
        {
            foreach (var item in items.OfType<MenuItem>())
            {
                yield return item;
                foreach (var child in WalkMenu(item.Items)) yield return child;
            }
        }
        Assert(WalkMenu(simplifiedMenu.Items).All(item => !removedEntries.Contains(Convert.ToString(item.Header))), "removed settings and game actions");
        Assert(_tray!.ContextMenuStrip!.Items.Cast<Forms.ToolStripItem>().Any(item => item.Text == "退出程序"), "tray exit retained");
        Assert(!_tray.ContextMenuStrip.Items.Cast<Forms.ToolStripItem>().Any(item => item.Text == "组件大小"), "tray size entry removed");
        Assert(Math.Abs(window.Opacity - 1) < .001, "default window opacity");
        checks.Add("Simplified settings omit size, opacity, launch, hide and exit while tray exit remains");
        checks.AddRange(await ReminderDelayDialogChecks.RunAsync());
        window.ShowGame("wuwa", true);
        await Task.Delay(400);
        Assert(window.CurrentGameKey == "wuwa", "animated flip");
        checks.Add("Animated flip settles on the requested game");
        Assert(_wuwa!.State.GameKey == "wuwa" && _endfield!.State.GameKey == "endfield", "game isolation");
        checks.Add("Independent game identities remain separate through lifecycle operations");
        var endfield = _endfield!;
        var wuwa = _wuwa!;
        var endfieldStamina = endfield.State.Stamina;
        var endfieldRole = endfield.State.SelectedAccountKey;
        var wuwaChoices = wuwa.State.Accounts;
        if (wuwaChoices.Count > 1)
        {
            await wuwa.SelectAccountAsync(wuwaChoices[wuwaChoices.Count - 1].Key);
            await endfield.RefreshAsync();
            Assert(endfield.State.Stamina == endfieldStamina && endfield.State.SelectedAccountKey == endfieldRole,
                "one game failure cannot replace the other game data");
            window.ShowGame("endfield", false);
            Assert(window.CurrentGameKey == "endfield" && endfield.State.Stamina != null, "healthy face after other game failure");
            checks.Add("Wuwa invalid account leaves Endfield independently readable and refreshable");
        }
        Assert(_tray is { Visible: true }, "single host tray");
        checks.Add("One host owns the tray for both game controllers");
        if (_edgeDock != null)
        {
            _edgeDock.Suspend();
            foreach (var edge in new[] { "left", "right", "top", "bottom" })
            {
                _edgeDock.ProbeDock(edge, true);
                Assert(_edgeDock.IsCollapsed, "edge collapse " + edge);
                Assert(_edgeDock.ExpandedPosition != null, "expanded anchor " + edge);
                _edgeDock.Restore();
                Assert(!_edgeDock.IsCollapsed, "edge restore " + edge);
            }
            _edgeDock.Resume();
            checks.Add("All four screen edges collapse and restore with an expanded anchor");
            var nativeChecks = await EdgeDockChecks.RunWindowChecksAsync(window, _edgeDock);
            checks.AddRange(nativeChecks);
            checks.AddRange(await EdgeRevealChecks.RunAsync(window, _edgeDock));
            WriteJson(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "v1.2-edge-window-checks.json"),
                new { status = "passed", count = nativeChecks.Count, checks = nativeChecks, realMouseMoved = false });
        }
        window.ShowOverview();
        window.Hide();
        QueueNotificationActivation("endfield");
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Assert(window.IsVisible && !window.IsOverview && window.CurrentGameKey == "endfield", "notification callback restores target game");
        checks.Add("Notification activation callback restores the requested game through the dispatcher");
        window.ShowOverview();
        window.Hide();
        var sample = _eventStore!.Data.Events.First(e => e.Game == "wuwa");
        QueueEventActivation(new("wuwa",sample.Key,true));
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Assert(!window.IsVisible && _eventStore.Data.Marks[sample.Key].Claimed, "event claim stays hidden");
        QueueEventActivation(new("endfield",null,true));
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Assert(window.IsVisible && window.CurrentGameKey == "endfield" && window.EventsOpenForCheck, "event opens target panel");
        checks.Add("PASS 通知后台领取不显示窗口，打开活动切到目标游戏并展开面板");
        window.ShowGame("wuwa", false);
        window.Hide();
        QueueNotificationActivation(null);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Assert(window.IsVisible && window.CurrentGameKey == "wuwa", "generic duplicate launch preserves the current game");
        checks.Add("Generic instance restore keeps the current game after an earlier notification activation");
        WriteJson(path, new { status = "passed", count = checks.Count, checks, topLevelWindow = true, demo = true });
    }

    private void BuildTray()
    {
        _icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? "") ?? (Icon)SystemIcons.Application.Clone();
        _tray = new Forms.NotifyIcon { Icon = _icon, Text = "双游戏日常 · 鸣潮 / 终末地", Visible = true };
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowWidget);
        var menu = new Forms.ContextMenuStrip();
        void Add(string label, Action action) => menu.Items.Add(label, null, (_, _) => Dispatcher.Invoke(action));
        Add("显示组件", ShowWidget);
        Add("查看鸣潮", () => { ShowWidget(); _widget?.ShowGame("wuwa"); });
        Add("查看终末地", () => { ShowWidget(); _widget?.ShowGame("endfield"); });
        Add("查看限时活动", () => { ShowWidget(); _widget?.OpenEvents(); });
        Add("同步活动", () => { if (_eventSync != null) _ = _eventSync.SyncAsync(DateTimeOffset.UtcNow.ToOffset(EventParser.China)); });
        Add("刷新当前游戏", () => { if (_widget != null) _ = _widget.CurrentController.RefreshAsync(); });
        Add("刷新两个游戏", () => { if (_wuwa != null) _ = _wuwa.RefreshAsync(); if (_endfield != null) _ = _endfield.RefreshAsync(); });
        menu.Items.Add(new Forms.ToolStripSeparator());
        Add("鸣潮：重新扫描启动器账号", () => { if (_wuwa != null) _ = _wuwa.LoginAsync(null); });
        Add("终末地：登录 / 切换森空岛账号", () => { if (_endfield != null) _ = _endfield.LoginAsync(null); });
        Add("终末地：退出登录", () => _endfield?.Logout());
        menu.Items.Add(new Forms.ToolStripSeparator());
        var top = new Forms.ToolStripMenuItem("始终置顶");
        top.Click += (_, _) => Dispatcher.Invoke(() => _widget?.SetAlwaysOnTop(!(_widget?.Topmost ?? false)));
        menu.Items.Add(top);
        menu.Opening += (_, _) => { top.Checked = _widget?.Topmost ?? false; };
        Add("设置…", () => { ShowWidget(); _widget?.ShowSettings(); });
        var autoHide = new Forms.ToolStripMenuItem("贴边自动收起");
        autoHide.Click += (_, _) => Dispatcher.Invoke(() => _edgeDock?.SetEnabled(!autoHide.Checked));
        menu.Items.Add(autoHide);
        menu.Opening += (_, _) => { autoHide.Checked = _edgeDock?.Enabled ?? false; _edgeDock?.Suspend(); };
        menu.Closed += (_, _) => _edgeDock?.Resume();
        menu.Items.Add(new Forms.ToolStripSeparator());
        Add("退出程序", () => ExitApplication());
        DarkTrayTheme.Apply(menu);
        _tray.ContextMenuStrip = menu;
    }

    private void ShowWidget()
    {
        _edgeDock?.Restore();
        _widget?.ShowWidget();
    }

    private void ExitApplication(int exitCode = 0)
    {
        if (_exiting) return;
        _exiting = true;
        if (_preReset != null)
        {
            Microsoft.Win32.SystemEvents.TimeChanged -= SystemTimeChanged;
            Microsoft.Win32.SystemEvents.PowerModeChanged -= PowerChanged;
        }
        _lifetime.Cancel();
        _launchReminderTimer?.Dispose();
        _showInstanceWait?.Unregister(null);
        _showInstanceEvent?.Dispose();
        _notifications?.Dispose();
        _historyClock?.Stop();
        _resetRefresh?.Dispose();
        _eventFeed?.Dispose();
        _artCache?.Dispose();
        _edgeDock?.Restore();
        _widget?.SavePosition();
        _edgeDock?.Dispose();
        _wuwa?.Dispose();
        _endfield?.Dispose();
        if (_tray != null) { _tray.Visible = false; _tray.ContextMenuStrip?.Dispose(); _tray.Dispose(); _tray = null; }
        _widget?.PermitClose();
        _widget?.Close();
        Shutdown(exitCode);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _icon?.Dispose();
        if (_ownsInstance) _instance?.ReleaseMutex();
        _instance?.Dispose();
        base.OnExit(e);
    }

    private static string? Value(string[] args, string flag)
    {
        var index = Array.IndexOf(args, flag);
        return index >= 0 && index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal) ? args[index + 1] : null;
    }

    private static void WriteJson(string path, object value)
    {
        var absolute = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllText(absolute, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    }

    internal static void RenderWindow(Window window, string path)
        => RenderElement(window, path);

    // Mirrors the editor's normalized focal point and cover-relative zoom using isolated preview settings.
    private static ArtCrop? ReadPreviewCrop(string[] args)
    {
        var focus = Value(args,"--preview-focus");
        var magnification = Value(args,"--preview-zoom");
        if (focus == null && magnification == null) return null;
        var coordinates = (focus ?? "0.5,0.3").Split(',');
        static double Number(string s) => double.Parse(s,System.Globalization.CultureInfo.InvariantCulture);
        if (coordinates.Length != 2) throw new ArgumentException("预览焦点需要 x,y 两个归一化坐标");
        return new ArtCrop(Number(coordinates[0]),Number(coordinates[1]),magnification == null ? ArtCrop.DefaultZoom : Number(magnification)).Clamp();
    }

    private static async Task<List<string>> CheckRedesign()
    {
        var checks = new List<string>(VersionCaption.Check());
        checks.AddRange(await OfficialArtSourceChecks.RunAsync());
        checks.AddRange(await ArtChecks.RunAsync());
        checks.AddRange(SkinChecks.Run());
        checks.AddRange(GuideLinks.Check());
        checks.AddRange(TwinChecks.Run());
        checks.Add(ArtFramingNotice.Check());
        return checks;
    }

    private static void RenderOffscreen(FrameworkElement element,string path)
    {
        element.Measure(new System.Windows.Size(double.PositiveInfinity,double.PositiveInfinity));
        element.Arrange(new Rect(element.DesiredSize));
        RenderElement(element,path);
    }

    private static RenderTargetBitmap CaptureElement(FrameworkElement window)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * 2), (int)Math.Ceiling(window.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(window);
        return bitmap;
    }

    private static void RenderElement(FrameworkElement window, string path)
    {
        var bitmap = CaptureElement(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var absolute = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        using var output = File.Create(absolute);
        encoder.Save(output);
    }

    private static void Assert(bool condition, string check)
    {
        if (!condition) throw new InvalidOperationException("Lifecycle assertion: " + check);
    }
}
