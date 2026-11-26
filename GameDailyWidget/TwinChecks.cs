using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GameDailyWidget;

public sealed partial class MainWindow
{
    internal object CheckOverviewLayout()
    {
        if (!IsOverview || _twinCanvas.ActualWidth!=560 || _twinCanvas.ActualHeight!=372)
            throw new InvalidOperationException("总览画布未按 560 × 372 排列");
        int count=0;
        static IEnumerable<FrameworkElement> Elements(DependencyObject node)
        {
            if (node is UIElement { Visibility:not Visibility.Visible }) yield break;
            if (node is FrameworkElement e) yield return e;
            for (int i=0;i<VisualTreeHelper.GetChildrenCount(node);i++)
                foreach (var child in Elements(VisualTreeHelper.GetChild(node,i))) yield return child;
        }
        foreach (var (game,col) in _twinColumns)
        {
            var own=Elements(col.Canvas).Concat(Elements(col.Credit));
            if (col.Light) own=own.Concat(_twinCanvas.Children.OfType<Button>().Where(b=>Canvas.GetTop(b)<36));
            foreach (var e in own.Where(e=>e.ActualWidth>0 && e.ActualHeight>0))
            {
                var bounds=e.TransformToAncestor(_twinCanvas).TransformBounds(new Rect(e.RenderSize));
                bool inside=col.Light ? bounds.Left>=Math.Max(SeamX(bounds.Top),SeamX(bounds.Bottom))+12
                    : bounds.Right<=Math.Min(SeamX(bounds.Top),SeamX(bounds.Bottom))-12;
                if (!inside || bounds.Left<0 || bounds.Right>560.01 || bounds.Top<0 || bounds.Bottom>262.01)
                    throw new InvalidOperationException($"总览 {game} 的 {e.GetType().Name} 越过分区 {bounds}");
                count++;
            }
            if (_twinScenario=="long" && (TextWidth(col.Version)<=col.Version.ActualWidth ||
                col.Version.ToolTip?.ToString()!=col.Version.Text || col.Version.TextTrimming!=TextTrimming.CharacterEllipsis))
                throw new InvalidOperationException("长版本名未按列宽省略或缺少全文提示");
            var state=OverviewState(game);
            if ((col.Login.Visibility==Visibility.Visible)==TwinReadings.HasAccount(state)) throw new InvalidOperationException("登录按钮状态");
            if (col.Ring.ActualWidth!=86 || col.Ring.ActualHeight!=86) throw new InvalidOperationException("体力环尺寸");
            if (!TwinReadings.DailyFraction(state).HasValue && (col.Daily.Text!="—" || col.Segments.Any(s=>s.Width!=0)))
                throw new InvalidOperationException("未知活跃度显示成实测值");
        }
        foreach(var ((game,_),row) in _summaryRows)
        {
            var b=row.Button.TransformToAncestor(_twinCanvas).TransformBounds(new Rect(row.Button.RenderSize));
            var label=row.Name.TransformToAncestor(_twinCanvas).TransformBounds(new Rect(row.Name.RenderSize));
            var track=row.Track.TransformToAncestor(_twinCanvas).TransformBounds(new Rect(row.Track.RenderSize));
            if(label.Bottom+1>track.Top) throw new InvalidOperationException("截止条遮挡名称");
            if(game=="wuwa"?b.Right+12>SeamX(b.Bottom):b.Left-12<SeamX(b.Top)) throw new InvalidOperationException("底部摘要越界");
        }
        if(count<30) throw new InvalidOperationException("总览可见控件数量异常");
        return new { scale=_settings.Scale,state=_twinScenario,controls=count,labels=_summaryRows.Count,diagonal=true };
    }

