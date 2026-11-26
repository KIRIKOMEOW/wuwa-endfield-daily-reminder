using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace GameDailyWidget;

public sealed partial class MainWindow : Window
{
    private static readonly Brush TextColor = Paint("#F2F0E9");
    private static readonly Brush Muted = Paint("#999AA3");
    private static readonly Brush Surface = Paint("#303138");
    private static readonly Brush Gold = Paint("#D6BB87");
    private static readonly Brush Warning = Paint("#EEAE95");
    private const double CardWidth = 560;
    private const double CompactHeight = 372;
    private readonly UnifiedSettings _settings;
    private readonly IGameController _wuwa;
    private readonly IGameController _endfield;
    private readonly bool _preview;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Dictionary<string, string> _localNotes = new();
    private readonly HashSet<string> _actions = new();
    private readonly Border _card = new();
    private readonly Grid _layout = new();
    private readonly Border _rail = new();
    private readonly TextBlock _title = Label("鸣潮", 15, Gold, FontWeights.SemiBold);
    private readonly TextBlock _demoMarker = Label("演示", 9, Muted);
    private readonly Button _account = MakeButton("", "账号操作");
    private readonly Button _login = MakeButton("扫描账号", "识别启动器中的账号");
    private readonly TextBlock _staminaLabel = Label("结晶波片", 11, Muted);
    private readonly TextBlock _stamina = Label("—", 41, TextColor, FontWeights.SemiBold);
    private readonly TextBlock _staminaMax = Label("/ 未知", 13, Muted);
    private readonly TextBlock _recovery = Label("恢复时间未知", 10, Muted);
    private readonly TextBlock _daily = Label("—", 27, TextColor, FontWeights.SemiBold);
    private readonly TextBlock _dailyMax = Label("/ 未知", 12, Muted);
    private readonly TextBlock _dailyState = Label("状态未知", 10, Muted);
    private readonly TextBlock _status = Label("等待同步", 10, Muted);
    private readonly TextBlock _streak = Label("", 10, Muted);
    private readonly Grid _progressTrack = new() { Height = 4, Background = Paint("#484950"), Margin = new Thickness(0, 6, 0, 0) };
    private readonly Border _progress = new() { Height = 4, HorizontalAlignment = HorizontalAlignment.Left, Background = Gold };
    private readonly Border _detailsPanel = new();
    private readonly StackPanel _details = new();
    private readonly ScrollViewer _detailScroll = new();
    private readonly List<(TextBlock Text, DetailLine Detail)> _detailTimers = new();
    private readonly Button _flip = MakeButton("鸣潮", "切换到鸣潮");
    private readonly Button _expand = MakeButton("详情 ˅", "展开当前游戏的详情");
    private readonly Button _refresh = MakeButton("刷新", "刷新当前游戏的数据");
    private readonly ScaleTransform _flipTransform = new(1, 1);
    private readonly ScaleTransform _sizeTransform = new(1, 1);
    private readonly Thumb _resizeHandle = new();
    private string _currentGame;
    private bool _expanded;
    private bool _overviewOpen = true;
    internal bool IsOverview => _overviewOpen;
    private Grid _singlePage = null!, _overviewPage = null!;
    private bool _allowClose;
    private bool _closed;
    private bool _flipping;
    private long _flipRevision;
    private Point? _dragStart;
    private (int X, int Y) _dragWindowStart;
    private GameViewState? _renderedState;
    private bool _resizing;
    private double _resizeStartScale;
    private Point _resizeMouseStart;
    private (int Width, int Height) _resizeStartSize;
    private ContextMenu? _settingsMenu;
    private bool _reminderDelayDialogActive;
    private long _geometryRevision;

    public string CurrentGameKey => _currentGame;
    public IGameController CurrentController => _currentGame == "endfield" ? _endfield : _wuwa;
    public bool DetailsExpanded => _expanded;
    public bool AllowOffscreenForAutoHide { get; set; }
    public Func<(int X, int Y)?>? ExpandedPositionProvider { get; set; }
    public bool IsAutoHideInteractionBlocked => _dragStart is not null || _flipping || _resizing || _resizeHandle.IsDragging;
    public bool IsInteractionActive => _eventsOpen || _poolsOpen || _guideDialog != null || IsAutoHideInteractionBlocked || _settingsMenu?.IsOpen == true || _accountMenu?.IsOpen == true || _actions.Count > 0 || _reminderDelayDialogActive || TimesDialog != null || _artEditor != null;
    public ContextMenu? ActiveSettingsMenu => _settingsMenu;
    public event Action? FaceStateRendered;
    public event Action? DragStarted;
    public event Action? DragFinished;
    public event Action? GeometryChanged;
    public event Action<bool>? EdgeAutoHideChanged;
    public event Action? ReminderTestRequested;
    public event Action? StartupReminderDelayChanged;

