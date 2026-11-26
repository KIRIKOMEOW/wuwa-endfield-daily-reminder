using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GameDailyWidget;

public sealed record ArtCrop(double X = .5, double Y = .3, double Zoom = ArtCrop.DefaultZoom)
{
    public const double DefaultZoom = 1.25;
    // Endfield version covers put the characters at the top and a large title
    // banner across the lower middle; frame the upper part so the banner stays out.
    internal static ArtCrop DefaultFor(string game) => game == "endfield" ? new(.6, .15, 1.35) : new();
    public ArtCrop Clamp() => new(Limit(X, 0, 1, .5), Limit(Y, 0, 1, .3), Limit(Zoom, 1, 3, DefaultZoom));
    private static double Limit(double v, double min, double max, double fallback) =>
        double.IsFinite(v) ? Math.Clamp(v, min, max) : fallback;
}

internal sealed record ArtLayers(BitmapSource Clear, BitmapSource Shaded, BitmapSource Blurred, double ExtraShade);
internal static partial class ArtImage
{
    internal const int MaxBytes = 12 * 1024 * 1024;
    internal static string? Type(ReadOnlySpan<byte> data)
    {
        if (data.StartsWith((ReadOnlySpan<byte>)[137, 80, 78, 71, 13, 10, 26, 10])) return "image/png";
        if (data.StartsWith((ReadOnlySpan<byte>)[255, 216, 255])) return "image/jpeg";
        if (data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WEBP"u8)) return "image/webp";
        return null;
    }

    internal static BitmapSource Decode(byte[] data, int edge)
    {
        if (data.Length > MaxBytes || Type(data) == null) throw new InvalidDataException("图片格式或大小不受支持");
        using var stream = new MemoryStream(data, false);
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
        var w = frame.PixelWidth;
        var h = frame.PixelHeight;
        if (w is < 1 or > 8000 || h is < 1 or > 8000) throw new InvalidDataException("图片边长超出限制");
        stream.Position = 0;
        var image = new BitmapImage();
        image.BeginInit();
        image.StreamSource = stream;
        image.CacheOption = BitmapCacheOption.OnLoad;
        if (w >= h) image.DecodePixelWidth = Math.Min(w, edge);
        else image.DecodePixelHeight = Math.Min(h, edge);
        image.EndInit();
        image.Freeze();
        return image;
    }

    internal static Rect Region(int w, int h, int cw, int ch, ArtCrop crop, double anchorX = .70, double anchorY = .22)
    {
        crop = crop.Clamp();
        var scale = Math.Max((double)cw / w, (double)ch / h) * crop.Zoom;
        var width = Math.Min(w, cw / scale);
        var height = Math.Min(h, ch / scale);
        return new(Math.Clamp(w * crop.X - width * anchorX, 0, w - width), Math.Clamp(h * crop.Y - height * anchorY, 0, h - height), width, height);
    }

    private static byte[] FramePixels(BitmapSource source, ArtCrop crop, ref int w, ref int h, double anchorX, double anchorY)
    {
        var rect = Region(source.PixelWidth, source.PixelHeight, w, h, crop, anchorX, anchorY);
        var sx = (int)rect.X; var sy = (int)rect.Y;
        var original = new CroppedBitmap(source, new Int32Rect(sx, sy,
            Math.Min(source.PixelWidth - sx, Math.Max(1, (int)Math.Round(rect.Width))),
            Math.Min(source.PixelHeight - sy, Math.Max(1, (int)Math.Round(rect.Height)))));
        var scaled = new TransformedBitmap(original, new ScaleTransform((double)w / original.PixelWidth, (double)h / original.PixelHeight));
        var formatted = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
        w = formatted.PixelWidth; h = formatted.PixelHeight;
        var pixels = new byte[w * h * 4];
        formatted.CopyPixels(pixels, w * 4, 0);
        return pixels;
    }

    private static double Ramp(double p, double start, double mid, double stop, double atMid, double atStop) =>
        p < atMid ? start + (mid - start) * p / atMid : p >= atStop ? stop : mid + (stop - mid) * (p - atMid) / (atStop - atMid);

    private static readonly double[] Linear = Enumerable.Range(0, 256).Select(i =>
        i / 255d <= .04045 ? i / 255d / 12.92 : Math.Pow((i / 255d + .055) / 1.055, 2.4)).ToArray();
    private static double Lum(byte b, byte g, byte r) => .2126 * Linear[r] + .7152 * Linear[g] + .0722 * Linear[b];
    private static byte Blend(byte value, byte target, double opacity) => (byte)Math.Round(value + (target - value) * opacity);

    internal static ArtLayers? Process(BitmapSource source, string game, ArtCrop crop, int strength, int w, int h)
    {
        if (strength <= 0) return null;
        bool light = game == "endfield";
        var bg = light ? Color.FromRgb(238, 236, 228) : Color.FromRgb(9, 10, 28);
        var pixels = FramePixels(source, crop, ref w, ref h, .70, .22);
        var clear = Bitmap(w, h, pixels);
        var rowShade = new double[h];
        var colShade = new double[w];
        for (int y = 0; y < h; y++) rowShade[y] = Ramp(1 - y / (double)h, light ? .95 : .92, light ? .55 : .35, 0, light ? .28 : .30, light ? .50 : .52);
        for (int x = 0; x < w; x++) colShade[x] = Ramp(x / (double)w, light ? .95 : .90, light ? .78 : .62, light ? .05 : .08, light ? .36 : .38, light ? .68 : .70);
        double left = 0, bottom = 0;
        int nl = 0, nb = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = 4 * (y * w + x);
                double opacity = 1 - (1 - rowShade[y]) * (1 - colShade[x]) * Math.Clamp(strength, 0, 100) / 100d * pixels[i + 3] / 255d;
                pixels[i] = Blend(pixels[i], bg.B, opacity);
                pixels[i + 1] = Blend(pixels[i + 1], bg.G, opacity);
                pixels[i + 2] = Blend(pixels[i + 2], bg.R, opacity);
                pixels[i + 3] = 255;
                if (x < w * .45 || y >= h * .65)
                {
                    var lum = Lum(pixels[i], pixels[i + 1], pixels[i + 2]);
                    if (x < w * .45) { left += lum; nl++; }
                    if (y >= h * .65) { bottom += lum; nb++; }
                }
            }
        // Secondary body text is the lower-contrast foreground in each theme.
        double fg = light ? Lum(90, 90, 90) : Lum(180, 185, 190);
        double limit = light ? (fg + .05) * 4.5 - .05 : (fg + .05) / 4.5 - .05;
        double average = light ? Math.Min(left / nl, bottom / nb) : Math.Max(left / nl, bottom / nb);
        double extra = light ? Math.Max(0, (limit - average) / (Lum(bg.B, bg.G, bg.R) - average + 1e-9)) :
            average > limit ? 1 - Math.Sqrt(Math.Max(0, limit / average)) : 0;
        if (light && average >= limit) extra = 0;
        extra = Math.Clamp(extra, 0, 1);
        if (extra > 0)
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = (y * w + x) * 4;
                    double mask = Math.Max(Math.Clamp((.58 - x / (double)w) / .13, 0, 1), Math.Clamp((y / (double)h - .52) / .13, 0, 1));
                    // Enforce the target on extreme pixels after the regional average adjustment.
                    double a = extra * mask;
                    var b = Blend(pixels[i], bg.B, a); var g = Blend(pixels[i + 1], bg.G, a); var r = Blend(pixels[i + 2], bg.R, a);
                    if (mask >= .999 && (light ? Lum(b, g, r) < limit : Lum(b, g, r) > limit))
                        for (int step = 0; step < 10 && (light ? Lum(b, g, r) < limit : Lum(b, g, r) > limit); step++)
                        { b = Blend(b, bg.B, .18); g = Blend(g, bg.G, .18); r = Blend(r, bg.R, .18); }
                    pixels[i] = b; pixels[i + 1] = g; pixels[i + 2] = r;
                }
        return new(clear, Bitmap(w, h, pixels), Blur(w, h, pixels), extra);
    }

    // Keeps the compact framing at the top of a taller (expanded) card and continues
    // with the theme base colour below it, so opening a panel never re-crops or
    // re-zooms the artwork. The last rows fade into the base to avoid a seam.
    internal static ArtLayers Extend(ArtLayers compact, string game, int height)
    {
        int frame = compact.Shaded.PixelHeight;
        if (height <= frame) return compact;
        var bg = game == "endfield" ? Color.FromRgb(238, 236, 228) : Color.FromRgb(9, 10, 28);
        BitmapSource Pad(BitmapSource image, int target)
        {
            int w = image.PixelWidth, h = image.PixelHeight;
            if (target <= h) return image;
            BitmapSource bgra = image.Format == PixelFormats.Bgra32 ? image : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            var pixels = new byte[w * target * 4];
            bgra.CopyPixels(pixels, w * 4, 0);
            int fade = Math.Max(1, (int)Math.Round(h * .08));
            for (int y = Math.Max(0, h - fade); y < target; y++)
            {
                double t = y >= h ? 1 : (y - (h - fade) + 1) / (double)fade;
                for (int x = 0; x < w; x++)
                {
                    int i = 4 * (y * w + x);
                    pixels[i] = Blend(y >= h ? bg.B : pixels[i], bg.B, t);
                    pixels[i + 1] = Blend(y >= h ? bg.G : pixels[i + 1], bg.G, t);
                    pixels[i + 2] = Blend(y >= h ? bg.R : pixels[i + 2], bg.R, t);
                    pixels[i + 3] = 255;
                }
            }
            return Bitmap(w, target, pixels);
        }
        int blur = compact.Blurred.PixelHeight;
        return new(Pad(compact.Clear, height), Pad(compact.Shaded, height),
            Pad(compact.Blurred, Math.Max(blur, (int)Math.Round(blur * (double)height / frame))), compact.ExtraShade);
    }

    private static BitmapSource Bitmap(int w, int h, byte[] pixels)
    {
        var image = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
        image.Freeze(); return image;
    }

    private static BitmapSource Blur(int w, int h, byte[] full)
    {
        int bw = Math.Max(1, w / 4), bh = Math.Max(1, h / 4);
        var a = new byte[bw * bh * 4]; var b = new byte[a.Length];
        for (int y = 0; y < bh; y++) for (int x = 0; x < bw; x++)
            Array.Copy(full, 4 * (Math.Min(h - 1, y * 4 + 2) * w + Math.Min(w - 1, x * 4 + 2)), a, 4 * (y * bw + x), 4);
        // Three box passes of radius four at quarter size approximate an 18 px Gaussian at 100%.
        int radius = Math.Max(1, (int)Math.Round(w / 480d * 4));
        for (int pass = 0; pass < 3; pass++)
        {
            Box(a, b, bw, bh, 1, bw, radius);
            Box(b, a, bh, bw, bw, 1, radius);
        }
        return Bitmap(bw, bh, a);
    }

    private static void Box(byte[] src, byte[] dst, int width, int height, int stride, int row, int radius)
    {
        for (int y = 0; y < height; y++) for (int c = 0; c < 4; c++)
        {
            int Offset(int x) => 4 * (y * row + Math.Clamp(x, 0, width - 1) * stride) + c;
            int sum = 0; for (int j = -radius; j <= radius; j++) sum += src[Offset(j)];
            for (int x = 0; x < width; x++)
            {
                dst[Offset(x)] = (byte)(sum / (2 * radius + 1));
                sum += src[Offset(x + radius + 1)] - src[Offset(x - radius)];
            }
        }
    }
}
