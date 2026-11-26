using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GameDailyWidget;

public sealed partial class MainWindow
{
    private readonly Dictionary<string,FrameworkElement> _worldNotices=new();
    private FrameworkElement _faceNotice=null!;
    private string? _previewNotice;
    private Action<string>? _previewEditorOpened;
    internal void PreviewFraming(string game)
    {
        if(!_preview)return;
        _previewNotice=game; RefreshFraming();
    }
    private FrameworkElement MakeFraming(string? world)
    {
        var body=new StackPanel {Orientation=Orientation.Horizontal};
        Button Part(string title,string tip)
        {
            var b=MakeButton(title,tip); b.FontSize=9;b.Padding=new Thickness(3,0,3,0);b.Template=Chrome(3);
            b.BorderThickness=new Thickness(0); b.Background=Brushes.Transparent;body.Children.Add(b);return b;
        }
        var open=Part("新版本背景 · 调整构图 ›","打开这张图的构图预览");
        var close=Part("×","关闭这张图的构图提醒");
        var chip=new Border {Child=body,BorderThickness=new Thickness(1),Visibility=Visibility.Collapsed};
        void Act(bool edit)
        {
            var game=world??_currentGame;
            if(_preview){if(edit)_previewEditorOpened?.Invoke(game); _previewNotice=null; RefreshFraming();return;}
            if(_settings.FramingFor(game).Pending is not {} key)return;
            if(edit) EditBackground(game,key);
            else HandleFraming(game,key);
        }
        open.Click+=(_,_)=>Act(true);close.Click+=(_,_)=>Act(false);
        chip.Tag=new Action<string>(game=>{
            bool ef=game=="endfield";chip.Background=ef?Ink:Paint("#D5201C27");chip.BorderBrush=ef?Ink:TwinGold;
            chip.CornerRadius=new CornerRadius(ef?0:8);open.Foreground=close.Foreground=ef?SafetyYellow:Sand;
        });
        return chip;
    }
    internal void CheckFramingButtons()
    {
        foreach(var game in new[]{"wuwa","endfield"})
        {
            string? opened=null;
            _previewEditorOpened=g=>opened=g;
            try
            {
                foreach(bool single in new[]{false,true})
                {
                    if(single) OpenGamePage(game); else ShowOverview();
                    var chip=(Border)(single?_faceNotice:_worldNotices[game]);
                    var actions=(StackPanel)chip.Child;
                    for(int i=0;i<2;i++)
                    {
                        opened=null;PreviewFraming(game);
                        if(chip.Visibility!=Visibility.Visible)throw new InvalidOperationException("未显示构图提醒");
                        ((Button)actions.Children[i]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        if(opened!=(i==0?game:null) || chip.Visibility!=Visibility.Collapsed)
                            throw new InvalidOperationException("构图提醒编辑或关闭路由异常");
                    }
                }
            }
            finally { _previewEditorOpened=null; ShowOverview(); }
        }
    }
    private void HandleFraming(string game,string key)
    {
        _settings.FramingFor(game).Handle(key);SavePreferences();RefreshFraming();
    }
    private void RefreshFraming()
    {
        foreach(var (game,chip) in _worldNotices)
        {
            var n=_settings.FramingFor(game);
            if(!_preview && _artCache!=null && n.Pending!=null && n.Validate(_latestArt.GetValueOrDefault(game),_settings.ArtFor(game),
                _artCache?.FileFor(game,n.Pending)?.EndsWith(".official",StringComparison.Ordinal)==true))SavePreferences();
            var visible=(_preview?_previewNotice==game:n.Pending!=null)?Visibility.Visible:Visibility.Collapsed;
            chip.Visibility=visible;
            if(game==_currentGame && _faceNotice!=null)
            {
                _faceNotice.Visibility=visible;
                if(visible==Visibility.Visible && !Equals(_faceNotice.DataContext,game)){ ((Action<string>)_faceNotice.Tag)(game); _faceNotice.DataContext=game; }
                _warningChip.MaxWidth=visible==Visibility.Visible?285:double.PositiveInfinity;
            }
        }
    }
}
