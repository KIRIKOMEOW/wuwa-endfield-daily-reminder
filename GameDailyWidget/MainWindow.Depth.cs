using System;
using System.Linq;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GameDailyWidget;

public sealed partial class MainWindow
{
    private readonly Canvas _tileShadows=new(){IsHitTestVisible=false};
    private readonly DepthLayers _depth = new();
    private sealed record Plate(Rect Bounds,Geometry Face,Brush Color,int Offset,bool Tile);

    private sealed class DepthLayers : FrameworkElement
    {
        internal List<Plate> Plates=[];
        protected override void OnRender(DrawingContext dc)
        {
            foreach(var p in Plates)
            {
                var b=p.Bounds;
                dc.PushTransform(new TranslateTransform(b.X,b.Y));
                void Back(double offset,Brush color)
                {
                    var shadow=p.Face.Clone();
                    shadow.Transform=new TranslateTransform(offset,offset);
                    var edge=new CombinedGeometry(GeometryCombineMode.Exclude,shadow,p.Face);
                    dc.DrawGeometry(color,null,edge);
                }
                Back(p.Offset,p.Color);
                if(p.Tile)
                {
                    var outline=new Pen(Paint("#8C141414"),1);
                    dc.DrawGeometry(null,outline,p.Face);
                    dc.DrawLine(new Pen(Brushes.White,1),new(2,1),new(b.Width-15,1));
                    var pen=new Pen(Ink,2);
                    foreach(double y in new[]{5d,b.Height-5})
                    {
                        dc.DrawLine(pen,new(5,y),new(14,y));
                        dc.DrawLine(pen,new(5,y),new(5,y+(y==5?9:-9)));
                    }
                    for(int x=0;x<16;x+=5) dc.DrawRectangle(Ink,null,new Rect(b.Width-34+x,7,3,4));
                }
                dc.Pop();
            }
        }
    }
    private void RefreshDepth()
    {
        if(_overviewOpen || !IsEndfield || !_depth.IsDescendantOf(_singlePage)) return;
        var plates=new List<Plate>();
        void PlateFor(FrameworkElement element,int offset,Brush color,bool tile=false)
        {
            if(element.Visibility!=Visibility.Visible || element.ActualWidth<=0 || element.ActualHeight<=0) return;
            var pos=element.TranslatePoint(new Point(),_depth);
            var rect=new Rect(pos,element.RenderSize);
            var shape=element.Clip ?? new RectangleGeometry(new Rect(element.RenderSize));
            plates.Add(new(rect,shape,color,offset,tile));
        }
        PlateFor(_staminaBox,5,Ink,true);PlateFor(_dailyBox,5,Ink,true);
        PlateFor(_versionBar,3,SafetyYellow);PlateFor(_streakChip,3,SafetyYellow);PlateFor(_warningChip,3,Ink);
        PlateFor(_summaryButton,3,_summaryButton.Background==Ink?SafetyYellow:Ink);
        PlateFor(_segments,3,Ink);
        foreach(var b in _windowButtons.Append(_backButton).Append(_refresh).Append(_expand).Append(_poolsButton).Append(_eventsButton))
            PlateFor(b,IsPanelDown(b)?1:3,Ink);
        if (_depth.Plates.Count==plates.Count && _depth.Plates.Zip(plates).All(pair=>pair.First.Bounds==pair.Second.Bounds &&
            pair.First.Color==pair.Second.Color && pair.First.Offset==pair.Second.Offset)) return;
        _tileShadows.Children.Clear();
        foreach(var tile in plates.Where(p=>p.Tile))
        {
            var region=new Canvas { Clip=new CombinedGeometry(GeometryCombineMode.Exclude,
                new RectangleGeometry(new Rect(-6,-6,tile.Bounds.Width+20,tile.Bounds.Height+20)),tile.Face) };
            var shadow=new System.Windows.Shapes.Path {Data=tile.Face,Fill=Ink,Opacity=.22,
                Effect=new System.Windows.Media.Effects.BlurEffect {Radius=9},RenderTransform=new TranslateTransform(4,6)};
            Canvas.SetLeft(region,tile.Bounds.Left);Canvas.SetTop(region,tile.Bounds.Top);
            region.Children.Add(shadow);_tileShadows.Children.Add(region);
        }
        _depth.Plates=plates;_depth.InvalidateVisual();
    }
    private bool IsPanelDown(Button b) => IsEndfield && _expanded &&
        (b==_eventsButton && _eventsOpen || b==_poolsButton && _poolsOpen || b==_expand && !_poolsOpen && !_eventsOpen);
    private void SetPanelDepth()
    {
        foreach(var b in new[]{_eventsButton,_poolsButton,_expand})
            b.RenderTransform=IsPanelDown(b)?new TranslateTransform(2,2):Transform.Identity;
        _tileShadows.Visibility=_depth.Visibility=IsEndfield?Visibility.Visible:Visibility.Collapsed;
        if(_singlePage!=null) RefreshDepth();
    }
    private static Border OverviewPlate(FrameworkElement face,Brush color,int offset)
    {
        var plate=new Border { Background=color,IsHitTestVisible=false,HorizontalAlignment=face.HorizontalAlignment,
            VerticalAlignment=face.VerticalAlignment,RenderTransform=new TranslateTransform(offset,offset) };
        foreach(var (property,path) in new[]{(WidthProperty,"ActualWidth"),(HeightProperty,"ActualHeight"),(VisibilityProperty,"Visibility"),(ClipProperty,"Clip")})
            plate.SetBinding(property,new System.Windows.Data.Binding(path){Source=face});
        return plate;
    }
}
