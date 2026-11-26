using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GameDailyWidget;

internal static class ArtChecks
{
    private sealed class Images : HttpMessageHandler
    {
        internal byte[] Data = [];
        internal string Mime = "image/png";
        internal int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            Requests++;
            if (request.Headers.Authorization != null || request.Headers.Contains("Cookie"))
                throw new InvalidOperationException("模拟图片请求意外携带凭据");
            var content = new ByteArrayContent(Data);
            content.Headers.ContentType = new MediaTypeHeaderValue(Mime);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private static byte[] Solid(int w, int h, byte channel, bool jpeg = false)
    {
        var pixels = Enumerable.Repeat(channel, w * h * 4).ToArray();
        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        var image = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
        BitmapEncoder encoder = jpeg ? new JpegBitmapEncoder() : new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static double Luminance(byte r, byte g, byte b)
    {
        static double Linear(byte x)
        {
            var s = x / 255d;
            return s <= .04045 ? s / 12.92 : Math.Pow((s + .055) / 1.055, 2.4);
        }
        return .2126 * Linear(r) + .7152 * Linear(g) + .0722 * Linear(b);
    }

    private static bool ContrastPass(BitmapSource image, bool light)
    {
        var data = new byte[image.PixelWidth * image.PixelHeight * 4];
        image.CopyPixels(data, image.PixelWidth * 4, 0);
        double body = light ? Luminance(90, 90, 90) : Luminance(190, 185, 180);
        double metric = light ? Luminance(20, 20, 20) : Luminance(244, 241, 234);
        static double Ratio(double a, double b) => (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
        for (int region = 0; region < 2; region++)
        {
            int count = 0;
            double total = 0;
            for (int y = 0; y < image.PixelHeight; y++)
                for (int x = 0; x < image.PixelWidth; x++)
                {
                    if (region == 0 ? x >= image.PixelWidth * .45 : y < image.PixelHeight * .65) continue;
                    int i = 4 * (y * image.PixelWidth + x);
                    total += Luminance(data[i + 2], data[i + 1], data[i]);
                    count++;
                }
            double background = total / count;
            if (Ratio(body, background) < 4.5 || Ratio(metric, background) < 3) return false;
        }
        return true;
    }

    internal static async Task<IReadOnlyList<string>> RunAsync()
    {
        var messages = new List<string>();
        void Check(bool ok, string message)
        {
            if (!ok) throw new InvalidOperationException(message);
            messages.Add("PASS " + message);
        }
        var legacy = JsonSerializer.Deserialize<UnifiedSettings>("{}")!;
        Check(new[] { legacy.WuwaArt, legacy.EndfieldArt }.All(a => a.Selected == null && a.Intensity == 100) &&
            legacy.WuwaArt.Crop(null,"wuwa") == new ArtCrop() && legacy.EndfieldArt.Crop(null,"endfield") == new ArtCrop(.6,.15,1.35),
            "旧设置迁移为每款游戏独立的自动背景、各自的默认焦点和 100% 强度");
        var saved = JsonSerializer.Deserialize<GameArtSettings>("""{"Crops":{"demo":{"X":0.4,"Y":0.6,"Zoom":1.6}}}""")!;
        Check(saved.Crop("demo","wuwa") == new ArtCrop(.4,.6,1.6) && saved.Crop("demo","endfield") == new ArtCrop(.4,.6,1.6) &&
            saved.Crop("other","wuwa") == new ArtCrop(.5,.3,1.25) && saved.Crop("other","endfield") == new ArtCrop(.6,.15,1.35) &&
            new ArtCrop(Zoom: double.NaN).Clamp().Zoom == 1.25, "旧构图按图保留，未保存时按游戏取默认构图，无效缩放回退到 1.25");
        // A 1600x900 Endfield version cover carries its title banner from about y=645 down.
        var cover = ArtImage.Region(1600, 900, 560, 372, ArtCrop.DefaultFor("endfield"));
        Check(cover.Top == 0 && cover.Bottom < 680 && Math.Abs(cover.Width / cover.Height - 560d / 372) < 1e-6 &&
            cover.Left > 250 && cover.Right < 1300,
            "终末地默认构图取封面上部人物区，避开中下部的标题横幅");
        var region = ArtImage.Region(1600, 900, 560, 372, new());
        Check(Math.Abs(region.Width / region.Height - 560d / 372) < 1e-6 &&
            Math.Abs((800 - region.Left) / region.Width - .7) < 1e-6 && Math.Abs((270 - region.Top) / region.Height - .22) < 1e-6,
            "默认裁切按卡片比例铺满并将原图焦点映射到指定位置");
        var corner = ArtImage.Region(1000, 800, 560, 372, new(-2, 2, 4));
        Check(corner.Left == 0 && Math.Abs(corner.Bottom - 800) < 1e-6 &&
            new ArtCrop(-2, 2, 4).Clamp() == new ArtCrop(0, 1, 3), "焦点越界与缩放上下限夹紧到原图区域");

        var white = ArtImage.Decode(Solid(100, 100, 255), 100);
        var black = ArtImage.Decode(Solid(100, 100, 0), 100);
        var darkFace = ArtImage.Process(white, "wuwa", new(), 100, 160, 116)!;
        var darkReference = ArtImage.Process(black, "wuwa", new(), 100, 160, 116)!;
        var lightFace = ArtImage.Process(black, "endfield", new(), 100, 160, 116)!;
        Check(darkFace.ExtraShade > darkReference.ExtraShade && ContrastPass(darkFace.Shaded, false) && ContrastPass(lightFace.Shaded, true),
            "亮图自适应压暗，两款皮肤的正文与大数字在左侧和底部均达到 4.5:1 和 3:1");
        Check(darkFace.Clear.IsFrozen && darkFace.Shaded.IsFrozen && darkFace.Blurred.IsFrozen && darkFace.Blurred.PixelWidth < darkFace.Shaded.PixelWidth &&
            ArtImage.Process(white, "wuwa", new(), 0, 160, 116) == null, "背景三层均冻结，模糊层降采样，强度零使用回退底纹");

        static byte[] Pixels(BitmapSource image)
        {
            var bgra = image.Format == PixelFormats.Bgra32 ? image : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            var data = new byte[bgra.PixelWidth * bgra.PixelHeight * 4];
            bgra.CopyPixels(data, bgra.PixelWidth * 4, 0);
            return data;
        }
        foreach (var (game, face, bg) in new[] { ("wuwa", darkFace, (B: (byte)28, G: (byte)10, R: (byte)9)),
                     ("endfield", lightFace, (B: (byte)228, G: (byte)236, R: (byte)238)) })
        {
            var tall = ArtImage.Extend(face, game, 190);
            var top = Pixels(face.Shaded);
            var extended = Pixels(tall.Shaded);
            int keep = 116 - (int)Math.Round(116 * .08);
            bool sameTop = extended.AsSpan(0, 160 * 4 * keep).SequenceEqual(top.AsSpan(0, 160 * 4 * keep));
            bool baseBelow = Enumerable.Range(116, 190 - 116).All(y => Enumerable.Range(0, 160).All(x =>
            {
                int i = 4 * (y * 160 + x);
                return extended[i] == bg.B && extended[i + 1] == bg.G && extended[i + 2] == bg.R && extended[i + 3] == 255;
            }));
            Check(tall.Shaded.PixelWidth == 160 && tall.Shaded.PixelHeight == 190 && tall.Clear.PixelHeight == 190 &&
                tall.Blurred.PixelWidth == face.Blurred.PixelWidth && tall.Blurred.PixelHeight == (int)Math.Round(face.Blurred.PixelHeight * 190d / 116) &&
                tall.Shaded.IsFrozen && tall.Blurred.IsFrozen && sameTop && baseBelow && ReferenceEquals(ArtImage.Extend(face, game, 116), face),
                game + " 展开面板时背景保持收起时的构图，下方用主题底色延伸，模糊层同比例延伸");
        }

        var progress = new GameViewState { LoggedIn = true, DailyState = "incomplete", MaxDaily = 100 };
        for (int step = 0; step < OverviewArtFrames.Count; step++)
        {
            var factors = OverviewArtFrames.Factors(step);
            Check(OverviewArtFrames.Phase(progress with { Daily = step * 20 }, true) == step &&
                Math.Abs(factors.Saturation - (.65 + step * .07)) < .00001 && Math.Abs(factors.Brightness - (.90 + step * .02)) < .00001,
                $"总览立绘 {step * 20}% 档饱和度与亮度符合线性插值");
        }
        Check(OverviewArtFrames.Phase(progress with { Daily = 39 }, true) == 1 &&
            OverviewArtFrames.Phase(progress with { Daily = 20 }, false) == 5 &&
            new[] { progress, progress with { Daily = 20, LoggedIn = false }, progress with { Daily = 20, DailyState = "locked" },
                progress with { Daily = 20, Stale = true }, progress with { Daily = 20, Error = "测试" } }
                .All(x => OverviewArtFrames.Phase(x, true) == 5), "总览进度在档位间向下取整，关闭开关或读数未知、过期、失败、未登录、未解锁均保持原色");

        foreach (string game in new[] { "wuwa", "endfield" })
        {
            bool light = game == "endfield";
            var anchor = OverviewArtFrames.Anchor(game);
            var frame = ArtImage.Region(1600, 900, 560, 372, new(.5,.5,1.5), anchor.X, anchor.Y);
            Check(Math.Abs((800 - frame.Left) / frame.Width - (light ? .64 : .38)) < 1e-6 &&
                Math.Abs((450 - frame.Top) / frame.Height - (light ? .32 : .30)) < 1e-6, game + " 总览使用保存的焦点和缩放，并摆在本侧人物位置");
            var frames = ArtImage.ProcessOverview(light ? black : white, game, new(), 100, 100, 64)!;
            var atlas = Pixels(frames.Atlas);
            int side = frames.Atlas.PixelWidth;
            double body = light ? Luminance(90,90,90) : Luminance(190,185,180);
            double large = light ? Luminance(20,20,20) : Luminance(244,241,234);
            static double Ratio(double a, double b) => (Math.Max(a,b)+.05)/(Math.Min(a,b)+.05);
            bool contrast = true;
            for (int phase = 0; phase < OverviewArtFrames.Count; phase++)
                for (int outer = 0; outer <= 26; outer++)
                {
                    int x = light ? side - 1 - outer : outer;
                    int i = 4 * ((phase * 64 + 17) * side + x);
                    double background = Luminance(atlas[i+2],atlas[i+1],atlas[i]);
                    contrast &= Ratio(body, background) >= 4.5 && Ratio(large, background) >= 3;
                }
            var brush = (ImageBrush)frames[3];
            Check(contrast && frames.Atlas.IsFrozen && Enumerable.Range(0,6).All(i => frames[i].IsFrozen && ReferenceEquals(frames[i],frames[i])) &&
                brush.Viewbox.Y == .5 && Math.Abs(brush.Viewport.X - (light ? 1 - side / 100d : 0)) < 1e-6 &&
                ArtImage.ProcessOverview(white, game, new(), 0, 100, 64) == null,
                game + " 六档复用冻结的本侧贴图，外缘小字与指标达到 4.5:1 和 3:1，零强度使用底纹");
        }

        var cyan = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[]{255,255,0,255}, 4);
        var colorSteps = ArtImage.ProcessOverview(cyan, "wuwa", new(), 100, 100, 64)!;
        var actual = Pixels(colorSteps.Atlas);
        for (int tier = 0; tier < 6; tier++)
        {
            double sat = .65 + .07 * tier, light = .90 + .02 * tier;
            double gray = 255 * (.0722 + .7152);
            byte Expected(int channel, int backdrop) => (byte)Math.Round(.1 * backdrop + .9 * light * (sat * channel + (1 - sat) * gray));
            int i = 4 * ((tier * 64 + 7) * colorSteps.Atlas.PixelWidth + 53);
            Check(Math.Abs(actual[i] - Expected(255,28)) <= 1 && Math.Abs(actual[i+1] - Expected(255,10)) <= 1 &&
                Math.Abs(actual[i+2] - Expected(0,9)) <= 1, $"纯色样本回读验证 {tier * 20}% 档实际像素的饱和、压暗和人物区渐变");
        }

        var sandbox = Directory.CreateTempSubdirectory("WidgetArtCheck-");
        try
        {
            var mock = new Images { Data = Solid(16, 16, 24) };
            using var cache = new ArtCache(sandbox.FullName, mock);
            const string prefix = "https://web.hycdn.cn/synthetic/";
            const string first = prefix + "first";
            var key = await cache.OfficialAsync("endfield", first, null, CancellationToken.None);
            var original = cache.FileFor("endfield", key)!;
            await cache.OfficialAsync("endfield", first, null, CancellationToken.None);
            Check(mock.Requests == 1 && cache.Process("endfield", key, new(), 100, 48, 36)?.Shaded.PixelWidth == 48,
                "同一个官方网址只下载一次并生成原图及可复用处理图");
            var stem = Path.Combine(sandbox.FullName, "endfield", "processed", key);
            cache.Process("endfield",key,new ArtCrop(Zoom: 1.6),100,48,36);
            var oldSignature = File.ReadAllText(stem + ".settings");
            File.WriteAllBytes(stem + ".clear",Solid(8,8,240));
            var rebuilt = cache.Process("endfield",key,new(),100,48,36);
            Check(rebuilt?.Clear.PixelWidth == 48 && File.ReadAllText(stem + ".settings") != oldSignature && mock.Requests == 1,
                "旧默认缩放的处理图按新缩放从原图重建，无需再次下载");
            var singleSignature = File.ReadAllText(stem + ".settings");
            var world = cache.ProcessOverview("endfield",key,new(),100,48,36)!;
            string overviewSignature = File.ReadAllText(stem + ".overview.settings");
            File.WriteAllBytes(original, [0, 1]);
            var hit = cache.ProcessOverview("endfield",key,new(),100,48,36)!;
            Check(overviewSignature.Contains("|overview|endfield|", StringComparison.Ordinal) &&
                File.ReadAllText(stem + ".settings") == singleSignature && Pixels(hit.Atlas).SequenceEqual(Pixels(world.Atlas)) && mock.Requests == 1,
                "总览缓存签名标识页面与游戏，六档可直接从磁盘恢复，单游戏缓存保持独立");
            await cache.OfficialAsync("endfield", first, null, CancellationToken.None);
            var processed = Path.Combine(sandbox.FullName, "endfield", "processed", key + ".shaded");
            cache.Process("endfield", key, new(), 100, 48, 36);
            File.WriteAllBytes(processed, [0, 1]);
            Check(mock.Requests == 2 && cache.Process("endfield", key, new(), 100, 48, 36)?.Shaded.IsFrozen == true &&
                ArtImage.Decode(ArtCache.ReadImage(processed), 48).PixelWidth == 48, "损坏的原图重新下载，损坏的处理图从本地原图重建");
            int bad = 0;
            foreach (var (mime, bytes) in new (string, byte[])[] {
                ("text/html",mock.Data), ("image/jpeg",mock.Data), ("image/png",new byte[ArtImage.MaxBytes+1]),
                ("image/png",mock.Data.Take(12).ToArray()), ("image/png",Solid(8001,1,12)) })
            {
                mock.Mime = mime; mock.Data = bytes;
                try { await cache.OfficialAsync("endfield", prefix + "bad" + bad, null, CancellationToken.None); }
                catch (Exception ex) when (ex is not OperationCanceledException) { bad++; }
            }
            Check(bad == 5, "图片下载拒绝不支持的类型、伪造 MIME、超 12 MB、损坏文件和超过 8000 像素的边长");
            // The WebP is a locally generated solid blue 12 by 12 test image.
            var webp = Convert.FromBase64String("UklGRjgAAABXRUJQVlA4ICwAAACQAQCdASoMAAwAAgA0JaACdLoAA5gA/vmTb/+QH/+QH/+QH/8gP+IXeyAwAA==");
            Check(ArtImage.Decode(webp, 12).PixelWidth == 12 && ArtImage.Decode(Solid(32, 16, 30, true), 16).PixelWidth == 16,
                "本地 WebP 与 JPEG 使用系统解码器按目标尺寸读取");
            string input = Path.Combine(sandbox.FullName, "input.png");
            var keys = new List<string>();
            for (int i = 0; i < 13; i++)
            {
                File.WriteAllBytes(input, Solid(12, 12, (byte)i));
                keys.Add(await cache.ImportAsync("wuwa", input, i == 0 ? null : keys[0], CancellationToken.None));
                File.SetLastWriteTimeUtc(cache.FileFor("wuwa", keys[i])!, new DateTime(2000, 1, 1, 0, i, 0, DateTimeKind.Utc));
                if (i == 1) cache.PinEditor("wuwa", keys[1]);
            }
            var files = Directory.GetFiles(Path.Combine(sandbox.FullName, "wuwa"), "*.local");
            Check(files.Length == 12 && cache.FileFor("wuwa", keys[0]) != null && cache.FileFor("wuwa", keys[1]) != null &&
                cache.FileFor("wuwa", keys[2]) == null && keys.Skip(3).All(k => cache.FileFor("wuwa", k) != null),
                "各游戏独立保留十二张原图，淘汰最久未用的图，保护已保存及编辑中的选择");
            cache.PinEditor("wuwa", null);
            File.WriteAllBytes(input, Solid(12, 12, 26));
            await cache.ImportAsync("wuwa", input, keys[0], CancellationToken.None);
            Check(cache.FileFor("wuwa", keys[1]) == null && cache.FileFor("wuwa", keys[0]) != null,
                "取消临时选图后释放候选图，仍可显示此前保存的背景");
        }
        finally { sandbox.Delete(recursive: true); }
        return messages;
    }
}
