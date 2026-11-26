using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GameDailyWidget;

public sealed partial class MainWindow
{
    private readonly Button _eventsButton = MakeButton("活动 ˅", "展开当前游戏的活动");
    private readonly Button _summaryButton = MakeButton("", "查看当前游戏的限时活动");
    private readonly TextBlock _summary = Label("", 10, Muted);
    private readonly StackPanel _eventsPanel = new();
    private EventStore? _eventStore;
    private EventSyncCoordinator? _eventSync;
    private IReadOnlyDictionary<string,string> _eventProgress = new Dictionary<string,string>();
    internal void SetEventProgress(IReadOnlyDictionary<string,string> progress)
    {
        if (_eventProgress.Count == progress.Count && progress.All(pair => _eventProgress.GetValueOrDefault(pair.Key) == pair.Value)) return;
        _eventProgress = progress;
        RenderEvents();
    }
    private bool _eventsOpen;
    internal (object,object,object,object) PanelButtonsForCheck =>
        (_expand.Content,_expand.ToolTip,_eventsButton.Content,_eventsButton.ToolTip);
    internal bool EventsOpenForCheck => _eventsOpen;
    private bool _showIgnored;
    private string? _panelState;

    internal void BindEvents(EventStore store, EventSyncCoordinator? coordinator)
    {
        _eventStore = store;
        _eventSync = coordinator;
        if (coordinator != null) coordinator.Changed += RenderEvents;
        RenderTimes();
    }

    internal void OpenEvents()
    {
        if (_overviewOpen) ShowGame(_currentGame,false);
        if (!_eventsOpen) ToggleEvents();
    }

    private void ToggleEvents()
    {
        _poolsOpen = false;
        _eventsOpen = !_eventsOpen;
        _expanded = _eventsOpen;
        ApplyPanel();
    }

    private void ApplyPanel()
    {
        _detailsPanel.Visibility = _expanded ? Visibility.Visible : Visibility.Collapsed;
        _detailScroll.Content = _eventsOpen ? _eventsPanel : _poolsOpen ? _poolsPanel : _details;
        _expand.Content = _expanded && !_eventsOpen && !_poolsOpen ? "详情 ˄" : "详情 ˅";
        _expand.ToolTip = _expanded && !_eventsOpen && !_poolsOpen ? "收起当前游戏的详情" : "展开当前游戏的详情";
        _eventsButton.Content = _eventsOpen ? "活动 ˄" : "活动 ˅";
        _eventsButton.ToolTip = _eventsOpen ? "收起当前游戏的活动" : "展开当前游戏的活动";
        _poolsButton.Content = _poolsOpen ? "卡池 ˄" : "卡池 ˅";
        _poolsButton.ToolTip = _poolsOpen ? "收起当前游戏的卡池" : "展开当前游戏的卡池";
        SetPanelDepth();
        _detailScroll.ScrollToTop();
        _poolState = null;
        RenderPools();
        _panelState = null;
        RenderEvents();
        _settings.Scale = Math.Min(_settings.Scale, MaximumScaleForScreen());
        ApplySize();
        ClampToWorkingArea();
        NotifyGeometryChanged();
    }

    internal object CheckEventLayout()
    {
        var spare = _layout.RowDefinitions[^1].ActualHeight;
        var scroll = _detailScroll;
        var clipped = scroll.ExtentHeight > scroll.ViewportHeight + 0.01;
        var bar = scroll.ComputedVerticalScrollBarVisibility == Visibility.Visible;
        scroll.ScrollToEnd();
        scroll.UpdateLayout();
        var reachable = scroll.VerticalOffset + scroll.ViewportHeight >= scroll.ExtentHeight - 0.1;
        scroll.ScrollToTop();
        scroll.UpdateLayout();
        if (!_eventsOpen || spare > 1.1 || scroll.ViewportHeight <= 0 || clipped != bar || !reachable)
            throw new InvalidOperationException($"活动面板布局异常 {spare}/{scroll.ViewportHeight}/{scroll.ExtentHeight}/{bar}/{reachable}");
        return new { spare, scroll.ViewportHeight, scroll.ExtentHeight, bar, reachable, Width, Height };
    }

    private void SyncEvents()
    {
        if (!_preview && _eventSync != null) _ = _eventSync.SyncAsync(DateTimeOffset.UtcNow.ToOffset(EventParser.China));
    }

    private static string Countdown(TimeSpan time) => CountdownText.Format(time);