    internal void CheckTwinTargets()
    {
        foreach (var (pos,game) in new[]{ (new Point(20,180),"wuwa"),(new Point(410,110),"endfield") })
        {
            OpenOverviewHalf(pos);
            if (IsOverview || _currentGame!=game || Width!=560) throw new InvalidOperationException("双区命中或卡片宽度");
            _backButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (!IsOverview || Width!=560) throw new InvalidOperationException("返回总览按钮");
        }
        OpenOverviewHalf(new Point(30,310));
        if (IsOverview || _currentGame!="wuwa") throw new InvalidOperationException("底部半区导航");
        ShowOverview();
        foreach (var ((game,pool),row) in _summaryRows)
        {
            var button=row.Button;
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (IsOverview || _currentGame!=game || (pool?!_poolsOpen:!_eventsOpen) || Width!=560)
                throw new InvalidOperationException("时间轴项目进入了错误的游戏或面板");
            ShowOverview();
        }
    }
}

internal static class TwinChecks
{
    private static void Assert(bool ok,string name)
    {
        if(!ok) throw new InvalidOperationException("双生总览检查失败 · "+name);
    }

    private static void CheckFloor()
    {
        foreach(bool light in new[]{false,true})
        {
            var world=new Grid { Background=light?Brushes.Black:Brushes.White };
            MainWindow.AddWorldFloor(world,light);
            world.Measure(new Size(560,372));world.Arrange(new Rect(0,0,560,372));
            var bmp=new RenderTargetBitmap(560,372,96,96,PixelFormats.Pbgra32); bmp.Render(world);
            var bytes=new byte[560*372*4];bmp.CopyPixels(bytes,560*4,0);
            int Sample(int y,int ch)=>bytes[(y*560+(light?489:71))*4+ch];
            for(int ch=0;ch<3;ch++)
            {
                Assert(Math.Abs(Sample(261,ch)-Sample(263,ch))<6,"立绘和底部衔接像素");
                Assert(Math.Abs(Sample(201,ch)-Sample(261,ch))>100,"立绘末段确实渐变");
            }
        }
    }

