using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace GameDailyWidget;

public sealed partial class MainWindow
{
    private readonly Canvas _twinCanvas = new() { Width = 560, Height = 372, Background = Brushes.Transparent };
    private readonly Dictionary<string, TwinColumn> _twinColumns = new();

    private readonly TextBlock _twinTime = new(), _twinReset = new();
    private readonly TwinStrip _twinStrip = new();
    private EdgeDockSide _twinEdge;
    private string _twinScenario = "";
    private static readonly FontFamily TwinDigits = new("Bahnschrift, Segoe UI"),
        TwinLight = new("Segoe UI Variable Display, Segoe UI");
    private static readonly Brush Coral = Paint("#FF9D86");
    private static readonly Brush GoldText = Paint("#E8CC92");
    private static readonly Brush TwinInk = Paint("#141414");
    private static readonly Brush TwinGold = Paint("#D6BB87");

    private sealed class TwinColumn(bool light)
    {
        internal readonly bool Light = light;
        internal (GameViewState State, long Minute, string? Note, string Zone, string Scenario, int Articles, int Windows, bool Busy)? Last;
        internal readonly Border Background = new(), VersionBar = new();
        internal readonly Canvas Canvas = new() { Width = 190, Height = 224 };
        internal readonly TextBlock Version = new(), Stamina = new(), MaxStamina = new(),
            Daily = new(), MaxDaily = new(), Hint = new(), Badge = new(), Credit = new();
        internal readonly Border Status = new();
        internal readonly Button Login = new();
        internal readonly TwinRing Ring = new(light);
        internal readonly Border[] Segments = new Border[5];
    }

    private static void TwinPlace(Canvas canvas, FrameworkElement element, double x, double y, double? w=null, double? h=null)
    {
        Canvas.SetLeft(element,x); Canvas.SetTop(element,y);
        if (w.HasValue) element.Width = w.Value;
        if (h.HasValue) element.Height = h.Value;
        canvas.Children.Add(element);
    }

    private static void Type(TextBlock t, string text, double size, Brush fg, bool right=false, FontWeight? weight=null)
    {
        t.Text=text; t.FontSize=size; t.Foreground=fg; t.FontWeight=weight ?? FontWeights.Normal;
        t.TextAlignment=right ? TextAlignment.Right : TextAlignment.Left; t.TextTrimming=TextTrimming.CharacterEllipsis;
        t.TextWrapping=TextWrapping.NoWrap;
    }