    private static string EventTime(GameEvent item, DateTimeOffset now)
    {
        if (now < item.Start) return CountdownText.Until(item.Start-now);
        if (item.Unknown) return "时间未识别 · 查看公告";
        if (item.Deadline is not {} end) return "版本结束前 · 时间待公告";
        return (item.ClaimDeadline == null ? "剩 " : "领奖剩 ") + Countdown(end-now);
    }

    private Brush DeadlineColor(GameEvent item, DateTimeOffset now)
    {
        if (item.Start > now || item.Unknown || item.Deadline == null) return FaceMuted;
        var remaining = item.Deadline - now;
        return remaining < TimeSpan.FromHours(3) ? (IsEndfield ? Paint("#873018") : Warning) : remaining < TimeSpan.FromHours(24) ? FaceAccentText : FaceMuted;
    }

    private void RenderEvents()
    {
        if (_eventStore == null || _closed) return;
        var now = DateTimeOffset.UtcNow.ToOffset(EventParser.China);
        var data = _eventStore.Data;
        var rows = data.Events.Where(e => e.Game == _currentGame && !(e.Deadline <= now))
            .GroupBy(e => EventParser.Normalize(e.Name)).Select(g => g.OrderBy(e => e.Start > now)
                .ThenBy(e => e.Start).First())
            .OrderBy(e => e.Deadline ?? DateTimeOffset.MaxValue).ThenBy(e => e.Name).ToArray();
        EventMark? Mark(GameEvent e) => data.Marks.GetValueOrDefault(e.Key);
        var active = rows.Where(e => e.Start <= now && Mark(e)?.Ignored != true).ToList();
        var claimed = active.Count(e => Mark(e)?.Claimed == true);
        var next = active.FirstOrDefault(e => Mark(e)?.Claimed != true);
        var sync = data.Sync.GetValueOrDefault(_currentGame);
        var busy = _eventSync?.Busy.Contains(_currentGame) == true;
        var summary = _eventStore.Warning ?? (busy ? "同步中…" : sync?.Error != null ? "同步失败，显示上次结果" :
            active.Count == 0 ? "暂无进行中的活动" : next == null ? $"{claimed}/{active.Count} 已全部领完" :
            $"{claimed}/{active.Count} 已领完 · 最近截止「{next.Name}」{EventTime(next,now).Replace(" · 时间待公告","")}");
        _summaryButton.ToolTip = "限时活动  " + summary;
        var compact = next != null && !busy && sync?.Error == null && _eventStore.Warning == null
            ? $"{claimed}/{active.Count} · 「{next.Name}」{EventTime(next,now).Replace(" · 时间待公告","")}" : summary;
        _summary.Text = "限时活动  " + compact;
        SetChipColor(_eventStore.Warning != null || sync?.Error != null || next?.Deadline - now < TimeSpan.FromDays(1));
        if (!_eventsOpen) return;
        var syncText = (busy ? "同步中… · " : sync?.Error != null ? "同步失败 · " : "") +
            (sync?.LastSuccess is {} last ? "上次同步 " + DailyReminderSchedule.Local(last,_displayZone(),"MM-dd HH:mm") : "尚无成功同步记录");
        if (sync?.Coverage != null) syncText += " · " + sync.Coverage;
        var state = string.Join("\n", rows.Select(e => $"{e}|{Mark(e)}|{EventTime(e,now)}|{_eventProgress.GetValueOrDefault(e.Key)}")) + _currentGame + syncText + _showIgnored + _displayZone().Id;
        if (_panelState == state) return;
        _panelState = state;
        _eventsPanel.Children.Clear();
        var top = new DockPanel();
        var syncButton = PanelButton("立即同步", "从两款游戏官网同步活动");
        syncButton.IsEnabled = !_preview && !busy;
        syncButton.Click += (_,_) => SyncEvents();
        DockPanel.SetDock(syncButton,Dock.Right);
        top.Children.Add(syncButton);
        top.Children.Add(Label(syncText,10,FaceMuted));
        _eventsPanel.Children.Add(top);
        void Group(string label, IEnumerable<GameEvent> items, bool dim)
        {
            var list = items.ToList();
            if (list.Count == 0) return;
            var title = Label(label,10,FaceMuted,FontWeights.SemiBold);
            title.Margin = new Thickness(0,7,0,2);
            _eventsPanel.Children.Add(title);
            foreach (var item in list) _eventsPanel.Children.Add(EventRow(item,Mark(item),now,dim));
        }
        var visible = rows.Where(e => Mark(e)?.Ignored != true);
        Group("进行中", visible.Where(e => Mark(e)?.Claimed != true && e.Start <= now), false);
        Group("即将开始", visible.Where(e => Mark(e)?.Claimed != true && e.Start > now).OrderBy(e => e.Start),true);
        Group("已领完",visible.Where(e => Mark(e)?.Claimed == true),true);
        var ignored = rows.Where(e => Mark(e)?.Ignored == true).ToList();
        if (ignored.Count > 0)
        {
            var toggle = PanelButton($"{(_showIgnored ? "收起" : "显示")}已忽略（{ignored.Count}）","查看或恢复忽略的活动");
            toggle.HorizontalAlignment = HorizontalAlignment.Left;
            toggle.Click += (_,_) => { _showIgnored = !_showIgnored; RenderEvents(); };
            _eventsPanel.Children.Add(toggle);
            if (_showIgnored) Group("已忽略",ignored,true);
        }
        if (rows.Length == 0) _eventsPanel.Children.Add(Label("暂无活动",11,FaceMuted));
        ApplySize();
    }