    internal static IEnumerable<string> Run()
    {
        var at=new DateTimeOffset(2026,3,8,3,59,59,DailyReminderSchedule.China);
        var beijing=TimeZoneInfo.CreateCustomTimeZone("demo-plus-eight",TimeSpan.FromHours(8),"demo","demo");
        var west=TimeZoneInfo.CreateCustomTimeZone("demo-minus-eight",TimeSpan.FromHours(-8),"demo","demo");
        var daylight=TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
        Assert(TwinReadings.Countdown(at)=="00:00:01" && TwinReadings.Countdown(at.AddSeconds(1))=="24:00:00" &&
            TwinReadings.Countdown(at.AddSeconds(2))=="23:59:59","04:00 边界");
        Assert(TwinReadings.ResetCaption(at,beijing)=="北京时间 04:00" &&
            TwinReadings.ResetCaption(at,west)=="北京 04:00 · 本地 12:00" &&
            TwinReadings.ResetCaption(at.AddDays(2),daylight)=="北京 04:00 · 本地 13:00","固定时区与夏令时");

        var now=DateTimeOffset.UtcNow;
        CheckFloor();
        var strip=new TwinStrip();
        Color Pixel(int x,int y)
        {
            strip.Measure(new Size(16,100)); strip.Arrange(new Rect(0,0,16,100)); strip.UpdateLayout();
            var bitmap=new RenderTargetBitmap(16,100,96,96,PixelFormats.Pbgra32); bitmap.Render(strip);
            byte[] b=new byte[4]; bitmap.CopyPixels(new Int32Rect(x,y,1,1),b,4,0);
            return Color.FromArgb(b[3],b[2],b[1],b[0]);
        }
        strip.Measure(new Size(16,100)); strip.Arrange(new Rect(0,0,16,100));
        strip.Set(.5,.25,EdgeDockSide.Left);
        Assert(Pixel(4,10)==(Color)ColorConverter.ConvertFromString("#2A2838") && Pixel(4,40)==(Color)ColorConverter.ConvertFromString("#D6BB87") &&
            Pixel(4,50)==Colors.Black && Pixel(4,55)==(Color)ColorConverter.ConvertFromString("#E9E7DF") &&
            Pixel(4,99)==(Color)ColorConverter.ConvertFromString("#F5E200"),"两段从底填充与 2px 分隔");
        strip.Set(null,null,EdgeDockSide.Right);
        Assert(Enumerable.Range(0,6).Select(x=>Pixel(x,12)).Distinct().Count()>1 &&
            Enumerable.Range(0,6).Select(x=>Pixel(x,72)).Distinct().Count()>1,"两端未知斜纹");
        foreach(var edge in new[]{EdgeDockSide.Top,EdgeDockSide.Bottom})
        {
            strip.Set(1,1,edge);
            int y=edge==EdgeDockSide.Top?92:0;
            Assert(Pixel(4,y+1)==(Color)ColorConverter.ConvertFromString("#D6BB87") && Pixel(4,y+4)==Colors.Black &&
                Pixel(4,y+6)==(Color)ColorConverter.ConvertFromString("#F5E200"),"上下屏幕边缘的可见条");
        }

        var settings=UnifiedSettings.Load(true);
        using var wu=new WuwaController(settings,true,true);
        using var ef=new EndfieldController(settings,true,true);
        wu.InitializeAsync().GetAwaiter().GetResult(); ef.InitializeAsync().GetAwaiter().GetResult();
        var widget=new MainWindow(settings,wu,ef,true);
        try
        {
            var store=new EventStore();
            var source=new EventSource("demo","演示公告","");
            store.Data.Events.Add(new("demo","wuwa",new string('长',100),"演示",now.AddDays(-1),now.AddHours(2),false,null,
                new("演示",null),source,"演示",now.AddDays(-2)));
            store.Data.Events.Add(store.Data.Events[0] with { Key="ignored",Name="忽略" });
            store.Data.Events.Add(store.Data.Events[0] with { Key="claimed",Name="已领演示",End=now.AddDays(5) });
            store.Data.Marks["claimed"]=new(true,false,now);
            store.Data.Events.Add(store.Data.Events[0] with { Key="future",Name="后续演示",Start=now.AddDays(7),End=now.AddDays(20) });
            store.Data.Marks["ignored"]=new(false,true,now);
            store.Data.Pools.Add(new("pool","endfield","演示池","干员",now.AddDays(2),now.AddDays(17),false,
                [new("演示干员",6,"干员")],source,"演示",now));
            DeadlineChecks.Run(store.Data,now);
            widget.BindEvents(store,null);
            var card=(FrameworkElement)widget.Content; widget.Content=null;
            foreach (double scale in new[]{.5,1,1.5,2.5})
            {
                widget.SetScale(scale);
                foreach(var scenario in new[]{"normal","wuwa-empty","endfield-empty","wuwa-failed","endfield-failed","locked","complete","long","demo","unknown","no-pools","no-events","urgent","pending","next"})
                {
                    widget.PreviewFraming(scenario=="normal"?"wuwa":"endfield"); widget.SetOverviewPreview(scenario); widget.UpdateOverview(now);
                    var size=new Size(widget.Width,widget.Height); card.Measure(size);card.Arrange(new Rect(size));card.UpdateLayout();
                    widget.CheckOverviewLayout();
                    Assert(Math.Abs(widget.Width-560*scale)<.01 && Math.Abs(widget.Height-372*scale)<.01,"逻辑尺寸");
                }
            }
            widget.SetScale(1);

            widget.CheckTwinTargets();
            widget.CheckFramingButtons();
        }
        finally { widget.PermitClose(); widget.Close(); }
        return ["PASS 总览四档缩放 × 十五种状态的斜线、长名截断、登录与未知值几何检查",
            "PASS 截止摘要选择、两行位置与面板跳转",
            "PASS 上下左右贴边小条分段像素、比例和未知斜纹","PASS 北京换日倒计时、跨日、UTC+8、UTC-8 与夏令时"];
    }
}
