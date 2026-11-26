using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GameDailyWidget;

public sealed partial class MainWindow
{
    private readonly Grid _artSurface = new() { IsHitTestVisible = false, ClipToBounds = true };
    private readonly TextBlock _artCredit = new()
    {
        FontSize = 8,
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Top,
        Margin = new Thickness(0, 46, 16, 0)
    };
    private readonly ConditionalWeakTable<FrameworkElement, ArtGlassLayer> _glasses = new();
    private Brush GlassBrush(FrameworkElement element, Color tint)
    {
        if (!_glasses.TryGetValue(element, out var glass))
        { glass = new ArtGlassLayer(tint); _glasses.Add(element, glass); }
        else if (((SolidColorBrush)glass.Tint.Brush).Color != tint) glass.Tint.Brush = new SolidColorBrush(tint);
        glass.Image.ImageSource = _currentLayers?.Blurred;
        return glass.Brush;
    }
    private void AlignGlass()
    {
        foreach (var (panel, glass) in _glasses)
        {
            if ((!panel.IsVisible && !_preview) || !panel.IsDescendantOf(_card)) continue;
            glass.Align(panel, _artSurface);
        }
    }
    private ArtCache? _artCache;
    private Func<string, CancellationToken, Task<IReadOnlyList<string>>>? _gallery;
    private readonly Dictionary<string, string?> _latestArt = new();
    private readonly Dictionary<string, string> _previewSources = new();
    private readonly SemaphoreSlim _processing = new(1);
    private ArtLayers? _currentLayers;
    private (string Game, string? Source, ArtCrop Crop, int Intensity, int W, int H, bool Preview)? _artRequest;
    // The last compact-framed layers, reused when only the card height changes.
    private ((string Game, string? Source, ArtCrop Crop, int Intensity, int W, int Frame, bool Preview) Key, ArtLayers Layers)? _framed;
    private Task _artWork = Task.CompletedTask;
    private bool _artReady;
    private BackgroundWindow? _artEditor;
    private bool _openingEditor;
    internal void BindArt(ArtCache? cache, Func<string, CancellationToken, Task<IReadOnlyList<string>>>? gallery)
    {
        _artCache = _preview ? null : cache; _gallery = _preview ? null : gallery;
        foreach (var game in new[] { "wuwa", "endfield" }) _latestArt[game] = _artCache?.Latest(game);
        RefreshArt();
    }
    internal async Task SetOfficialArtAsync(string game, string url, CancellationToken ct)
    {
        if (_preview || _artCache == null) return;
        var key = await _artCache.OfficialAsync(game, url, _settings.ArtFor(game).Selected, ct);
        ct.ThrowIfCancellationRequested();
        var previous=_latestArt.GetValueOrDefault(game);
        _artCache.SetLatest(game, key); _latestArt[game] = key;
        if(_settings.FramingFor(game).Observe(previous,key,_settings.ArtFor(game)))SavePreferences();
        RefreshFraming();
        if (OverviewSlot(game).Frames == null && OverviewSlot(game).Work.IsCompleted) OverviewSlot(game).Request = null;
        if (_overviewOpen || _currentGame == game)
        {
            // A re-downloaded original may repair an earlier render without changing its URL hash.
            if (_currentLayers == null && _artWork.IsCompleted) _artRequest = null;
            RefreshArt();
        }
    }
    internal async Task SetPreviewArtAsync(string path, ArtCrop? crop = null)
    {
        if (!_preview) return;
        var fullPath = Path.GetFullPath(path);
        _previewSources[_currentGame] = fullPath;
        if (crop != null) _settings.ArtFor(_currentGame).Crops[fullPath] = crop.Clamp();
        RefreshArt();
        await _artWork;
    }

    private sealed class WorldArt
    {
        internal (string? Source, ArtCrop Crop, int Strength, int Width)? Request;
        internal OverviewArtFrames? Frames;
        internal bool Official;
        internal int Displayed = -1;
        internal Task Work = Task.CompletedTask;
    }

