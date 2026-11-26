using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GameDailyWidget;

internal sealed class GuideAuthorsWindow : Window
{
    internal List<GuideAuthor> Authors { get; }
    private readonly StackPanel _list = new();
    private readonly TextBox _name = new(), _mid = new();
    private readonly ComboBox _game = new();
    private readonly TextBlock _error = new();

    internal GuideAuthorsWindow(IEnumerable<GuideAuthor> authors)
    {
        Authors = authors.ToList();
        Title = "攻略博主";
        Width = 465;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Background = ReminderDelayWindow.Paint("#22232A");
        Foreground = ReminderDelayWindow.Paint("#F2F0E9");
        FontFamily = new FontFamily("Microsoft YaHei UI");
        var body = new StackPanel { Margin=new Thickness(22) };
        body.Children.Add(new TextBlock { Text=Title, FontSize=19, FontWeight=FontWeights.SemiBold });
        body.Children.Add(new ScrollViewer { Content=_list, MaxHeight=205, Margin=new Thickness(0,8,0,12), VerticalScrollBarVisibility=ScrollBarVisibility.Auto });
        void Input(string caption,TextBox input)
        {
            body.Children.Add(new TextBlock { Text=caption });
            input.Background=ReminderDelayWindow.Paint("#16171C");
            input.Foreground=input.CaretBrush=Foreground;
            input.Padding=new Thickness(7,4,7,4);
            input.Margin=new Thickness(0,3,0,8);
            body.Children.Add(input);
        }
        Input("显示名称",_name);
        Input("B 站空间地址或数字 mid",_mid);
        var addRow = new DockPanel();
        var add = ReminderDelayWindow.MakeButton("添加");
        add.Click += (_,_) => AddAuthor();
        DockPanel.SetDock(add,Dock.Right); addRow.Children.Add(add);
        addRow.Children.Add(new TextBlock { Text="适用游戏",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,8,0) });
        _game.ItemsSource=new[]{"鸣潮","终末地","两者"};
        _game.SelectedIndex=0;
        _game.MaxWidth=120;
        _game.HorizontalAlignment=HorizontalAlignment.Left;
        addRow.Children.Add(_game);
        body.Children.Add(addRow);
        _error.Foreground=ReminderDelayWindow.Paint("#FFB3A8");
        _error.MinHeight=23;
        body.Children.Add(_error);
        var buttons=new StackPanel { Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right };
        var cancel=ReminderDelayWindow.MakeButton("取消");
        cancel.IsCancel=true;
        buttons.Children.Add(cancel);
        var save=ReminderDelayWindow.MakeButton("保存",true);
        save.Margin=new Thickness(8,0,0,0);
        save.Click += (_,_) => { if ((_name.Text.Trim().Length == 0 && _mid.Text.Trim().Length == 0) || AddAuthor()) DialogResult=true; };
        buttons.Children.Add(save);
        body.Children.Add(buttons);
        Content=body;
        Refresh();
    }

    private bool AddAuthor()
    {
        if (!GuideAuthor.TryCreate(_name.Text,_mid.Text,_game.SelectedIndex switch { 0=>"wuwa",1=>"endfield",_=>"both" },out var author))
        { _error.Text="请输入显示名称和有效的数字 mid 或 B 站空间主页地址。"; return false; }
        Authors.Add(author!);
        _name.Clear(); _mid.Clear(); _error.Text="";
        Refresh();
        return true;
    }

    private void Refresh()
    {
        _list.Children.Clear();
        foreach (var author in Authors)
        {
            var row=new DockPanel { Margin=new Thickness(0,3,0,3) };
            var delete=ReminderDelayWindow.MakeButton("删除");
            delete.Click += (_,_) => { Authors.Remove(author); Refresh(); };
            DockPanel.SetDock(delete,Dock.Right); row.Children.Add(delete);
            string game = author.Game switch { "wuwa"=>"鸣潮","endfield"=>"终末地",_=>"两者" };
            var text=author.Name+" · "+author.Mid+" · "+game;
            row.Children.Add(new TextBlock { Text=text, ToolTip=text, TextTrimming=TextTrimming.CharacterEllipsis, VerticalAlignment=VerticalAlignment.Center });
            _list.Children.Add(row);
        }
    }
}