    internal Grid BuildOverview()
    {
        var root = new Grid();
        var view = new Viewbox { Child=_twinCanvas, Stretch=Stretch.Fill };
        root.Children.Add(view);
        _twinCanvas.Clip = new RectangleGeometry(new Rect(0,0,560,372),16,16);
        foreach (var game in new[] { "wuwa","endfield" })
        {
            bool light=game=="endfield";
            var col=new TwinColumn(light);
            _twinColumns[game]=col;
            col.Background.Background=ArtFallback.For(game);
            var world=new Grid(); world.Children.Add(col.Background); AddWorldFloor(world,light);
            world.Clip=Geometry.Parse(light ? "M336,0 L560,0 560,372 224,372 Z" : "M0,0 L336,0 224,372 0,372 Z");
            TwinPlace(_twinCanvas,world,0,0,560,372);
        }
        void Seam(Brush brush,double width,double opacity)
        {
            var line=new Line { X1=336,Y1=0,X2=224,Y2=372,Stroke=brush,StrokeThickness=width,Opacity=opacity,IsHitTestVisible=false };
            TwinPlace(_twinCanvas,line,0,0);
        }
        Seam(Brushes.White,7,.07);
        var gradient=new LinearGradientBrush { StartPoint=new Point(.5,0),EndPoint=new Point(.5,1) };
        gradient.GradientStops.Add(new GradientStop(((SolidColorBrush)SafetyYellow).Color,0));
        gradient.GradientStops.Add(new GradientStop(Colors.White,.45));
        gradient.GradientStops.Add(new GradientStop(((SolidColorBrush)TwinGold).Color,.8));
        Seam(gradient,1.6,.9);
        foreach (var (game,col) in _twinColumns) BuildTwinColumn(game,col);
        var clock=new StackPanel { HorizontalAlignment=HorizontalAlignment.Center, VerticalAlignment=VerticalAlignment.Center };
        var header=Label("距 换 日",8.5,Paint("#D3FFFFFF")); header.HorizontalAlignment=HorizontalAlignment.Center;
        clock.Children.Add(header);
        Type(_twinTime,"00:00:00",19,Brushes.White,weight:FontWeights.SemiBold);
        _twinTime.TextAlignment=TextAlignment.Center; _twinTime.FontFamily=TwinDigits;
        clock.Children.Add(_twinTime);
        Type(_twinReset,"北京时间 04:00",8.5,Paint("#DAFFFFFF")); _twinReset.TextAlignment=TextAlignment.Center;
        clock.Children.Add(_twinReset);
        TwinPlace(_twinCanvas,new Border { Background=Paint("#BC0E0E16"),BorderBrush=Paint("#42FFFFFF"),BorderThickness=new Thickness(1),
            CornerRadius=new CornerRadius(12),Child=clock },221,14,118,56);
        int i=0;
        foreach (var (glyph,name) in new[]{("⚙","设置"),("—","最小化到托盘"),("×","关闭到托盘")})
        {
            var button=MakeButton(glyph,name);
            button.Template=Chrome(0); button.Background=Paint("#FAF9F4"); button.BorderBrush=Paint("#30000000");
            TwinPlace(_twinCanvas,OverviewPlate(button,Ink,2),472+i*27,12); button.BorderBrush=Ink;
            button.BorderThickness=new Thickness(1); button.Foreground=TwinInk; button.Padding=new Thickness(0); button.FontSize=12;
            button.Click+=(_,_)=>{ if(name=="设置") ShowSettings(); else Hide(); };
            TwinPlace(_twinCanvas,button,472+i++*27,12,22,22);
        }
        foreach(var game in _twinColumns.Keys) BuildDeadlineRows(game,game=="endfield");
        return root;
    }