    private readonly WorldArt _wuwaWorld = new(), _endfieldWorld = new();
    private WorldArt OverviewSlot(string game) => game == "wuwa" ? _wuwaWorld : _endfieldWorld;
    private async Task<(Brush Brush, bool Official)> EditorOtherBackgroundAsync(string game)
    {
        string other = game == "wuwa" ? "endfield" : "wuwa";
        var slot = OverviewSlot(other);
        if (slot.Frames != null) return (slot.Frames[5], slot.Official);
        var options = _settings.ArtFor(other);
        var key = _preview ? _previewSources.GetValueOrDefault(other) : options.Selected ?? _latestArt.GetValueOrDefault(other);
        var path = _preview ? key : _artCache?.FileFor(other, key);
        if (path != null && options.Intensity > 0)
        {
            try
            {
                var frames = await Task.Run(() => ArtImage.ProcessOverview(ArtImage.Decode(ArtCache.ReadImage(path), 900),
                    other, options.Crop(key, other), options.Intensity, BackgroundWindow.FrameWidth, BackgroundWindow.FrameHeight));
                if (frames != null) return (frames[5], _preview || path.EndsWith(".official", StringComparison.Ordinal));
            }
            catch (Exception) { /* The other half can be previewed with its built-in texture. */ }
        }
        return (ArtFallback.For(other), false);
    }

    internal async Task SetOverviewPreviewArtAsync(string wuwaPath, string endfieldPath)
    {
        if (!_preview) return;
        if (!string.IsNullOrWhiteSpace(wuwaPath)) _previewSources["wuwa"] = Path.GetFullPath(wuwaPath);
        if (!string.IsNullOrWhiteSpace(endfieldPath)) _previewSources["endfield"] = Path.GetFullPath(endfieldPath);
        RefreshOverviewArt();
        await Task.WhenAll(_wuwaWorld.Work, _endfieldWorld.Work);
        if (!_overviewOpen) { RefreshArt(); await _artWork; }
    }

    private void RefreshOverviewArt()
    {
        if (!_overviewOpen) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        // Each side owns six color grades. Bound the backing texture and let WPF scale the frozen brushes.
        int width = (int)Math.Clamp(Math.Round(CardWidth * _settings.Scale * dpi.DpiScaleX), 280, 840);
        RefreshWorldArt("wuwa", _wuwaWorld, width);
        RefreshWorldArt("endfield", _endfieldWorld, width);
    }

    private void RefreshWorldArt(string game, WorldArt slot, int width)
    {
        var options = _settings.ArtFor(game);
        string? key = _preview ? _previewSources.GetValueOrDefault(game) : options.Selected ?? _latestArt.GetValueOrDefault(game);
        var crop = options.Crop(key, game);
        var request = (key, crop, options.Intensity, width);
        if (slot.Request == request) { ApplyWorldArt(game, slot); return; }

        slot.Request = request;
        slot.Frames = null;
        slot.Displayed = -1;
        ApplyWorldArt(game, slot);
        slot.Work = key == null || options.Intensity <= 0 ? Task.CompletedTask : RenderAsync(key, options.Intensity);
        async Task RenderAsync(string source, int strength)
        {
            int height = (int)Math.Round(CompactHeight * width / CardWidth);
            await _processing.WaitAsync();
            try
            {
                if (slot.Request != request || _closed) return;
                var frames = await Task.Run(() => _preview
                    ? ArtImage.ProcessOverview(ArtImage.Decode(ArtCache.ReadImage(source), Math.Min(1800, width * 2)), game, crop, strength, width, height)
                    : _artCache?.ProcessOverview(game, source, crop, strength, width, height));
                if (slot.Request != request || _closed) return;
                slot.Frames = frames;
                slot.Official = _preview || _artCache?.FileFor(game, source)?.EndsWith(".official", StringComparison.Ordinal) == true;
                slot.Displayed = -1;
                ApplyWorldArt(game, slot);
            }
            catch (Exception) { /* Retain the in-memory theme fallback until a new original or settings change. */ }
            finally { _processing.Release(); }
        }
    }

    private void ApplyWorldArt(string game, WorldArt slot)
    {
        int phase = slot.Frames == null ? -2 : OverviewArtFrames.Phase(OverviewState(game), _settings.CompletionArtEnabled);
        if (slot.Displayed == phase) return;
        slot.Displayed = phase;
        SetOverviewBackground(game, phase < 0 ? ArtFallback.For(game) : slot.Frames![phase], phase >= 0 && slot.Official);
    }

