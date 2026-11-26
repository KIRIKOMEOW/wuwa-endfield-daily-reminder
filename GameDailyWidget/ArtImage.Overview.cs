using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GameDailyWidget;

// The six rows share one frozen texture. Only the bounding box of this world's
// polygon is stored. The overview Border clips off the remaining triangular corner.
internal sealed class OverviewArtFrames
{
    internal const int Count = 6;
    internal static int SideWidth(int width) => (int)Math.Ceiling(.60 * width);
    internal readonly BitmapSource Atlas;
    private readonly Brush[] _phases = new Brush[Count];

    internal OverviewArtFrames(BitmapSource atlas, string game, int width)
    {
        Atlas = atlas;
        double side = atlas.PixelWidth / (double)width;
        for (int i = 0; i < Count; i++)
        {
            var brush = new ImageBrush(atlas)
            {
                ViewboxUnits = BrushMappingMode.RelativeToBoundingBox, Viewbox = new Rect(0, i / (double)Count, 1, 1d / Count),
                ViewportUnits = BrushMappingMode.RelativeToBoundingBox, Viewport = new Rect(game == "wuwa" ? 0 : 1 - side, 0, side, 1),
                TileMode = TileMode.None, Stretch = Stretch.Fill
            };
            brush.Freeze();
            _phases[i] = brush;
        }
    }

    internal Brush this[int phase] => _phases[phase];
    internal static Point Anchor(string game) => game == "wuwa" ? new(.38, .30) : new(.64, .32);
    internal static int Phase(GameViewState state, bool enabled)
    {
        if (!enabled || !state.LoggedIn && !state.Demo || state.Stale || !string.IsNullOrEmpty(state.Error) ||
            state.DailyState is not ("incomplete" or "complete") || state.Daily is not int daily || daily < 0 ||
            state.MaxDaily is not int max || max <= 0) return Count - 1;
        return (int)(Math.Min(daily, max) * 5L / max);
    }
    internal static (double Saturation, double Brightness) Factors(int phase)
    {
        double remaining = (Count - 1 - phase) / (double)(Count - 1);
        return (1 - .35 * remaining, 1 - .10 * remaining);
    }
}

internal static partial class ArtImage
{
    internal static OverviewArtFrames? ProcessOverview(BitmapSource source, string game, ArtCrop crop, int strength, int width, int height)
    {
        if (strength <= 0) return null;
        var light = game == "endfield";
        var bg = light ? Color.FromRgb(238, 236, 228) : Color.FromRgb(9, 10, 28);
        var anchor = OverviewArtFrames.Anchor(game);
        var original = FramePixels(source, crop, ref width, ref height, anchor.X, anchor.Y);
        int side = OverviewArtFrames.SideWidth(width);
        int origin = light ? width - side : 0;
        var keep = new double[side];
        var adaptive = new double[side];
        for (int x = 0; x < side; x++)
        {
            var position = (origin + x + .5) / width;
            double outer = light ? 1 - position : position;
            double shade = Ramp(outer, light ? .95 : .93, light ? .76 : .72, light ? .06 : .10, .26, .46);
            keep[x] = (1 - shade) * Math.Clamp(strength, 0, 100) / 100d;
            // Full protection over the outer data column, feathered towards the characters.
            adaptive[x] = Math.Clamp((.34 - outer) / .07, 0, 1);
        }
        double body = light ? Lum(90, 90, 90) : Lum(180, 185, 190);
        double limit = light ? (body + .05) * 4.5 - .05 : (body + .05) / 4.5 - .05;
        var atlas = new byte[OverviewArtFrames.Count * height * side * 4];
        for (int phase = 0; phase < OverviewArtFrames.Count; phase++)
        {
            var (saturation, brightness) = OverviewArtFrames.Factors(phase);
            for (int y = 0; y < height; y++)
                for (int x = 0; x < side; x++)
                {
                    int src = 4 * (y * width + origin + x);
                    int dst = 4 * ((phase * height + y) * side + x);
                    double gray = original[src] * .0722 + original[src + 1] * .7152 + original[src + 2] * .2126;
                    double opacity = keep[x] * original[src + 3] / 255d;
                    double colorFactor = saturation * brightness * opacity;
                    double grayPart = gray * (1 - saturation) * brightness * opacity;
                    double bgPart = 1 - opacity;
                    byte b = (byte)Math.Round(bg.B * bgPart + original[src] * colorFactor + grayPart);
                    byte g = (byte)Math.Round(bg.G * bgPart + original[src + 1] * colorFactor + grayPart);
                    byte r = (byte)Math.Round(bg.R * bgPart + original[src + 2] * colorFactor + grayPart);
                    if (adaptive[x] > 0 && (light ? Lum(b, g, r) < limit : Lum(b, g, r) > limit))
                    {
                        byte cb = b, cg = g, cr = r;
                        do { cb = Blend(cb, bg.B, .12); cg = Blend(cg, bg.G, .12); cr = Blend(cr, bg.R, .12); }
                        while (light ? Lum(cb, cg, cr) < limit : Lum(cb, cg, cr) > limit);
                        b = Blend(b, cb, adaptive[x]); g = Blend(g, cg, adaptive[x]); r = Blend(r, cr, adaptive[x]);
                    }
                    atlas[dst] = b; atlas[dst + 1] = g; atlas[dst + 2] = r; atlas[dst + 3] = 255;
                }
        }
        return new(Bitmap(side, height * OverviewArtFrames.Count, atlas), game, width);
    }
}