    private void BuildTwinColumn(string game,TwinColumn c)
    {
        bool ef=c.Light;
        var canvas=c.Canvas;
        TwinPlace(_twinCanvas,canvas,ef ? 350 : 20,ef ? 37 : 14);
        var ink=ef ? TwinInk : TextColor;
        double x=ef ? 40 : 0;
        TextBlock Line(string text,double size,Brush fg,double y,FontWeight? weight=null)
        {
            var label=new TextBlock(); Type(label,text,size,fg,ef,weight);
            TwinPlace(canvas,label,0,y,190); return label;
        }
        Line(ef ? "终末地" : "鸣 潮",20,ef ? ink : Sand,0,FontWeights.Bold);
        var sub=Line(ef ? "// ARKNIGHTS: ENDFIELD" : "W U T H E R I N G   W A V E S",8,ef ? TwinInk : Sand,27,ef ? FontWeights.SemiBold : FontWeights.Normal);
        if (ef) sub.FontFamily=Mono;
        Type(c.Version,"",10,ef?SafetyYellow:Paint("#E9DDF7"),ef,ef?FontWeights.SemiBold:null);
        c.VersionBar.Child=c.Version; c.VersionBar.Padding=new Thickness(ef?11:7,0,ef?6:0,1);
        if (ef)
        {
            c.VersionBar.Background=TwinInk;
            c.VersionBar.Clip=Geometry.Parse("M5,0 L190,0 190,16 0,16 Z");
        }
        else { c.VersionBar.BorderBrush=TwinGold; c.VersionBar.BorderThickness=new Thickness(2,0,0,0); }
        // Right-align the compact version tag, allowing its background to follow its text.
        var versionHost=new Grid();
        c.VersionBar.HorizontalAlignment=ef?HorizontalAlignment.Right:HorizontalAlignment.Left;
        if(ef) versionHost.Children.Add(OverviewPlate(c.VersionBar,SafetyYellow,3));
        versionHost.Children.Add(c.VersionBar); TwinPlace(canvas,versionHost,0,43,190,16);

        var chip=MakeFraming(game); ((Action<string>)chip.Tag)(game);
        _worldNotices[game]=chip;
        // Below the version strip and beside the ring, with separate hit areas.
        TwinPlace(canvas,chip,ef?-32:88,64,128,17);
        if(ef) { var plate=OverviewPlate(chip,Ink,2); canvas.Children.Insert(canvas.Children.IndexOf(chip),plate);
            Canvas.SetLeft(plate,-32);Canvas.SetTop(plate,64); }
        var dial=new Grid();
        dial.Children.Add(c.Ring);
        var nums=new StackPanel { HorizontalAlignment=HorizontalAlignment.Center, VerticalAlignment=VerticalAlignment.Center };
        Type(c.Stamina,"—",28,ink,weight:ef?FontWeights.SemiBold:FontWeights.Light);
        c.Stamina.FontFamily=ef?TwinDigits:TwinLight; c.Stamina.TextAlignment=TextAlignment.Center;
        Type(c.MaxStamina,ef?"理智":"波片",9.5,ink); c.MaxStamina.TextAlignment=TextAlignment.Center; c.MaxStamina.Opacity=.78;
        nums.Children.Add(c.Stamina); nums.Children.Add(c.MaxStamina); dial.Children.Add(nums);
        TwinPlace(canvas,dial,ef?104:0,ef?64:62,86,86);

        var dailyLabel=Line(ef?"ACTIVITY  每日活跃度":"每日活跃度",ef?8.5:10,ink,149,ef?FontWeights.SemiBold:null);
        if (ef) dailyLabel.FontFamily=Mono;
        var dailyRow=new StackPanel { Orientation=Orientation.Horizontal, HorizontalAlignment=ef?HorizontalAlignment.Right:HorizontalAlignment.Left,
            VerticalAlignment=VerticalAlignment.Center };
        Type(c.Daily,"—",22,ink,weight:ef?FontWeights.SemiBold:FontWeights.Light); c.Daily.FontFamily=ef?TwinDigits:TwinLight;
        Type(c.MaxDaily,"",11,ink); c.MaxDaily.Opacity=.75; c.MaxDaily.Margin=new Thickness(3,0,0,3); c.MaxDaily.VerticalAlignment=VerticalAlignment.Bottom;
        dailyRow.Children.Add(c.Daily); dailyRow.Children.Add(c.MaxDaily);
        TwinPlace(canvas,new Grid { Children={dailyRow} },x,161,150,27);
        for (int i=0;i<5;i++)
        {
            var fill=new Border { Background=ef?TwinInk:TwinGold,HorizontalAlignment=HorizontalAlignment.Left,Width=0,CornerRadius=new CornerRadius(ef?0:1) };
            c.Segments[i]=fill;
            var track=new Border { Background=ef?Hazard:Paint("#29FFFFFF"),CornerRadius=new CornerRadius(ef?0:1),Child=fill };
            TwinPlace(canvas,track,x+i*30.6,191,27.6,5);
        }
        Type(c.Hint,"",10,ef?TwinInk:GoldText,ef,ef?FontWeights.SemiBold:null);
        TwinPlace(canvas,c.Hint,0,201,190);
        c.Login.Template=Chrome(ef?0:5); c.Login.Background=ef?TwinInk:Paint("#CE242236"); c.Login.Foreground=ef?SafetyYellow:Sand;
        c.Login.BorderBrush=ef?TwinInk:TwinGold; c.Login.BorderThickness=new Thickness(1); c.Login.FontSize=10;
        c.Login.Padding=new Thickness(4,0,4,0); c.Login.Content=ef?"扫码登录":"扫描账号"; c.Login.Cursor=Cursors.Hand;
        c.Login.ToolTip=ef?"通过森空岛扫码登录":"扫描鸣潮官方启动器中的账号";
        c.Login.Click+=async (_,_)=>await RunActionAsync(ef?_endfield:_wuwa,ctrl=>ctrl.LoginAsync(this),previewBlock:true);
        TwinPlace(canvas,c.Login,ef?119:0,201,71,19);

        Type(c.Badge,"",9,ef?TwinInk:Sand);
        c.Status.Child=c.Badge; c.Status.Padding=new Thickness(4,0,4,1);
        c.Status.Background=ef?Paint("#EAF3EED8"):Paint("#CA141322"); c.Status.BorderBrush=ef?Paint("#8D897D"):TwinGold;
        c.Status.BorderThickness=new Thickness(.7); c.Status.CornerRadius=new CornerRadius(ef?0:3);
        TwinPlace(canvas,c.Status,ef?46:68,6);
        Type(c.Credit,ef?"© 鹰角网络":"© 库洛游戏",8,ef?PaperMuted:NightMuted,ef);
        TwinPlace(_twinCanvas,c.Credit,ef?349:20,ef?15:241,ef?116:150);
        c.Credit.Visibility=Visibility.Collapsed;
    }

