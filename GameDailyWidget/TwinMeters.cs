using System;
using System.Windows;
using System.Windows.Media;

namespace GameDailyWidget;

internal sealed class TwinRing(bool light) : FrameworkElement
{
    private double? _fraction;
    private static SolidColorBrush BrushOf(byte a, byte r, byte g, byte b)
    {
        var paint = new SolidColorBrush(Color.FromArgb(a,r,g,b)); paint.Freeze(); return paint;
    }
    private static readonly Brush Gold = BrushOf(255,214,187,135), Dark = BrushOf(255,20,20,20);
    private static readonly Brush WuFill = BrushOf(128,16,16,40), EfFill = BrushOf(192,250,250,246);
    private static Pen Stroke(Brush brush, double width, bool round = false)
    {
        var pen = new Pen(brush,width);
        if (round) pen.StartLineCap = pen.EndLineCap = PenLineCap.Round;
        pen.Freeze(); return pen;
    }
    private static readonly Pen WuTrack = Stroke(BrushOf(36,255,255,255),4.5), EfTrack = Stroke(BrushOf(255,217,214,204),4.5),
        WuArc = Stroke(Gold,4.5,true), EfArc = Stroke(Dark,4.5), Glow = Stroke(BrushOf(45,214,187,135),9,true);
    internal void Set(double? fraction)
    {
        if (_fraction == fraction) return;
        _fraction = fraction;
        InvalidateVisual();
    }
    protected override void OnRender(DrawingContext dc)
    {
        dc.PushTransform(new ScaleTransform(RenderSize.Width/96,RenderSize.Height/96));
        DrawRing(dc);
        dc.Pop();
    }
    private void DrawRing(DrawingContext dc)
    {
        var center = new Point(48,48);
        dc.DrawEllipse(light ? EfFill : WuFill, light ? EfTrack : WuTrack,center,42,42);
        if (_fraction is not { } fraction || fraction<=0) return;
        var pen = light ? EfArc : WuArc;
        if (fraction >= 1)
        {
            if (!light) dc.DrawEllipse(null,Glow,center,42,42);
            dc.DrawEllipse(null,pen,center,42,42); return;
        }
        double angle = fraction * Math.Tau - Math.PI / 2;
        var geo = new StreamGeometry();
        using (var path = geo.Open())
        {
            path.BeginFigure(new Point(48,6),false,false);
            path.ArcTo(new Point(48 + 42 * Math.Cos(angle),48 + 42 * Math.Sin(angle)),new Size(42,42),
                0,fraction > .5,SweepDirection.Clockwise,true,false);
        }
        geo.Freeze();
        if (!light) dc.DrawGeometry(null,Glow,geo);
        dc.DrawGeometry(null,pen,geo);
    }
}

internal sealed class TwinStrip : FrameworkElement
{
    private static SolidColorBrush ColorOf(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b;
    }
    private static readonly Brush Gold = ColorOf("#D6BB87"), Yellow = ColorOf("#F5E200"),
        WuTrack = ColorOf("#2A2838"), EfTrack = ColorOf("#E9E7DF");
    private static DrawingBrush Hatch(Brush track, Brush ink)
    {
        var drawing = new DrawingGroup();
        using (var dc = drawing.Open())
        {
            dc.DrawRectangle(track,null,new Rect(0,0,6,6));
            var pen = new Pen(ink,2);
            dc.DrawLine(pen,new Point(-1,1),new Point(1,-1));
            dc.DrawLine(pen,new Point(-1,7),new Point(7,-1));
            dc.DrawLine(pen,new Point(5,7),new Point(7,5));
        }
        drawing.ClipGeometry = new RectangleGeometry(new Rect(0,0,6,6));
        var brush = new DrawingBrush(drawing) { TileMode=TileMode.Tile, ViewportUnits=BrushMappingMode.Absolute, Viewport=new Rect(0,0,6,6) };
        brush.Freeze(); return brush;
    }
    private static readonly Brush WuUnknown = Hatch(WuTrack,ColorOf("#7F705A")), EfUnknown = Hatch(EfTrack,ColorOf("#A09B89"));
    private double? _wu, _ef;
    private EdgeDockSide _edge;
    private double _exposedHeight=EdgeDockGeometry.DefaultStripDip, _divider=2;
    internal void Set(double? wu, double? ef, EdgeDockSide edge, double scale=1, double outerBorder=0)
    {
        double exposed=Math.Max(0,EdgeDockGeometry.DefaultStripDip/scale-outerBorder), divider=2/scale;
        if ((_wu,_ef,_edge,_exposedHeight,_divider) == (wu,ef,edge,exposed,divider)) return;
        (_wu,_ef,_edge,_exposedHeight,_divider)=(wu,ef,edge,exposed,divider);
        InvalidateVisual();
    }
    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth <= 0 || ActualHeight < 2) return;
        double height = _edge is EdgeDockSide.Top or EdgeDockSide.Bottom ? Math.Min(ActualHeight,_exposedHeight) : ActualHeight;
        double y = _edge == EdgeDockSide.Top ? ActualHeight-height : 0, half = Math.Max(0,(height-_divider)/2);
        dc.DrawRectangle(Brushes.Black,null,new Rect(RenderSize));
        Draw(y,_wu,WuTrack,Gold,WuUnknown);
        Draw(y+half+_divider,_ef,EfTrack,Yellow,EfUnknown);
        void Draw(double top,double? fraction,Brush track,Brush fill,Brush unknown)
        {
            dc.DrawRectangle(fraction == null ? unknown : track,null,new Rect(0,top,ActualWidth,half));
            if (fraction is > 0) dc.DrawRectangle(fill,null,new Rect(0,top+half*(1-fraction.Value),ActualWidth,half*fraction.Value));
        }
    }
}