    private FrameworkElement EventRow(GameEvent item, EventMark? mark, DateTimeOffset now, bool dim)
    {
        var row = new Grid { Tag=item.Key, Margin = new Thickness(0,3,0,3) };
        row.ColumnDefinitions.Add(new());
        row.ColumnDefinitions.Add(new() { Width=GridLength.Auto });
        row.ColumnDefinitions.Add(new() { Width=GridLength.Auto });
        var info = new StackPanel();
        var name = Label(item.Name,12, dim ? FaceMuted : FaceText,FontWeights.SemiBold);
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        name.ToolTip = item.Name;
        info.Children.Add(name);
        var reward = item.Currency.Amount is {} amount ? $" · {item.Currency.Name}×{amount}" : "";
        var details = Label(item.Kind + " · " + EventTime(item,now) + reward,10,dim ? FaceMuted : DeadlineColor(item,now));
        details.TextTrimming = TextTrimming.CharacterEllipsis;
        details.ToolTip = details.Text + (item.Deadline is {} deadline ? " · " + EventDeadlineReminder.DeadlineTip(deadline,_displayZone()) : "");
        if (_eventProgress.TryGetValue(item.Key, out var progress))
        {
            var line = new Grid { HorizontalAlignment=HorizontalAlignment.Left };
            line.ColumnDefinitions.Add(new());
            line.ColumnDefinitions.Add(new() { Width=GridLength.Auto });
            line.Children.Add(details);
            var label = Label(" · " + progress,10,details.Foreground);
            Grid.SetColumn(label,1);
            line.Children.Add(label);
            info.Children.Add(line);
        }
        else info.Children.Add(details);
        row.Children.Add(info);
        if (EventStore.CanClaim(item, mark, now))
        {
            var claim = new CheckBox { Content = mark?.Automatic == true ? "已领完（自动）" : "已领完", IsChecked = mark?.Claimed == true, Foreground=FaceMuted,
                FontSize=10, VerticalAlignment=VerticalAlignment.Center, Margin=new Thickness(4,0,6,0) };
            claim.Template = ClaimTemplate();
            claim.Click += (_,_) => SetMark(item,claim.IsChecked == true,mark?.Ignored == true);
            Grid.SetColumn(claim,1); row.Children.Add(claim);
        }
        var more = PanelButton("⋯","活动操作");
        more.VerticalAlignment = VerticalAlignment.Center;
        more.Click += (_,_) =>
        {
            var menu = new ContextMenu { PlacementTarget=more };
            var ignore = new MenuItem { Header=mark?.Ignored == true ? "取消忽略" : "忽略" };
            ignore.Click += (_,_) => SetMark(item,mark?.Claimed == true,mark?.Ignored != true);
            menu.Items.Add(ignore);
            var open = new MenuItem { Header="打开原公告", IsEnabled=!_preview };
            open.Click += (_,_) =>
            {
                try
                {
                    var source = EventFeed.Source(item.Game,item.Source.Id,item.Source.Title);
                    Process.Start(new ProcessStartInfo(source.Url) { UseShellExecute=true });
                }
                catch { Note("暂时无法打开原公告"); }
            };
            menu.Items.Add(open);
            DarkMenuTheme.Apply(menu);
            menu.IsOpen = true;
        };
        Grid.SetColumn(more,2); row.Children.Add(more);
        return row;
    }

    internal void SetMark(GameEvent item, bool claimed, bool ignored)
    {
        _eventStore?.Mark(item,claimed,ignored,DateTimeOffset.UtcNow.ToOffset(EventParser.China));
        RenderEvents();
    }
}