    private void SetOverviewBackground(string game,Brush brush,bool official)
    {
        if (!_twinColumns.TryGetValue(game,out var c)) return;
        c.Background.Background=brush; c.Credit.Visibility=official?Visibility.Visible:Visibility.Collapsed;
    }

    private GameViewState OverviewState(string game)
    {
        var state=(game=="endfield"?_endfield:_wuwa).State;
        if (!_preview) return state;
        return _twinScenario switch
        {
            "normal" => state with { LoggedIn=TwinReadings.HasAccount(state), Demo=false },
            "wuwa-empty" when game=="wuwa" => new() { GameKey=game, StaminaLabel="波片" },
            "endfield-empty" when game=="endfield" => new() { GameKey=game, StaminaLabel="理智" },
            "wuwa-failed" when game=="wuwa" => state with { Error="更新失败 · 演示连接暂时不可用",Stale=true },
            "endfield-failed" when game=="endfield" => state with { Error="更新失败 · 演示连接暂时不可用",Stale=true },
            "locked" when game=="wuwa" => state with { Daily=null,MaxDaily=null,DailyState="locked" },
            "complete" => state with { Daily=100,MaxDaily=100,DailyState="complete" },
            "unknown" => state with { Stamina=null,MaxStamina=null,Daily=null,MaxDaily=null,DailyState="unknown" },
            "long" => state with { Nickname=new string('长',120) },
            _ => state
        };
    }

    private EventData? _previewSummaries;
    internal void SetOverviewPreview(string scenario)
    {
        if (!_preview) return;
        _twinScenario=scenario; _previewSummaries=EventPreviewData.Summaries(scenario,DateTimeOffset.UtcNow);
        UpdateOverview(DateTimeOffset.UtcNow);
    }