    private void RefreshArt()
    {
        if (!_artReady)
        {
            _artReady = true; _artSurface.Children.Add(_artCredit);
            _artSurface.SizeChanged += (_, _) => RefreshArt();
            _artSurface.LayoutUpdated += (_, _) => AlignGlass();
            DpiChanged += (_, _) => Dispatcher.BeginInvoke(new Action(RefreshArt));
        }
        if (_overviewOpen)
        {
            RefreshOverviewArt();
            return;
        }
        var game = _currentGame;
        var options = _settings.ArtFor(game);
        var key = _preview ? _previewSources.GetValueOrDefault(game) : options.Selected ?? _latestArt.GetValueOrDefault(game);
        var crop = options.Crop(key, game);
        var dpi = VisualTreeHelper.GetDpi(this);
        var scale = Math.Clamp(_settings.Scale, .5, 2.5);
        int w = (int)Math.Clamp(Math.Round(CardWidth * scale * dpi.DpiScaleX), 240, 1500);
        double logicalHeight = double.IsFinite(_card.Height) && _card.Height > 0 ? _card.Height : CompactHeight;
        int h = (int)Math.Clamp(Math.Round(logicalHeight * w / CardWidth), 174, 1800);
        // The artwork is always framed for the compact card. Expanded panels only
        // extend it downward, so the crop, cache entry and character position stay put.
        int frame = Math.Min(h, (int)Math.Clamp(Math.Round(CompactHeight * w / CardWidth), 174, 1800));
        var request = (game, key, crop, options.Intensity, w, h, _preview);
        var frameKey = (game, key, crop, options.Intensity, w, frame, _preview);
        if (_artRequest == request) return;
        bool sameImage = _artRequest is { } old && old.Game == game && old.Source == key;
        _artRequest = request;
        if (!sameImage || key == null || options.Intensity <= 0) InstallArt(game, null, false);
        if (key == null || options.Intensity <= 0) { _artWork = Task.CompletedTask; return; }
        var official = _preview || _artCache?.FileFor(game, key)?.EndsWith(".official", StringComparison.Ordinal) == true;
        _artWork = RenderAsync();
        async Task RenderAsync()
        {
            // Avoid allocating parallel full-sized buffers during an interactive resize.
            await _processing.WaitAsync();
            try
            {
                if (_artRequest != request || _closed) return;
                var framed = _framed is { } cached && cached.Key == frameKey ? cached.Layers : null;
                if (framed == null)
                {
                    framed = await Task.Run(() => _preview
                        ? ArtImage.Process(ArtImage.Decode(ArtCache.ReadImage(key), 1800), game, crop, request.Intensity, w, frame)
                        : _artCache?.Process(game, key, crop, request.Intensity, w, frame));
                    _framed = framed == null ? null : (frameKey, framed);
                }
                var layers = framed == null ? null : h > frame ? await Task.Run(() => ArtImage.Extend(framed, game, h)) : framed;
                if (_artRequest == request && !_closed) InstallArt(game, layers, official);
            }
            catch (Exception) { if (_artRequest == request && !_closed) InstallArt(game, null, false); }
            finally { _processing.Release(); }
        }
    }
    private void InstallArt(string game, ArtLayers? layers, bool official)
    {
        _currentLayers = layers;
        _artSurface.Background = layers == null ? ArtFallback.For(game) : new ImageBrush(layers.Shaded) { Stretch = Stretch.Fill };
        _artCredit.Text = layers != null && official ? (game == "wuwa" ? "© KURO GAMES" : "© HYPERGRYPH") : "";
        _artCredit.Foreground = new SolidColorBrush(game == "wuwa" ? Color.FromArgb(195, 255, 255, 255) : Color.FromArgb(180, 10, 10, 10));
        foreach (var (_, glass) in _glasses) glass.Image.ImageSource = layers?.Blurred;
        AlignGlass();
    }

    private void EditBackground() => EditBackground(_currentGame);

