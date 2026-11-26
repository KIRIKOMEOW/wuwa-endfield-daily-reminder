using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Input;

namespace GameDailyWidget;

public sealed partial class MainWindow
{
    private sealed record SummaryRow(Button Button,TextBlock Name,TextBlock Time,Grid Track,Border Fill,Line Dash)
    {
        public DeadlineSummary? Value { get; set; }
    }
    private readonly Dictionary<(string Game,bool Pool),SummaryRow> _summaryRows=new();

    private void BuildDeadlineRows(string game,bool light)
    {
        double left=light?286:18;
        var cap=Label(light?"BANNER · DEADLINE   卡池 · 最近截止":"卡池 · 最近截止",9.5,light?PaperMuted:GoldText);
        if(light) cap.FontFamily=Mono;
        TwinPlace(_twinCanvas,cap,left,270);
        foreach(bool pool in new[]{true,false})
        {
            double y=pool?291:326;
            double width=light?542-left:Math.Floor(SeamX(y+28)-12-left);
            var button=MakeButton("",""); button.Cursor=Cursors.Hand; button.Template=Chrome(0);
            button.Background=Brushes.Transparent; button.BorderBrush=Brushes.Transparent;button.Padding=new Thickness(2,1,2,1);
            button.HorizontalContentAlignment=HorizontalAlignment.Stretch;
            var hover=light?Paint("#0D141414"):Paint("#0FFFFFFF");
            button.MouseEnter+=(_,_)=>button.Background=hover;
            button.MouseLeave+=(_,_)=>button.Background=Brushes.Transparent;
            button.Foreground=light?Ink:Sand;
            var grid=new Grid {Height=22}; var line=new DockPanel {VerticalAlignment=VerticalAlignment.Top};
            var time=Label("",10,light?PaperMuted:NightMuted); DockPanel.SetDock(time,Dock.Right);line.Children.Add(time);
            time.Margin=new Thickness(6,0,0,0);
            var name=Label("",11,button.Foreground,pool?FontWeights.Bold:FontWeights.Normal); Trim(name);
            if(pool && light) line.Children.Add(new Border {Width=8,Height=8,Background=SafetyYellow,BorderBrush=Ink,BorderThickness=new Thickness(1),Margin=new Thickness(0,0,4,0)});
            line.Children.Add(name);grid.Children.Add(line);
            var track=new Grid {Height=3,VerticalAlignment=VerticalAlignment.Bottom,Background=light?Hazard:Paint("#1FFFFFFF")};
            var fill=new Border {HorizontalAlignment=HorizontalAlignment.Left};track.Children.Add(fill);
            var dash=new Line {X2=1,Y1=1,Y2=1,Stretch=Stretch.Fill,Stroke=light?Ink:Gold,StrokeThickness=1,StrokeDashArray=new DoubleCollection{4,3}};
            track.Children.Add(dash);grid.Children.Add(track);
            var row=new SummaryRow(button,name,time,track,fill,dash);_summaryRows[(game,pool)]=row;
            button.Content=grid; button.Click+=(_,_)=>{
                OpenGamePage(game,pool?"pools":"events");
                if(!pool && row.Value?.Key is {} key)
                    _eventsPanel.Children.OfType<FrameworkElement>().FirstOrDefault(e=>Equals(e.Tag,key))?.BringIntoView();
            };
            TwinPlace(_twinCanvas,button,left,y,width,25);
        }
    }

    private void RefreshDeadlineRows(DateTimeOffset now,TimeZoneInfo zone)
    {
        foreach(var ((game,pool),row) in _summaryRows)
        {
            var data=_previewSummaries??_eventStore?.Data;
            var value=pool?DeadlineSummary.Pools(data==null?[]:PoolCatalog.Current(data,game,now),now,zone):DeadlineSummary.Activity(data,game,now,zone);
            if(_preview && _twinScenario=="long") value=value with {Name=new string('长',90),Tip=new string('长',90)};
            if(row.Value==value) continue;
            row.Value=value; bool light=game=="endfield";
            row.Name.Text=pool && !light && value.Name.StartsWith("UP " )?"★ "+value.Name:value.Name;
            row.Time.Text=value.Time;row.Button.ToolTip=value.Tip;
            row.Name.Foreground=value.Urgent?Coral:light?Ink:pool?Sand:TextColor;
            row.Time.Foreground=value.Urgent?Coral:light?PaperMuted:NightMuted;
            row.Fill.Background=value.Urgent?Coral:light?Ink:TwinGold;
            row.Fill.Effect=value.Urgent || !light ? new System.Windows.Media.Effects.DropShadowEffect {Color=((SolidColorBrush)row.Fill.Background).Color,BlurRadius=4,ShadowDepth=0} : null;
            row.Fill.Width=(row.Button.Width-4)*(value.Fraction??0);
            row.Dash.Visibility=value.Fraction.HasValue?Visibility.Collapsed:Visibility.Visible;
        }
    }

    internal static void AddWorldFloor(Grid world,bool light)
    {
        var color=((SolidColorBrush)Paint(light?"#EEECE4":"#090A1C")).Color;
        var fade=new LinearGradientBrush(Color.FromArgb(0,color.R,color.G,color.B),color,90);
        var canvas=new Canvas {IsHitTestVisible=false};
        TwinPlace(canvas,new Border{Background=fade},0,204,560,58);
        TwinPlace(canvas,new Border{Background=new SolidColorBrush(color)},0,262,560,110);
        if(light)
        {
            var grid=Paint("#0D141414");
            for(int x=0;x<560;x+=14) TwinPlace(canvas,new Border{Background=grid},x,262,1,110);
            for(int y=262;y<372;y+=14) TwinPlace(canvas,new Border{Background=grid},0,y,560,1);
        }
        else TwinPlace(canvas,new Border {Background=new RadialGradientBrush(Color.FromArgb(26,214,187,135),Colors.Transparent){Center=new Point(0,1),GradientOrigin=new Point(0,1),RadiusX=.6,RadiusY=.8}},0,262,560,110);
        world.Children.Add(canvas);
    }
}
