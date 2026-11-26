using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace GameDailyWidget;

public sealed class GameArtSettings
{
    public string? Selected { get; set; }
    public int Intensity { get; set; } = 100;
    public Dictionary<string, ArtCrop> Crops { get; set; } = new();
    internal ArtCrop Crop(string? key, string game) =>
        key != null && Crops?.GetValueOrDefault(key) is { } crop ? crop.Clamp() : ArtCrop.DefaultFor(game);
}

internal sealed class ArtCache : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _root;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly object _disk = new();
    private readonly ConcurrentDictionary<string, string> _editing = new();
    internal void PinEditor(string game, string? key)
    {
        if (Valid(key)) _editing[game] = key!;
        else _editing.TryRemove(game, out _);
    }
    internal ArtCache(string? root = null, HttpMessageHandler? handler = null)
    {
        _root = root ?? Path.Combine(UnifiedSettings.DataDirectory, "art");
        _http = new(handler ?? new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false, UseDefaultCredentials = false });
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DesktopWidget/1.0");
    }
    public void Dispose() => _http.Dispose();
    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    internal static bool Valid(string? key) => key?.Length == 64 && key.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private string Dir(string game) => Path.Combine(_root, game switch { "wuwa" => "wuwa", "endfield" => "endfield", _ => throw new ArgumentException("未知游戏") });
    internal string? Latest(string game)
    {
        try { var key = File.ReadAllText(Path.Combine(Dir(game), "latest")); return Valid(key) ? key : null; } catch (IOException) { return null; } catch (UnauthorizedAccessException) { return null; }
    }
    internal void SetLatest(string game, string key)
    {
        if (Valid(key)) Atomic(Path.Combine(Dir(game), "latest"), Encoding.ASCII.GetBytes(key));
    }

    internal string? FileFor(string game, string? key)
    {
        if (!Valid(key)) return null;
        string path = Path.Combine(Dir(game), key!);
        return File.Exists(path + ".official") ? path + ".official" : File.Exists(path + ".local") ? path + ".local" : null;
    }

    internal static byte[] ReadImage(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (file.Length > ArtImage.MaxBytes || file.Length <= 0) throw new InvalidDataException("图片必须在 12 MB 以内");
        byte[] bytes = new byte[(int)file.Length]; file.ReadExactly(bytes); return bytes;
    }
    private static void Atomic(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temp, bytes); File.Move(temp, path, true); } finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    internal async Task<string> OfficialAsync(string game, string url, string? pinned, CancellationToken token)
    {
        if (!OfficialArtSources.Allowed(game, url)) throw new InvalidDataException("背景来源地址无效");
        string key = Hash(Encoding.UTF8.GetBytes(url));
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            string path = Path.Combine(Dir(game), key + ".official");
            if (File.Exists(path))
            {
                try { await Task.Run(() => ArtImage.Decode(ReadImage(path), 96), token).ConfigureAwait(false); File.SetLastWriteTimeUtc(path, DateTime.UtcNow); return key; }
                catch (Exception ex) when (ex is not OperationCanceledException) { /* Download a fresh validated original. */ }
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var mime = resp.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
            if (mime is not ("image/png" or "image/jpeg" or "image/webp") || resp.Content.Headers.ContentLength > ArtImage.MaxBytes)
                throw new InvalidDataException("图片类型或大小不受支持");
            using var source = await resp.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var memory = new MemoryStream();
            var buffer = new byte[65536];
            for (int n; (n = await source.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0;)
            {
                if (memory.Length + n > ArtImage.MaxBytes) throw new InvalidDataException("图片超过 12 MB");
                memory.Write(buffer, 0, n);
            }
            var data = memory.ToArray();
            if (ArtImage.Type(data) != mime) throw new InvalidDataException("图片内容与类型不符");
            await Task.Run(() =>
            {
                ArtImage.Decode(data, 128);
                lock (_disk)
                {
                    Atomic(path, data);
                    ClearProcessed(game, key);
                    Prune(game, key, pinned);
                }
            }, token).ConfigureAwait(false);
            return key;
        }
        finally { _gate.Release(); }
    }

    internal async Task<string> ImportAsync(string game, string path, string? pinned, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
        {
            var data = ReadImage(path); ArtImage.Decode(data, 128);
            var key = Hash(data); var dest = Path.Combine(Dir(game), key + ".local");
            lock (_disk) { Atomic(dest, data); Prune(game, key, pinned); }
            return key;
        }, token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private void Prune(string game, string key, string? pinned)
    {
        var dir = Dir(game);
        var images = Directory.EnumerateFiles(dir).Where(p => (Path.GetExtension(p) is ".local" or ".official")
            && Valid(Path.GetFileNameWithoutExtension(p))).Select(p => new FileInfo(p)).ToList();
        var keepLatest = Latest(game);
        _editing.TryGetValue(game, out var editing);
        foreach (var file in images.Where(f => Path.GetFileNameWithoutExtension(f.Name) is var k && k != key && k != pinned && k != keepLatest && k != editing)
            .OrderBy(f => f.LastWriteTimeUtc).Take(Math.Max(0, images.Count - 12)))
        {
            file.Delete(); ClearProcessed(game, Path.GetFileNameWithoutExtension(file.Name));
        }
    }

    private void ClearProcessed(string game, string key)
    {
        var dir = Path.Combine(Dir(game), "processed");
        if (Directory.Exists(dir)) foreach (var path in Directory.EnumerateFiles(dir, key + ".*")) File.Delete(path);
    }
    private static void WriteFrame(string path, BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream(); encoder.Save(stream); Atomic(path, stream.ToArray());
    }
    internal ArtLayers? Process(string game, string key, ArtCrop crop, int strength, int w, int h)
    {
        lock (_disk) return ReadOrProcess(game, key, crop, strength, w, h);
    }

    internal OverviewArtFrames? ProcessOverview(string game, string key, ArtCrop crop, int strength, int width, int height)
    {
        lock (_disk)
        {
            if (strength <= 0 || FileFor(game, key) is not { } path) return null;
            var stem = Path.Combine(Dir(game), "processed", key + ".overview");
            var signature = FormattableString.Invariant($"2|overview|{game}|{crop.X:R}|{crop.Y:R}|{crop.Zoom:R}|{strength}|{width}|{height}");
            try
            {
                var stamp = new FileInfo(stem + ".settings");
                if (stamp.Exists && stamp.Length < 256 && File.ReadAllText(stamp.FullName) == signature)
                {
                    var atlas = ArtImage.Decode(ReadImage(stem + ".png"), height * OverviewArtFrames.Count);
                    if (atlas.PixelWidth == OverviewArtFrames.SideWidth(width) && atlas.PixelHeight == height * OverviewArtFrames.Count)
                    {
                        File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                        return new(atlas, game, width);
                    }
                }
            }
            catch (Exception) { /* Rebuild incomplete or damaged derived data from the original. */ }
            var source = ArtImage.Decode(ReadImage(path), Math.Clamp((int)(width * crop.Clamp().Zoom * 2), 640, 2200));
            var frames = ArtImage.ProcessOverview(source, game, crop, strength, width, height);
            if (frames != null)
            {
                if (File.Exists(stem + ".settings")) File.Delete(stem + ".settings");
                WriteFrame(stem + ".png", frames.Atlas);
                Atomic(stem + ".settings", Encoding.UTF8.GetBytes(signature));
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            }
            return frames;
        }
    }

    private ArtLayers? ReadOrProcess(string game, string key, ArtCrop crop, int strength, int w, int h)
    {
        if (strength <= 0 || FileFor(game, key) is not { } path) return null;
        var stem = Path.Combine(Dir(game), "processed", key);
        var signature = FormattableString.Invariant($"1|{crop.X:R}|{crop.Y:R}|{crop.Zoom:R}|{strength}|{w}|{h}");
        ArtLayers? layers;
        try
        {
            var stamp = new FileInfo(stem + ".settings");
            if (stamp.Exists && stamp.Length < 256 && File.ReadAllText(stamp.FullName) == signature)
            {
                BitmapSource Read(string kind) => ArtImage.Decode(ReadImage(stem + "." + kind), Math.Max(w, h));
                layers = new(Read("clear"), Read("shaded"), Read("blur"), 0);
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                return layers;
            }
        }
        catch (Exception) { /* Missing or damaged derived files are rebuilt from the original. */ }
        var source = ArtImage.Decode(ReadImage(path), Math.Clamp((int)(Math.Max(w, h) * crop.Clamp().Zoom * 2), 640, 2200));
        layers = ArtImage.Process(source, game, crop, strength, w, h);
        if (layers != null)
        {
            // Publish the signature only after all three frames were atomically replaced.
            if (File.Exists(stem + ".settings")) File.Delete(stem + ".settings");
            WriteFrame(stem + ".clear", layers.Clear);
            WriteFrame(stem + ".shaded", layers.Shaded);
            WriteFrame(stem + ".blur", layers.Blurred);
            Atomic(stem + ".settings", Encoding.UTF8.GetBytes(signature));
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        return layers;
    }
}