    private async void EditBackground(string game,string? noticeKey=null)
    {
        if (_preview || _openingEditor || _artEditor != null || _artCache == null) return;
        _openingEditor = true;
        try
        {
            _artEditor = new BackgroundWindow(game, _settings.ArtFor(game), _artCache, _latestArt.GetValueOrDefault(game), _gallery, null, _overviewOpen, await EditorOtherBackgroundAsync(game)) { Owner = this };
            if ((noticeKey ?? _latestArt.GetValueOrDefault(game)) is {} opened && _settings.FramingFor(game).Pending==opened)
                _artEditor.ContentRendered+=(_,_)=>HandleFraming(game,opened);
            if (_artEditor.ShowDialog() == true)
            {
                if (game == "wuwa") _settings.WuwaArt = _artEditor.Draft; else _settings.EndfieldArt = _artEditor.Draft;
                RefreshFraming(); _settings.Save(); RefreshArt(); await _artWork;
            }
        }
        catch (Exception) { Note("背景暂时无法更换"); }
        finally { _artEditor = null; _openingEditor = false; }
    }
    internal async Task RenderArtEditorAsync(string output, bool overview = false)
    {
        var editor = new BackgroundWindow(_currentGame, _settings.ArtFor(_currentGame), null, null, null, _previewSources.GetValueOrDefault(_currentGame), overview, await EditorOtherBackgroundAsync(_currentGame));
        await editor.PrepareAsync();
        var body = (FrameworkElement)editor.Content; editor.Content = null;
        var surface = new Border { Width = editor.Width, Background = editor.Background, Child = body };
        System.Windows.Documents.TextElement.SetForeground(surface, editor.Foreground);
        System.Windows.Documents.TextElement.SetFontFamily(surface, editor.FontFamily);
        surface.Measure(new Size(editor.Width, double.PositiveInfinity)); surface.Arrange(new Rect(surface.DesiredSize)); surface.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(surface.ActualWidth * 2), (int)Math.Ceiling(surface.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(surface); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        using var file = File.Create(output); png.Save(file); editor.Close();
    }
}

internal sealed class ArtGlassLayer
{
    internal readonly ImageBrush Image = new() { ViewboxUnits = BrushMappingMode.RelativeToBoundingBox, Stretch = Stretch.Fill };
    internal readonly DrawingBrush Brush;
    internal readonly GeometryDrawing Tint;
    internal ArtGlassLayer(Color color)
    {
        var rect = new RectangleGeometry(new Rect(0, 0, 1, 1));
        rect.Freeze();
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(Image, null, rect));
        Tint = new(new SolidColorBrush(color), null, rect);
        group.Children.Add(Tint);
        Brush = new(group) { Stretch = Stretch.Fill };
    }

    internal void Align(FrameworkElement panel, FrameworkElement surface)
    {
        if (Image.ImageSource == null || panel.ActualWidth <= 0 || panel.ActualHeight <= 0 ||
            surface.ActualWidth <= 0 || surface.ActualHeight <= 0) return;
        var at = panel.TranslatePoint(new Point(), surface);
        var region = new Rect(at.X / surface.ActualWidth, at.Y / surface.ActualHeight,
            panel.ActualWidth / surface.ActualWidth, panel.ActualHeight / surface.ActualHeight);
        if (Image.Viewbox != region) Image.Viewbox = region;
    }
}

internal static class ArtFallback
{
    private static readonly Brush Dark = Create(false), Light = Create(true);
    internal static Brush For(string game) => game == "endfield" ? Light : Dark;
    private static Brush Create(bool light)
    {
        var group = new DrawingGroup();
        using (var dc = group.Open())
        {
            Brush bg = light ? new SolidColorBrush(Color.FromRgb(238, 236, 228)) : new LinearGradientBrush(Color.FromRgb(9, 10, 28), Color.FromRgb(42, 33, 63), 38);
            dc.DrawRectangle(bg, null, new Rect(0, 0, 480, 348));
            var pen = new Pen(new SolidColorBrush(light ? Color.FromArgb(22, 58, 59, 58) : Color.FromArgb(23, 202, 194, 245)), .8);
            if (light)
            {
                for (int x = 0; x < 480; x += 18) dc.DrawLine(pen, new Point(x, 0), new Point(x, 348));
                for (int y = 0; y < 348; y += 18) dc.DrawLine(pen, new Point(0, y), new Point(480, y));
                foreach (var (x, y, s, t) in new[] { (454, 62, -1, 1), (34, 282, 1, -1) })
                { dc.DrawLine(pen, new(x, y), new(x + s * 14, y)); dc.DrawLine(pen, new(x, y), new(x, y + t * 14)); }
            }
            else for (int i = 0; i < 6; i++)
            {
                var path = new StreamGeometry();
                using (var c = path.Open()) { c.BeginFigure(new Point(0, 240 + i * 13), false, false); c.BezierTo(new(160, 328 + i * 15), new(265, 26 + i * 11), new(480, 108 + i * 9), true, false); }
                dc.DrawGeometry(null, pen, path);
            }
        }
        var brush = new DrawingBrush(group) { Stretch = Stretch.Fill }; brush.Freeze(); return brush;
    }
}