    internal event Action<DateTimeOffset>? ReminderTimesChanged;
    internal ReminderTimesWindow? TimesDialog { get; private set; }
    private readonly Func<TimeZoneInfo> _displayZone;
    public MainWindow(UnifiedSettings settings, IGameController wuwa, IGameController endfield, bool preview, Func<TimeZoneInfo>? displayZone = null, Action<string>? copyText = null)
    {
        _displayZone = displayZone ?? (() => preview ? TimeZoneInfo.Utc : TimeZoneInfo.Local);
        _settings = settings;
        _wuwa = wuwa;
        _endfield = endfield;
        _preview = preview;
        _copyText = copyText ?? Clipboard.SetText;
        _currentGame = settings.CurrentGame == "endfield" ? "endfield" : "wuwa";
        Title = "双游日常";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        ShowInTaskbar = false;
        Background = Brushes.Transparent;
        FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        Content = BuildUi();
        ApplySize();
        _wuwa.Changed += OnWuwaChanged;
        _endfield.Changed += OnEndfieldChanged;
        _clock.Tick += (_, _) => RenderTimes();
        _account.Click += (_, _) => ShowAccountMenu();
        _account.ContextMenuOpening += (_, e) => { e.Handled = true; ShowAccountMenu(); };
        _login.Click += async (_, _) => await RunActionAsync(CurrentController, c => c.LoginAsync(this), previewBlock: true);
        _refresh.Click += async (_, _) => await RunActionAsync(CurrentController, c => c.RefreshAsync());
        _flip.Click += (_, _) => ShowGame("wuwa");
        _efSwitch.Click += (_, _) => ShowGame("endfield");
        _expand.Click += (_, _) => ToggleDetails();
        _progressTrack.SizeChanged += (_, _) => RenderProgress();
        SizeChanged += (_, _) =>
        {
            if (IsLoaded && !_closed) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(ClampToWorkingArea));
        };
        Loaded += (_, _) =>
        {
            _settings.Scale = Math.Clamp(_settings.Scale, .5, MaximumScaleForScreen());
            ApplySize();
            RestorePosition();
            SetAlwaysOnTop(_settings.AlwaysOnTop);
            RenderState(force: true);
            _clock.Start();
        };
        Closing += OnClosing;
        Closed += OnClosed;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Hide(); e.Handled = true; } };
        PreviewMouseLeftButtonDown += OnDragStart;
        PreviewMouseMove += OnDragMove;
        PreviewMouseLeftButtonUp += OnDragEnd;
        LostMouseCapture += (_, _) => { if (_dragStart is not null) { _dragStart = null; ClampToWorkingArea(); DragFinished?.Invoke(); SavePosition(); } };
        RenderState(force: true);
    }

    private UIElement BuildUi()
    {
        _card.BorderThickness = new Thickness(1);
        _card.CornerRadius = new CornerRadius(14);
        _card.RenderTransform = _flipTransform;
        _card.RenderTransformOrigin = new Point(.5, .5);
        _card.LayoutTransform = _sizeTransform;
        var root = new Grid();
        root.SizeChanged += (_, _) => root.Clip = new RectangleGeometry(new Rect(root.RenderSize),13,13);
        _singlePage = BuildSkinLayout();
        _singlePage.Visibility = Visibility.Collapsed;
        root.Children.Add(_singlePage);
        _overviewPage = BuildOverview();
        root.Children.Add(_overviewPage);
        _edgeOverlay.Child = BuildTwinStrip();
        Panel.SetZIndex(_edgeOverlay,20);
        root.Children.Add(_edgeOverlay);
        _resizeHandle.Width = _resizeHandle.Height = 18;
        _resizeHandle.HorizontalAlignment = HorizontalAlignment.Right;
        _resizeHandle.VerticalAlignment = VerticalAlignment.Bottom;
        _resizeHandle.Margin = new Thickness(0, 0, 4, 2);
        _resizeHandle.Cursor = Cursors.SizeNWSE;
        _resizeHandle.ToolTip = "拖动右下角调整大小";
        _resizeHandle.Template = (ControlTemplate)XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Thumb">
              <Grid Background="Transparent"><Path x:Name="Grip" Stroke="#7A7E89" StrokeThickness="1.1" Data="M 4,13 L 13,4 M 9,14 L 14,9" HorizontalAlignment="Center" VerticalAlignment="Center" /></Grid><ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Grip" Property="Stroke" Value="#D6BB87" /></Trigger><Trigger Property="IsDragging" Value="True"><Setter TargetName="Grip" Property="Stroke" Value="#EEE4CE" /></Trigger></ControlTemplate.Triggers>
            </ControlTemplate>
            """);
        _resizeHandle.DragStarted += OnResizeStarted;
        _resizeHandle.DragDelta += OnResizeDelta;
        _resizeHandle.DragCompleted += (_, _) =>
        {
            _resizing = false;
            ClampToWorkingArea();
            DragFinished?.Invoke();
            SavePosition();
        };
        root.Children.Add(_resizeHandle);
        _card.Child = root;
        return _card;
    }

    internal void ShowOverview()
    {
        if (_closed || _overviewOpen) return;
        ++_flipRevision;
        _flipping = false;
        _flipTransform.BeginAnimation(ScaleTransform.ScaleXProperty,null);
        _flipTransform.ScaleX = 1;
        _efSwitch.IsEnabled = _flip.IsEnabled = true;
        _overviewOpen = true;
        _expanded = _poolsOpen = _eventsOpen = false;
        _singlePage.Visibility = Visibility.Collapsed;
        _overviewPage.Visibility = Visibility.Visible;
        ApplyPanel();
        RenderState(force:true);
    }

    internal void OpenGamePage(string game, string? panel = null)
    {
        ShowGame(game,false);
        if (panel == "events") OpenEvents();
        if (panel == "pools") OpenPools();
    }

    public void ShowGame(string key, bool animate = true)
    {
        if (key is not ("wuwa" or "endfield") || _closed) return;
        if (_overviewOpen)
        {
            _overviewOpen = false;
            _overviewPage.Visibility = Visibility.Collapsed;
            _singlePage.Visibility = Visibility.Visible;
            _appliedSkin = null;
            SelectFace(key);
            NotifyGeometryChanged();
            return;
        }
        if (key == _currentGame)
        {
            if (_flipping)
            {
                ++_flipRevision;
                _flipTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                _flipTransform.ScaleX = 1;
                _flipping = false;
                _efSwitch.IsEnabled = _flip.IsEnabled = true;
            }
            return;
        }
        var revision = ++_flipRevision;
        _flipping = animate && IsLoaded && IsVisible;
        _flipTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        if (!_flipping)
        {
            _flipTransform.ScaleX = 1;
            SelectFace(key);
            return;
        }
        _efSwitch.IsEnabled = _flip.IsEnabled = false;
        var compress = new DoubleAnimation(_flipTransform.ScaleX, .02, TimeSpan.FromMilliseconds(145))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        compress.Completed += (_, _) =>
        {
            if (_closed || revision != _flipRevision) return;
            SelectFace(key);
            var open = new DoubleAnimation(.02, 1, TimeSpan.FromMilliseconds(155))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            open.Completed += (_, _) =>
            {
                if (_closed || revision != _flipRevision) return;
                _flipTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                _flipTransform.ScaleX = 1;
                _flipping = false;
                _efSwitch.IsEnabled = _flip.IsEnabled = true;
                ClampToWorkingArea();
            };
            _flipTransform.BeginAnimation(ScaleTransform.ScaleXProperty, open);
        };
        _flipTransform.BeginAnimation(ScaleTransform.ScaleXProperty, compress);
    }

    private void SelectFace(string key)
    {
        _currentGame = key;
        _settings.CurrentGame = key;
        _renderedState = null;
        RenderState(force: true);
        SavePreferences();
    }

    public void ToggleDetails()
    {
        _expanded = _eventsOpen || _poolsOpen || !_expanded;
        _eventsOpen = _poolsOpen = false;
        RenderDetails(CurrentController.State);
        ApplyPanel();
    }

    public void ShowWidget()
    {
        if (_closed) return;
        Show();
        WindowState = WindowState.Normal;
        if (PreviewDisplay.Screen == null) Activate();
        ClampToWorkingArea();
    }

    public void SetAlwaysOnTop(bool enabled)
    {
        _settings.AlwaysOnTop = enabled;
        Topmost = enabled;
        SavePreferences();
        NotifyGeometryChanged();
    }

    public void SetScale(double scale)
    {
        if (scale > 10) scale /= 100;
        var anchor = IsLoaded ? FloatingWindowPlacement.GetScreenPosition(this) : ((int X, int Y)?)null;
        var allowed = MaximumScaleForScreen();
        _settings.Scale = double.IsFinite(scale) ? Math.Clamp(scale, .5, allowed) : 1;
        ApplySize();
        if (anchor is { } position) FloatingWindowPlacement.MoveToScreen(this, position.X, position.Y);
        ClampToWorkingArea();
        SavePreferences();
        NotifyGeometryChanged();
    }

    public void SetStreakText(string text)
    {
        _streak.Text = (text ?? "").Split(" · ",2)[0];
        _streakChip.ToolTip = text + "\n以每日活跃度达标为准，按游戏日、本机从首次观察开始记录";
    }
    public void RenderAll() => RenderState(force: true);

    private void OnWuwaChanged() => QueueRender("wuwa");
    private void OnEndfieldChanged() => QueueRender("endfield");
    private void QueueRender(string key)
    {
        if (_closed) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _localNotes.Remove(key);
            if (_closed) return;
            if (!_overviewOpen && key != _currentGame) { UpdateTwinStrip(); return; }
            RenderState();
        }));
    }

    private void RenderState(bool force = false)
    {
        if (_closed) return;
        if (_overviewOpen) { RenderTimes(); FaceStateRendered?.Invoke(); return; }
        var state = CurrentController.State;
        var endfield = _currentGame == "endfield";
        ApplyFaceSkin();
        _demoMarker.Visibility = _preview || state.Demo ? Visibility.Visible : Visibility.Collapsed;
        _demoMarker.Text = state.Demo ? "演示" : "预览";
        _accountName.Text = state.Nickname;
        var selected = state.Accounts.FirstOrDefault(a => a.Key == state.SelectedAccountKey)?.Label;
        _accountIdentity.Text = endfield && selected?.StartsWith(state.Nickname + " · ",StringComparison.Ordinal) == true
            ? selected[(state.Nickname.Length+3)..] : state.IdentityLabel;
        _account.ToolTip = $"{state.Nickname}  {state.IdentityLabel} · 账号操作";
        bool hasAccount = state.LoggedIn || state.Demo && state.Accounts.Count > 0;
        _account.Visibility = hasAccount ? Visibility.Visible : Visibility.Collapsed;
        _login.Visibility = hasAccount ? Visibility.Collapsed : Visibility.Visible;
        _staminaLabel.Text = state.StaminaLabel;
        _stamina.Text = Number(state.Stamina);
        _staminaMax.Text = "/ " + Number(state.MaxStamina, "未知");
        _daily.Text = Number(state.Daily);
        _dailyMax.Text = "/ " + Number(state.MaxDaily, "未知");
        _login.Content = endfield ? "扫码登录" : "扫描账号";
        _login.ToolTip = endfield ? "通过森空岛扫码登录或切换账号" : "重新识别鸣潮官方启动器中的账号";
        var pending = state.Busy || _actions.Contains(_currentGame);
        _login.IsEnabled = !pending;
        _refresh.IsEnabled = !pending && hasAccount;
        _refresh.Content = pending ? "更新中" : "刷新";
        if (force || _renderedState is null || !state.Details.SequenceEqual(_renderedState.Details))
        {
            RenderDetails(state);
            ApplySize();
        }
        _renderedState = state;
        RenderProgress();
        RenderTimes();
        FaceStateRendered?.Invoke();
    }

    private async Task SelectAccountAsync(AccountChoice choice)
    {
        if (_closed || string.IsNullOrWhiteSpace(choice.Key)) return;
        var controller = CurrentController;
        if (choice.Key == controller.State.SelectedAccountKey) return;
        _localNotes.Remove(controller.GameKey);
        try { await controller.SelectAccountAsync(choice.Key); }
        catch { _localNotes[controller.GameKey] = "账号切换未完成，请稍后重试"; }
        finally { if (!_closed && controller.GameKey == _currentGame) RenderState(); }
    }

    private async Task RunActionAsync(IGameController controller, Func<IGameController, Task> action, bool previewBlock = false)
    {
        var key = controller.GameKey;
        if (_closed || _actions.Contains(key)) return;
        if (_preview && previewBlock) { _localNotes[key] = "演示模式不会连接真实账号"; if (key == _currentGame) RenderTimes(); return; }
        _actions.Add(key);
        _localNotes.Remove(key);
        if (key == _currentGame) RenderState();
        try { await action(controller); }
        catch { _localNotes[key] = "操作未完成，请稍后重试"; }
        finally
        {
            _actions.Remove(key);
            if (!_closed && key == _currentGame) RenderState();
        }
    }

    private void RenderProgress()
    {
        var state = CurrentController.State;
        var known = state.Daily is not null && state.MaxDaily is > 0;
        _progressTrack.Visibility = known ? Visibility.Visible : Visibility.Hidden;
        _progress.Width = known ? _progressTrack.ActualWidth * Math.Clamp((double)state.Daily!.Value / state.MaxDaily!.Value, 0, 1) : 0;
    }

    private void RenderTimes()
    {
        if (_closed) return;
        RefreshFraming();
        UpdateTwinStrip();
        if (_overviewOpen) { UpdateOverview(DateTimeOffset.UtcNow); return; }
        var state = CurrentController.State;
        _recovery.Text = Recovery(state.Stamina, state.MaxStamina, state.FullAt);
        _recovery.Foreground = state.Stamina is { } amount && state.MaxStamina is > 0 && amount >= state.MaxStamina.Value ? FaceAccentText : FaceMuted;
        foreach (var (text, detail) in _detailTimers) text.Text = Remaining(detail.FullAt!.Value);
        RefreshSkinText(state, DateTimeOffset.UtcNow);
        RenderEvents();
        RenderPools();
    }

    private void RenderDetails(GameViewState state)
    {
        _details.Children.Clear();
        _detailTimers.Clear();
        foreach (var detail in state.Details)
        {
            var row = new Grid { Margin = new Thickness(0, 5, 0, 5) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.5, GridUnitType.Star) });
            var label = Label(detail.Label, 10, FaceMuted);
            label.VerticalAlignment = VerticalAlignment.Top;
            Trim(label);
            label.ToolTip = detail.Label;
            row.Children.Add(label);
            var values = new StackPanel();
            var value = Label(detail.Value, 11, FaceText);
            value.TextWrapping = TextWrapping.Wrap;
            values.Children.Add(value);
            if (detail.FullAt is { } target)
            {
                var time = Label(Remaining(target), 9, FaceMuted);
                time.Margin = new Thickness(0, 3, 0, 0);
                values.Children.Add(time);
                _detailTimers.Add((time, detail));
            }
            else if (!string.IsNullOrWhiteSpace(detail.Hint))
            {
                var hint = Label(detail.Hint, 9, FaceMuted);
                hint.TextWrapping = TextWrapping.Wrap;
                hint.Margin = new Thickness(0, 3, 0, 0);
                values.Children.Add(hint);
            }
            if (detail.Current is { } current && detail.Maximum is > 0)
            {
                var track = new Grid { Height = 3, Background = TrackBrush, Margin = new Thickness(0, 6, 0, 0) };
                var fill = new Border { Height = 3, Background = IsEndfield ? Ink : Gold, HorizontalAlignment = HorizontalAlignment.Left };
                var ratio = Math.Clamp((double)current / detail.Maximum.Value, 0, 1);
                track.SizeChanged += (_, _) => fill.Width = track.ActualWidth * ratio;
                track.Children.Add(fill);
                values.Children.Add(track);
            }
            Grid.SetColumn(values, 1);
            row.Children.Add(values);
            _details.Children.Add(row);
        }
        if (state.Details.Count == 0) _details.Children.Add(Label("暂无详情，请登录并更新数据", 10, FaceMuted));
        if (_currentGame == "endfield")
        {
            var community = PanelButton("打开森空岛", "打开森空岛官方网站");
            community.Margin = new Thickness(0, 7, 0, 0);
            community.HorizontalAlignment = HorizontalAlignment.Left;
            community.Click += (_, _) => OpenCommunity();
            _details.Children.Add(community);
        }
    }

    private void CopyIdentity()
    {
        var identity = CurrentController.State.CopyIdentity;
        if (string.IsNullOrWhiteSpace(identity)) return;
        try { _copyText(identity); Note(IsEndfield ? "角色 ID 已复制" : "特征码已复制"); }
        catch { Note("暂时无法复制，请重试"); }
    }

    public void ShowSettings()
    {
        _settingsMenu?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
        _settingsMenu = CreateSettingsMenuForPreview();
        _settingsMenu.PlacementTarget = _card;
        _settingsMenu.IsOpen = true;
    }

    public ContextMenu CreateSettingsMenuForPreview()
    {
        var menu = new ContextMenu { Background = Surface, Foreground = TextColor, FontSize = 11 };
        var top = new MenuItem { Header = "始终置顶", IsCheckable = true, IsChecked = _settings.AlwaysOnTop };
        top.Click += (_, _) => SetAlwaysOnTop(top.IsChecked);
        menu.Items.Add(top);
        var background = new MenuItem { Header = "更换背景…" };
        if (_overviewOpen)
        {
            foreach (var (key,title) in new[] { ("wuwa","鸣潮"), ("endfield","终末地") })
            {
                var game = new MenuItem { Header=title };
                game.Click += (_,_) => EditBackground(key);
                background.Items.Add(game);
            }
        }
        else background.Click += (_, _) => EditBackground();
        menu.Items.Add(background);
        var art = new MenuItem { Header="完成度影响立绘", IsCheckable=true, IsChecked=_settings.CompletionArtEnabled };
        art.Click += (_,_) => { _settings.CompletionArtEnabled = art.IsChecked; SavePreferences(); RefreshOverviewArt(); };
        menu.Items.Add(art);
        var authors = new MenuItem { Header="攻略博主…" };
        authors.Click += (_,_) => EditGuideAuthors();
        menu.Items.Add(authors);
        var autoHide = new MenuItem { Header = "贴边自动收起", IsCheckable = true, IsChecked = _settings.EdgeAutoHide };
        autoHide.Click += (_, _) => { _settings.EdgeAutoHide = autoHide.IsChecked; SavePreferences(); EdgeAutoHideChanged?.Invoke(autoHide.IsChecked); };
        menu.Items.Add(autoHide);
        menu.Items.Add(new Separator());
        menu.Items.Add(GameSettingsMenu(_wuwa, "鸣潮"));
        menu.Items.Add(GameSettingsMenu(_endfield, "终末地"));
        menu.Items.Add(new Separator());
        var startup = new MenuItem { Header = "开机启动", IsCheckable = true, IsChecked = _settings.StartWithWindows, IsEnabled = !_preview };
        startup.Click += (_, _) =>
        {
            if (!UnifiedSettings.SetStartup(startup.IsChecked)) { startup.IsChecked = _settings.StartWithWindows; Note("开机启动设置未保存，请稍后重试"); return; }
            _settings.StartWithWindows = startup.IsChecked;
            SavePreferences();
        };
        menu.Items.Add(startup);
        var reminder = new MenuItem { Header = "启动后日常提醒", IsCheckable = true, IsChecked = _settings.StartupReminderEnabled, IsEnabled = !_preview };
        reminder.Click += (_, _) => { _settings.StartupReminderEnabled = reminder.IsChecked; SavePreferences(); };
        menu.Items.Add(reminder);
        var reminderDelay = new MenuItem { Header = $"提醒延时：{_settings.StartupReminderDelaySeconds} 秒…", IsEnabled = !_preview };
        reminderDelay.Click += (_, _) => EditReminderDelay();
        menu.Items.Add(reminderDelay);
        var times = DailyReminderSchedule.DisplayTimes(_settings.DailyReminderTimes,DateTimeOffset.UtcNow,_displayZone());
        var editTimes = new MenuItem { Header="日常提醒时间：" + (times.Count == 0 ? "已关闭" : string.Join(" / ",times)) + "…", IsEnabled=!_preview };
        editTimes.ToolTip = "刷新：" + DailyReminderSchedule.ResetText(DateTimeOffset.UtcNow,_displayZone());
        editTimes.Click += (_,_) =>
        {
            if (_closed || TimesDialog != null) return;
            TimesDialog = new ReminderTimesWindow(_settings.DailyReminderTimes,() => DateTimeOffset.UtcNow,_displayZone) { Owner=this };
            try
            {
                if (TimesDialog.ShowDialog() != true) return;
                _settings.DailyReminderTimes = TimesDialog.Times;
                ReminderTimesChanged?.Invoke(DateTimeOffset.UtcNow);
            }
            finally { TimesDialog=null; }
        };
        menu.Items.Add(editTimes);
        var testReminder = new MenuItem { Header = "测试日常通知", IsEnabled = !_preview };
        testReminder.Click += (_, _) => ReminderTestRequested?.Invoke();
        menu.Items.Add(testReminder);
        menu.Items.Add(new Separator());
        var events = new MenuItem { Header="限时活动提醒", IsCheckable=true, IsChecked=_settings.EventReminderEnabled };
        events.Click += (_,_) => { _settings.EventReminderEnabled = events.IsChecked; SavePreferences(); };
        menu.Items.Add(events);
        var sync = new MenuItem { Header="立即同步活动", IsEnabled=!_preview };
        sync.Click += (_,_) => SyncEvents();
        menu.Items.Add(sync);
        DarkMenuTheme.Apply(menu);
        _settingsMenu = menu;
        return menu;
    }

    private void EditReminderDelay()
    {
        if (_closed || _reminderDelayDialogActive) return;
        _reminderDelayDialogActive = true;
        try
        {
            var dialog = new ReminderDelayWindow(_settings.StartupReminderDelaySeconds) { Owner = this };
            if (dialog.ShowDialog() != true) return;
            _settings.StartupReminderDelaySeconds = dialog.SelectedSeconds;
            SavePreferences();
            StartupReminderDelayChanged?.Invoke();
        }
        finally { _reminderDelayDialogActive = false; }
    }

    private MenuItem GameSettingsMenu(IGameController controller, string title)
    {
        var key = controller.GameKey;
        var game = new MenuItem { Header = title };
        var intervals = new MenuItem { Header = "自动更新间隔" };
        foreach (var minutes in new[] { 1, 5, 10 })
        {
            var item = new MenuItem { Header = $"{minutes} 分钟", IsCheckable = true, IsChecked = controller.State.RefreshMinutes == minutes };
            item.Click += (_, _) =>
            {
                if (key == "wuwa") _settings.Wuwa.RefreshMinutes = minutes; else _settings.Endfield.RefreshMinutes = minutes;
                controller.SetRefreshMinutes(minutes);
                SavePreferences();
                if (key == _currentGame) RenderState();
            };
            intervals.Items.Add(item);
        }
        game.Items.Add(intervals);
        if (key == "endfield")
        {
            var logout = new MenuItem { Header = "退出森空岛账号", IsEnabled = !_preview && controller.State.LoggedIn };
            logout.Click += (_, _) => { controller.Logout(); if (key == _currentGame) RenderState(); };
            game.Items.Add(logout);
        }
        return game;
    }

    private void OpenCommunity()
    {
        if (_preview) { Note("演示模式不会打开外部网站"); return; }
        try { Process.Start(new ProcessStartInfo("https://www.skland.com/") { UseShellExecute = true }); }
        catch { Note("暂时无法打开森空岛，请稍后重试"); }
    }

    internal void Note(string note, string? key = null) { var owner = key ?? _currentGame; _localNotes[owner] = note; if (owner == _currentGame) RenderTimes(); }
    private void SavePreferences() { if (!_preview) _settings.Save(); }

    private void ApplySize()
    {
        var oldWidth = Width;
        var oldHeight = Height;
        var scale = Math.Clamp(_settings.Scale, .5, 2.5);
        _sizeTransform.ScaleX = _sizeTransform.ScaleY = scale;
        _card.Width = CardWidth;
        var maxDetailHeight = 218d;
        var screen = IsLoaded ? Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle) : _preview ? PreviewDisplay.Screen : null;
        if (screen != null)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            maxDetailHeight = Math.Clamp(screen.WorkingArea.Height / dpi.DpiScaleY / scale - CompactHeight - 16, 38, 260);
        }
        var padding = _detailsPanel.Padding;
        var borderSize = _detailsPanel.BorderThickness;
        var margin = _detailsPanel.Margin;
        var panelChrome = padding.Top + padding.Bottom + borderSize.Top + borderSize.Bottom + margin.Top + margin.Bottom;
        _detailScroll.MaxHeight = Math.Max(1, maxDetailHeight - panelChrome);
        if (_expanded)
        {
            var border = _card.BorderThickness;
            // Apply child layout changes before measuring outside the normal WPF layout pass.
            _detailScroll.UpdateLayout();
            _detailsPanel.InvalidateMeasure();
            _layout.InvalidateMeasure();
            _layout.Measure(new Size(CardWidth - border.Left - border.Right, double.PositiveInfinity));
            _card.Height = _layout.DesiredSize.Height + border.Top + border.Bottom;
        }
        else _card.Height = CompactHeight;
        Width = CardWidth * scale;
        Height = _card.Height * scale;
        RefreshArt();
        if (IsLoaded && (Math.Abs(oldWidth - Width) > .01 || Math.Abs(oldHeight - Height) > .01)) NotifyGeometryChanged();
    }

    private double MaximumScaleForScreen()
    {
        if (!IsLoaded) return 2.5;
        var screen = Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle);
        var dpi = VisualTreeHelper.GetDpi(this);
        var compactLimit = screen.WorkingArea.Width / (CardWidth * dpi.DpiScaleX);
        var heightLimit = (screen.WorkingArea.Height - 12) / ((CompactHeight + (_expanded ? 38 : 0)) * dpi.DpiScaleY);
        return Math.Max(.5, Math.Min(2.5, Math.Min(compactLimit, heightLimit)));
    }

    private void OnResizeStarted(object sender, DragStartedEventArgs e)
    {
        if (_flipping || _closed) return;
        _resizing = true;
        DragStarted?.Invoke();
        _resizeStartScale = _settings.Scale;
        // Batched input can move the OS cursor ahead of this event. Convert the
        // WPF input position to physical pixels, matching GetWindowRect below.
        var cursor = PointToScreen(Mouse.GetPosition(this));
        _resizeMouseStart = new Point(cursor.X, cursor.Y);
        if (GetWindowRect(new WindowInteropHelper(this).Handle, out var rectangle))
            _resizeStartSize = (Math.Max(1, rectangle.Right - rectangle.Left), Math.Max(1, rectangle.Bottom - rectangle.Top));
        else
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            _resizeStartSize = ((int)Math.Max(1, Width * dpi.DpiScaleX), (int)Math.Max(1, Height * dpi.DpiScaleY));
        }
    }

    private void OnResizeDelta(object sender, DragDeltaEventArgs e)
    {
        if (!_resizing || _closed) return;
        // Sample the same input-coordinate stream used at drag start.
        var cursor = PointToScreen(Mouse.GetPosition(this));
        var dx = cursor.X - _resizeMouseStart.X;
        var dy = cursor.Y - _resizeMouseStart.Y;
        var width = (double)_resizeStartSize.Width;
        var height = (double)_resizeStartSize.Height;
        var factor = 1 + (dx * width + dy * height) / (width * width + height * height);
        SetScale(_resizeStartScale * factor);
        e.Handled = true;
    }

    private void NotifyGeometryChanged()
    {
        var revision = ++_geometryRevision;
        if (!IsLoaded) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (_closed || revision != _geometryRevision) return;
            UpdateLayout();
            GeometryChanged?.Invoke();
        }));
    }

    private void OnDragStart(object sender, MouseButtonEventArgs e)
    {
        if (_flipping || _resizing || e.ChangedButton != MouseButton.Left || IsInteractive(e.OriginalSource as DependencyObject)) return;
        DragStarted?.Invoke();
        _dragStart = PointToScreen(e.GetPosition(this));
        _dragWindowStart = FloatingWindowPlacement.GetScreenPosition(this);
        CaptureMouse();
        e.Handled = true;
    }

    private void OnDragMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
        var now = PointToScreen(e.GetPosition(this));
        FloatingWindowPlacement.MoveToScreen(this, _dragWindowStart.X + (int)Math.Round(now.X - start.X), _dragWindowStart.Y + (int)Math.Round(now.Y - start.Y));
        e.Handled = true;
    }

    private void OnDragEnd(object sender, MouseButtonEventArgs e)
    {
        if (_dragStart is not {} start) return;
        _dragStart = null;
        ReleaseMouseCapture();
        ClampToWorkingArea();
        DragFinished?.Invoke();
        SavePosition();
        if (_overviewOpen) TryOverviewClick(start,e);
        e.Handled = true;
    }

    public void SavePosition()
    {
        if (_preview || !IsLoaded) return;
        var position = ExpandedPositionProvider?.Invoke() ?? FloatingWindowPlacement.GetScreenPosition(this);
        _settings.Left = position.X;
        _settings.Top = position.Y;
        SavePreferences();
    }

    private void RestorePosition()
    {
        var screens = Forms.Screen.AllScreens;
        var primary = Forms.Screen.PrimaryScreen ?? screens[0];
        var dpi = VisualTreeHelper.GetDpi(this);
        var x = _settings.Left is { } savedX ? (int)Math.Round(savedX) : primary.WorkingArea.Right - (int)Math.Round(Width * dpi.DpiScaleX) - 30;
        var y = _settings.Top is { } savedY ? (int)Math.Round(savedY) : primary.WorkingArea.Top + 44;
        var titleHeight = 34 * dpi.DpiScaleY;
        var titleWidth = Width * dpi.DpiScaleX;
        var visible = screens.Any(screen =>
        {
            var a = screen.WorkingArea;
            var overlap = Rect.Intersect(new Rect(a.Left, a.Top, a.Width, a.Height), new Rect(x, y, titleWidth, titleHeight));
            return overlap.Width >= 90 * dpi.DpiScaleX && overlap.Height >= 22 * dpi.DpiScaleY;
        });
        if (!visible) { x = primary.WorkingArea.Right - (int)Math.Round(Width * dpi.DpiScaleX) - 30; y = primary.WorkingArea.Top + 44; }
        FloatingWindowPlacement.MoveToScreen(this, x, y);
        ClampToWorkingArea();
    }

    private void ClampToWorkingArea()
    {
        if (!IsLoaded || _closed || AllowOffscreenForAutoHide) return;
        UpdateLayout();
        var handle = new WindowInteropHelper(this).Handle;
        if (!GetWindowRect(handle, out var rectangle)) return;
        var position = FloatingWindowPlacement.GetScreenPosition(this);
        var screen = Forms.Screen.FromRectangle(new System.Drawing.Rectangle(position.X, position.Y, Math.Max(1, rectangle.Right - rectangle.Left), Math.Max(1, rectangle.Bottom - rectangle.Top)));
        var area = screen.WorkingArea;
        var width = rectangle.Right - rectangle.Left;
        var height = rectangle.Bottom - rectangle.Top;
        var x = width <= area.Width ? Math.Clamp(position.X, area.Left, area.Right - width) : area.Left;
        var y = height <= area.Height ? Math.Clamp(position.Y, area.Top, area.Bottom - height) : area.Top;
        if (x != position.X || y != position.Y) FloatingWindowPlacement.MoveToScreen(this, x, y);
    }

    public void PermitClose() => _allowClose = true;
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) { SavePosition(); return; }
        e.Cancel = true;
        Hide();
    }
    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        ++_flipRevision;
        _clock.Stop();
        _wuwa.Changed -= OnWuwaChanged;
        _endfield.Changed -= OnEndfieldChanged;
        _flipTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
    }

    private static bool IsInteractive(DependencyObject? source)
    {
        for (var current = source; current is not null;)
        {
            if (current is ButtonBase or Selector or TextBoxBase or Slider or Thumb or MenuItem or ScrollViewer) return true;
            current = current is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current);
        }
        return false;
    }

    private static string Number(int? value, string fallback = "—") => value?.ToString() ?? fallback;
    private string LocalTime(DateTimeOffset time) => DailyReminderSchedule.Local(time,_displayZone(),"MM-dd HH:mm");
    private static string Recovery(int? value, int? maximum, DateTimeOffset? target)
    {
        if (value is null || maximum is null) return "恢复时间未知";
        if (maximum > 0 && value >= maximum) return "已回满";
        return target is { } at ? Remaining(at) : "恢复时间未知";
    }
    private static string Remaining(DateTimeOffset target)
    {
        var seconds = (long)Math.Ceiling((target - DateTimeOffset.UtcNow).TotalSeconds);
        if (seconds <= 0) return "预计已回满 · 等待更新";
        var hours = seconds / 3600;
        var minutes = seconds % 3600 / 60;
        var remainingSeconds = seconds % 60;
        return $"回满还需 {hours}小时 {minutes:00}分 {remainingSeconds:00}秒";
    }
    private static Brush Paint(string value) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value)); b.Freeze(); return b; }
    private static TextBlock Label(string text, double size, Brush color, FontWeight? weight = null) => new()
    { Text = text, FontSize = size, Foreground = color, FontWeight = weight ?? FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center };
    private static void Add(Grid parent, UIElement child, int row) { Grid.SetRow(child, row); parent.Children.Add(child); }
    private static Button MakeButton(string content, string tooltip)
    {
        var button = new Button { Content = content, ToolTip = tooltip, Foreground = Gold, Background = Paint("#383941"), Padding = new Thickness(9, 4, 9, 4), FontSize = 10, BorderThickness = new Thickness(0), Cursor = Cursors.Hand };
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var disabled = new Trigger { Property = IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(OpacityProperty, .4));
        template.Triggers.Add(disabled);
        button.Template = template;
        return button;
    }
    private static Style PanelScrollBar(bool light) => (Style)XamlReader.Parse("""
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ScrollBar">
          <Setter Property="Width" Value="10" />
          <Setter Property="Background" Value="Transparent" />
          <Setter Property="Focusable" Value="False" />
          <Setter Property="Template">
            <Setter.Value>
              <ControlTemplate TargetType="ScrollBar">
                <Grid Background="{TemplateBinding Background}" Margin="0,2,0,2">
                  <Track x:Name="PART_Track" IsDirectionReversed="True" Orientation="{TemplateBinding Orientation}" Minimum="{TemplateBinding Minimum}" Maximum="{TemplateBinding Maximum}" ViewportSize="{TemplateBinding ViewportSize}" Value="{Binding Value, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}">
                    <Track.DecreaseRepeatButton>
                      <RepeatButton Command="{x:Static ScrollBar.PageUpCommand}" Focusable="False">
                        <RepeatButton.Template><ControlTemplate TargetType="RepeatButton"><Border Background="Transparent" /></ControlTemplate></RepeatButton.Template>
                      </RepeatButton>
                    </Track.DecreaseRepeatButton>
                    <Track.Thumb>
                      <Thumb MinHeight="24" Margin="2,0,2,0">
                        <Thumb.Template>
                          <ControlTemplate TargetType="Thumb">
                            <Border x:Name="Grip" Background="#5F626D" CornerRadius="3" />
                            <ControlTemplate.Triggers>
                              <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Grip" Property="Background" Value="#8B8E99" /></Trigger>
                              <Trigger Property="IsDragging" Value="True"><Setter TargetName="Grip" Property="Background" Value="#A3A5AE" /></Trigger>
                            </ControlTemplate.Triggers>
                          </ControlTemplate>
                        </Thumb.Template>
                      </Thumb>
                    </Track.Thumb>
                    <Track.IncreaseRepeatButton>
                      <RepeatButton Command="{x:Static ScrollBar.PageDownCommand}" Focusable="False">
                        <RepeatButton.Template><ControlTemplate TargetType="RepeatButton"><Border Background="Transparent" /></ControlTemplate></RepeatButton.Template>
                      </RepeatButton>
                    </Track.IncreaseRepeatButton>
                  </Track>
                </Grid>
              </ControlTemplate>
            </Setter.Value>
          </Setter>
        </Style>
        """.Replace("#5F626D", light ? "#817E70" : "#918798").Replace("#8B8E99", light ? "#44433C" : "#C1B38B").Replace("#A3A5AE", light ? "#141414" : "#D6BB87"));

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out NativeRect rectangle);
}
