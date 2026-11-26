using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace GameDailyWidget;

public sealed partial class MainWindow
{
    private readonly Button _poolsButton = MakeButton("卡池 ˅","展开当前游戏的卡池");
    private readonly StackPanel _poolsPanel = new();
    private bool _poolsOpen;
    private string? _poolState;
    private Button? _firstGuide;
    private GuideAuthorsWindow? _guideDialog;

    internal void OpenPools()
    {
        if (_overviewOpen) ShowGame(_currentGame,false);
        if (!_poolsOpen) TogglePools();
    }

    private void TogglePools()
    {
        _eventsOpen = false;
        _expanded = _poolsOpen = !_poolsOpen;
        ApplyPanel();
    }

    private void EditGuideAuthors()
    {
        if (_guideDialog != null || _closed) return;
        _guideDialog = new(_settings.GuideAuthors) { Owner=this };
        try
        {
            if (_guideDialog.ShowDialog() == true)
            {
                _settings.GuideAuthors = _guideDialog.Authors;
                SavePreferences();
            }
        }
        finally { _guideDialog=null; }
    }

    private void RenderPools()
    {
        if (!_poolsOpen || _eventStore == null) return;
        var now = DateTimeOffset.UtcNow;
        var pools = PoolCatalog.Current(_eventStore.Data,_currentGame,now).ToArray();
        var zone = _displayZone();
        string Local(DateTimeOffset time) => DailyReminderSchedule.Local(time,zone,"MM/dd HH:mm");
        string Timing(GamePool p)
        {
            if (p.Start is {} start && start > now)
                return Local(start) + " 开始 · " + (p.End is {} end ? Local(end) + " 结束" : "结束时间待公告");
            if (p.End is {} finish)
                return "剩 " + Countdown(finish-now) + " · " + Local(finish) + " 结束";
            return p.Start is {} since ? Local(since) + " 起 · 结束时间待公告" : "时间待公告";
        }
        var signature = _currentGame + zone.Id + string.Join("\n",pools.Select(p =>
            p.Key + p.Name + p.Kind + Timing(p) + string.Join("|",p.Items)));
        if (_poolState == signature) return;
        _poolState = signature;
        _poolsPanel.Children.Clear();
        _firstGuide = null;
        foreach (var pool in pools)
        {
            var block = new StackPanel { Margin=new Thickness(0,4,0,5) };
            var name = Label(pool.Name,13,FaceText,FontWeights.SemiBold);
            Trim(name); name.ToolTip=pool.Name;
            block.Children.Add(name);
            var timing = Label(Timing(pool),10,FaceMuted);
            string Beijing(DateTimeOffset? t) => t?.ToOffset(EventParser.China).ToString("MM/dd HH:mm") ?? "待公告";
            timing.ToolTip = $"北京时间 {Beijing(pool.Start)} 至 {Beijing(pool.End)}\n来源：{pool.Source.Title}";
            block.Children.Add(timing);
            foreach (var item in pool.Items)
            {
                var row = new DockPanel { Margin=new Thickness(0,3,0,0) };
                var guide = PanelButton("攻略 ▾","查看" + item.Name + "的攻略链接");
                guide.Tag = item.Name;
                guide.Click += (_,_) => { var menu=GuideMenu(guide); menu.IsOpen=true; };
                _firstGuide ??= guide;
                DockPanel.SetDock(guide,Dock.Right); row.Children.Add(guide);
                var stars = Label("★" + item.Stars,12,IsEndfield ? Ink : Sand,FontWeights.SemiBold);
                stars.Margin = new Thickness(0,0,8,0);
                row.Children.Add(stars);
                var title = Label(item.Name+" · "+item.Category,12,FaceText);
                Trim(title); title.ToolTip=title.Text;
                row.Children.Add(title);
                block.Children.Add(row);
            }
            _poolsPanel.Children.Add(block);
        }
        if (pools.Length == 0) _poolsPanel.Children.Add(Label("暂无已公布的限时卡池",11,FaceMuted));
        ApplySize();
    }

    private ContextMenu GuideMenu(Button button)
    {
        var menu = new ContextMenu { PlacementTarget=button, Placement=PlacementMode.Bottom };
        foreach (var link in GuideLinks.For(_settings.GuideAuthors,_currentGame,(string)button.Tag))
        {
            var entry = new MenuItem { Header=link.Label };
            entry.Click += (_,_) =>
            {
                if (_preview) return;
                try { GuideLinks.Open(link.Target,u => Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute=true })); }
                catch { Note("无法打开默认浏览器"); }
            };
            menu.Items.Add(entry);
        }
        DarkMenuTheme.Apply(menu);
        return menu;
    }

    internal ContextMenu GuideMenuForPreview() => GuideMenu(_firstGuide ?? throw new InvalidOperationException("预览卡池没有 UP 条目"));
}
