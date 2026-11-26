using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GameDailyWidget;

internal sealed class ReminderTimesWindow : Window
{
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<TimeZoneInfo> _zone;
    private readonly StackPanel _list = new();
    private readonly TextBlock _reset = new(), _zoneInfo = new(), _season = new(), _mode = new(), _error = new();
    private readonly TextBox _input = new();
    internal List<string>? Times { get; private set; }

    internal ReminderTimesWindow(List<string>? times, Func<DateTimeOffset> clock, Func<TimeZoneInfo> zone)
    {
        Times = times?.ToList();
        _clock = clock;
        _zone = zone;
        Title = "日常提醒时间";
        Width = 490;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = ReminderDelayWindow.Paint("#22232A");
        Foreground = ReminderDelayWindow.Paint("#F2F0E9");
        FontFamily = new FontFamily("Microsoft YaHei UI");
        FontSize = 13;
        UseLayoutRounding = true;
        var body = new StackPanel { Margin=new Thickness(22) };
        _reset.FontSize = 17;
        _reset.FontWeight = FontWeights.SemiBold;
        body.Children.Add(_reset);
        foreach (var label in new[]{_zoneInfo,_season})
        {
            label.Foreground = ReminderDelayWindow.Paint("#B7B8C2");
            label.TextWrapping = TextWrapping.Wrap;
            label.Margin = new Thickness(0,5,0,0);
            body.Children.Add(label);
        }
        _mode.Margin = new Thickness(0,18,0,5);
        body.Children.Add(_mode);
        body.Children.Add(_list);
        var addRow = new DockPanel { Margin=new Thickness(0,8,0,0) };
        var add = ReminderDelayWindow.MakeButton("添加");
        add.Click += (_,_) => AddTime();
        DockPanel.SetDock(add,Dock.Right);
        addRow.Children.Add(add);
        _input.Background = ReminderDelayWindow.Paint("#16171C");
        _input.Foreground = Foreground;
        _input.CaretBrush = Foreground;
        _input.Margin = new Thickness(0,0,8,0);
        _input.Padding = new Thickness(8,5,8,5);
        _input.ToolTip = "本地时间，例如 9:00 或 21:30";
        _input.TextChanged += (_,_) => _error.Text = "";
        _input.KeyDown += (_,e) => { if (e.Key == System.Windows.Input.Key.Enter) { e.Handled=true; AddTime(); } };
        addRow.Children.Add(_input);
        body.Children.Add(addRow);
        body.Children.Add(new TextBlock { Text="使用 24 小时制，最多 5 个。删空列表即关闭换日前提醒。", Margin=new Thickness(0,6,0,0) });
        _error.Foreground = ReminderDelayWindow.Paint("#FFB3A8");
        _error.MinHeight = 28;
        body.Children.Add(_error);
        var buttons = new DockPanel();
        var reset = ReminderDelayWindow.MakeButton("恢复默认");
        reset.Click += (_,_) => { Times=null; _input.Clear(); _error.Text=""; Refresh(); };
        buttons.Children.Add(reset);
        var right = new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};
        var cancel = ReminderDelayWindow.MakeButton("取消");
        cancel.IsCancel = true;
        right.Children.Add(cancel);
        var save = ReminderDelayWindow.MakeButton("保存",true);
        save.Margin = new Thickness(8,0,0,0);
        save.Click += (_,_) =>
        {
            if (!string.IsNullOrWhiteSpace(_input.Text) && !AddTime()) return;
            DialogResult = true;
        };
        right.Children.Add(save);
        buttons.Children.Add(right);
        body.Children.Add(buttons);
        Content = body;
        Refresh();
    }

    internal static string? Validate(string input, List<string> existing, DateTimeOffset now, TimeZoneInfo zone, out string? time)
    {
        time = DailyReminderSchedule.Normalize(input);
        if (time == null) return "请输入有效的 24 小时时间，例如 09:00。";
        if (time == DailyReminderSchedule.Local(DailyReminderSchedule.NextReset(now),zone)) return "提醒时间不能与换日时间相同。";
        if (existing.Contains(time)) return "这个时间已经在列表中。";
        return existing.Count >= 5 ? "最多可设置 5 个提醒时间。" : null;
    }

    private bool AddTime()
    {
        var now = _clock();
        var zone = _zone();
        var list = DailyReminderSchedule.DisplayTimes(Times,now,zone);
        var error = Validate(_input.Text,list,now,zone,out var time);
        if (error != null) { _error.Text=error; return false; }
        list.Add(time!);
        Times = list.Order(StringComparer.Ordinal).ToList();
        _input.Clear();
        _error.Text = "";
        Refresh();
        return true;
    }

    internal void PreviewError() { _input.Text="25:00"; AddTime(); }

    // Windows display names already carry the standard offset, e.g. "(UTC-05:00) ...".
    // Add the offset only when the name lacks it, and the current one only during DST.
    internal static string ZoneText(TimeZoneInfo zone, DateTimeOffset now)
    {
        static string Offset(TimeSpan value) => $"UTC{(value < TimeSpan.Zero ? "-" : "+")}{value.Duration():hh\\:mm}";
        var name = zone.DisplayName.Contains("UTC",StringComparison.Ordinal)
            ? zone.DisplayName : $"{Offset(zone.BaseUtcOffset)} {zone.DisplayName}".TrimEnd();
        var daylight = zone.IsDaylightSavingTime(now) ? $" · 现为夏令时 {Offset(zone.GetUtcOffset(now))}" : "";
        return "时区：跟随系统 · " + name + daylight;
    }

    internal void Refresh()
    {
        var now = _clock();
        var zone = _zone();
        var end = DailyReminderSchedule.NextReset(now);
        _reset.Text = "日常刷新：" + DailyReminderSchedule.ResetText(now,zone);
        _zoneInfo.Text = ZoneText(zone,now);
        _season.Text = "";
        if (zone.SupportsDaylightSavingTime)
        {
            for (var i=1; i<=370; i++)
            {
                var other = end.AddDays(i);
                if (zone.IsDaylightSavingTime(other) == zone.IsDaylightSavingTime(end) ||
                    zone.GetUtcOffset(other) == zone.GetUtcOffset(end)) continue;
                _season.Text = (zone.IsDaylightSavingTime(other) ? "夏令时" : "标准时间") + "期间换日为本地 " + DailyReminderSchedule.Local(other,zone);
                break;
            }
        }
        _season.Visibility = _season.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _mode.Text = Times == null ? "提醒时间（默认）" : Times.Count == 0 ? "提醒时间（已关闭）" : "提醒时间（本地）";
        _list.Children.Clear();
        var slots = DailyReminderSchedule.Slots(now,zone,Times);
        foreach (var value in DailyReminderSchedule.DisplayTimes(Times,now,zone))
        {
            var row = new DockPanel { Margin=new Thickness(0,2,0,2) };
            var remove = ReminderDelayWindow.MakeButton("删除");
            remove.Click += (_,_) =>
            {
                var list = DailyReminderSchedule.DisplayTimes(Times,_clock(),_zone());
                list.Remove(value);
                Times = list;
                _error.Text = "";
                Refresh();
            };
            DockPanel.SetDock(remove,Dock.Right);
            row.Children.Add(remove);
            var slot = slots.FirstOrDefault(s => Times == null ? DailyReminderSchedule.Local(s.At,zone)==value : s.Id==value);
            var relative = slot == null ? "本游戏日跳过" : "距刷新还有 " + DailyReminderSchedule.Remaining(slot.At);
            row.Children.Add(new TextBlock {Text=value + "     " + relative,VerticalAlignment=VerticalAlignment.Center});
            _list.Children.Add(row);
        }
    }
}