    internal void UpdateOverview(DateTimeOffset now)
    {
        if (_twinColumns.Count==0) return;
        _twinTime.Text=TwinReadings.Countdown(now);
        var zone=_displayZone();
        _twinReset.Text=TwinReadings.ResetCaption(now,zone);
        long minute=now.ToUnixTimeSeconds()/60;
        foreach (var (game,c) in _twinColumns)
        {
            bool ef=c.Light;
            var state=OverviewState(game);
            var refresh=(state,minute,_localNotes.GetValueOrDefault(game),zone.Id,_twinScenario,
                _eventStore?.Data.Articles.Count??0,_eventStore?.Data.Maintenance.GetValueOrDefault(game)?.Count??0,_actions.Contains(game));
            if(c.Last==refresh) continue;
            c.Last=refresh;
            bool account=TwinReadings.HasAccount(state);
            double? stamina=account?TwinReadings.Fraction(state.Stamina,state.MaxStamina):null;
            double? daily=TwinReadings.DailyFraction(state);
            c.Ring.Set(stamina);
            c.Stamina.Text=stamina.HasValue?state.Stamina.ToString():"—";
            c.MaxStamina.Text=(account && state.MaxStamina>0?$"/ {state.MaxStamina} ":"")+(ef?"理智":"波片");
            c.Daily.Text=daily.HasValue?state.Daily.ToString():"—";
            c.MaxDaily.Text=daily.HasValue?$"/ {state.MaxDaily}":"";
            for (int i=0;i<5;i++) c.Segments[i].Width=27.6*Math.Clamp((daily??0)*5-i,0,1);
            var status=state.DailyState switch
            {
                "locked"=>"未解锁",
                "complete" when daily.HasValue=>"已达标",
                "incomplete" when daily.HasValue=>$"还差 {Math.Max(0,state.MaxDaily!.Value-state.Daily!.Value)}",
                _=>"状态未知"
            };
            string? recovery=stamina>=1?"已回满":stamina.HasValue && state.FullAt is { } at && at>now?"回满 "+DailyReminderSchedule.Local(at,zone):null;
            c.Hint.Text=status+(recovery!=null?" · "+recovery:"");
            if(ef && state.DailyState=="incomplete" && daily.HasValue)
            {
                string hint=c.Hint.Text;
                if (c.Hint.Inlines.FirstInline is not System.Windows.Documents.Run { Text:"▲ " })
                {
                    c.Hint.Inlines.Clear(); c.Hint.Inlines.Add(new System.Windows.Documents.Run("▲ "){Foreground=Paint("#AA8200")});
                    c.Hint.Inlines.Add(new System.Windows.Documents.Run(hint));
                }
            }
            c.Hint.Visibility=account?Visibility.Visible:Visibility.Collapsed;
            c.Login.Visibility=account?Visibility.Collapsed:Visibility.Visible; c.Login.IsEnabled=!state.Busy && !_actions.Contains(game);
            var caption=_twinScenario=="long"?new string('超',90)+"演示长版本名":VersionCaption.For(_eventStore?.Data,game,now);
            c.Version.Text=caption; c.Version.ToolTip=caption;
            c.VersionBar.Visibility=caption.Length==0?Visibility.Collapsed:Visibility.Visible;
            bool stale=state.Stale || !state.Demo && !_preview && state.UpdatedAt is { } updated && now-updated>TimeSpan.FromMinutes(ef?15:state.RefreshMinutes+1);
            var warning=_localNotes.GetValueOrDefault(game) ?? state.Error ?? (stale?"数据可能过期，请刷新":null);
            var badge=warning==null?"":state.Error!=null?"⚠ 更新失败":stale?"⚠ 数据过期":"⚠ 提示";
            c.Badge.Text=state.Demo?(badge.Length>0?badge+" · 演示":"演示"):badge;
            c.Status.ToolTip=warning ?? (state.Demo?"演示数据":null);
            c.Status.Visibility=c.Badge.Text.Length>0?Visibility.Visible:Visibility.Collapsed;
        }
        RefreshOverviewArt();
        RefreshDeadlineRows(now,zone);
        UpdateTwinStrip();
    }

    internal FrameworkElement BuildTwinStrip() => _twinStrip;
    internal void SetTwinStripEdge(EdgeDockSide side) { _twinEdge=side; UpdateTwinStrip(); }
    internal void UpdateTwinStrip() => _twinStrip.Set(TwinReadings.DailyFraction(OverviewState("wuwa")), TwinReadings.DailyFraction(OverviewState("endfield")),_twinEdge,Math.Clamp(_settings.Scale,.5,2.5),_card.BorderThickness.Top);

    private void TryOverviewClick(Point start, MouseButtonEventArgs e)
    {
        if (!IsOverview || e.ChangedButton!=MouseButton.Left) return;
        var pos=PointToScreen(e.GetPosition(this)); var dpi=VisualTreeHelper.GetDpi(this);
        if (Math.Abs(start.X-pos.X)>SystemParameters.MinimumHorizontalDragDistance*dpi.DpiScaleX ||
            Math.Abs(start.Y-pos.Y)>SystemParameters.MinimumVerticalDragDistance*dpi.DpiScaleY) return;
        OpenOverviewHalf(e.GetPosition(_twinCanvas));
    }

    private void OpenOverviewHalf(Point p)
    {
        if (p.Y>=0 && p.Y<372 && p.X>=0 && p.X<560) OpenGamePage(p.X<SeamX(p.Y)?"wuwa":"endfield");
    }
    private static double SeamX(double y) => 336-112*y/372;
}
