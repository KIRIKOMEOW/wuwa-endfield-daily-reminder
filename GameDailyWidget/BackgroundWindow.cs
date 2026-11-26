using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace GameDailyWidget;

internal sealed class BackgroundWindow : Window
{
    private readonly string _game;
    private readonly ArtCache? _cache;
    private readonly string? _latest, _preview, _savedSelection;
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<string>>>? _gallery;
    private readonly CancellationTokenSource _closedToken = new();
    internal const int FrameWidth = 404, FrameHeight = 268;
    private readonly Grid _single = new(), _worlds = new();
    private readonly Border _editedWorld = new();
    private readonly TextBlock _worldCredit = new();
    private bool _overview;
    private readonly Grid _canvas = new() { Width = FrameWidth, Height = FrameHeight, ClipToBounds = true, Cursor = Cursors.SizeAll };
    private readonly WrapPanel _thumbs = new();
    private readonly TextBlock _status = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _choice = new() { FontSize = 12 };
    private readonly Slider _intensity = new() { Minimum = 0, Maximum = 100, IsSnapToTickEnabled = true, TickFrequency = 1, Width = 250 };
    private readonly Button _save = ReminderDelayWindow.MakeButton("保存", true);
    private readonly List<(Border Tile, ArtGlassLayer Glass)> _tiles = [];
    private BitmapSource? _source;
    private string? _localPath, _localHash;
    private Point? _drag;
    private ArtCrop _crop = new();
    private int _sourceRevision, _paintRevision;
    private readonly SemaphoreSlim _paintLock = new(1);
    private bool _initial = true;
    internal GameArtSettings Draft { get; }
    private string? CurrentKey => _localHash ?? Draft.Selected ?? _preview ?? _latest;

    internal BackgroundWindow(string game, GameArtSettings settings, ArtCache? cache, string? latest,
        Func<string, CancellationToken, Task<IReadOnlyList<string>>>? gallery, string? preview, bool overview = false,
        (Brush Brush, bool Official)? otherWorld = null)
    {
        _overview = overview;
        _game = game; _cache = cache; _latest = latest; _gallery = gallery; _preview = preview; _savedSelection = settings.Selected;
        Draft = new() { Selected = settings.Selected, Intensity = settings.Intensity, Crops = new(settings.Crops ?? new()) };
        _crop = Draft.Crop(CurrentKey, _game);
        Title = "更换背景"; Width = 470; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        Background = ReminderDelayWindow.Paint("#22232A"); Foreground = ReminderDelayWindow.Paint("#F2F0E9"); FontSize = 13; FontFamily = new("Microsoft YaHei UI");
        var body = new StackPanel { Margin = new Thickness(22, 17, 22, 17) };
        void Add(UIElement e) => body.Children.Add(e);
        Add(new TextBlock { Text = (game == "wuwa" ? "鸣潮" : "终末地") + " · 更换背景", FontSize = 17, FontWeight = FontWeights.SemiBold });
        var bg = ArtFallback.For(game);
        _canvas.Background = bg;
        var model = new StackPanel { Margin = new Thickness(game == "wuwa" ? 17 : 26, 15, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, IsHitTestVisible = false };
        var ink = game == "wuwa" ? Brushes.Ivory : Brushes.Black;
        model.Children.Add(new TextBlock { Text = game == "wuwa" ? "鸣潮" : "终末地", FontSize = 22, FontWeight = FontWeights.Bold, Foreground = ink });
        model.Children.Add(new TextBlock { Text = game == "wuwa" ? "WUTHERING WAVES" : "// ARKNIGHTS: ENDFIELD", FontSize = 8, Foreground = ink });
        _single.Children.Add(model);
        if (game == "endfield") _single.Children.Add(new Border { Width = 13, Background = Brushes.Yellow, HorizontalAlignment = HorizontalAlignment.Left });
        _canvas.Clip = new RectangleGeometry(new Rect(0, 0, FrameWidth, FrameHeight), 11, 11);
        var metrics = new UniformGrid { Rows = 1, Columns = 2 };
        metrics.Margin = new Thickness(game == "wuwa" ? 17 : 26, 118, 17, 0); metrics.VerticalAlignment = VerticalAlignment.Top; metrics.IsHitTestVisible = false;
        foreach (var title in new[] { game == "wuwa" ? "◇ 结晶波片" : "SANITY 理智", game == "wuwa" ? "◇ 每日活跃度" : "ACTIVITY 每日活跃度" })
        {
            var numbers = new StackPanel { Margin = new Thickness(9, 7, 9, 5) };
            numbers.Children.Add(new TextBlock { Text = title, FontSize = 10, Foreground = ink });
            numbers.Children.Add(new TextBlock { Text = "—", FontSize = 33, Foreground = ink });
            var glass = new ArtGlassLayer((Color)ColorConverter.ConvertFromString(game == "wuwa" ? "#66101028" : "#9EFAFAF6"));
            var tile = new Border
            {
                Child = numbers,
                Height = 84,
                CornerRadius = new CornerRadius(game == "wuwa" ? 8 : 0),
                Margin = new Thickness(3, 0, 3, 0),
                Background = glass.Brush,
                BorderThickness = new Thickness(1),
                BorderBrush = ReminderDelayWindow.Paint(game == "wuwa" ? "#29FFFFFF" : "#24000000")
            };
            _tiles.Add((tile, glass));
            if (game == "endfield") tile.SizeChanged += (_, _) => tile.Clip = CutCorner(tile.ActualWidth, tile.ActualHeight);
            metrics.Children.Add(tile);
        }
        _single.Children.Add(metrics);
        _canvas.LayoutUpdated += (_, _) => { foreach (var (tile, glass) in _tiles) glass.Align(tile, _canvas); };
        var credit = new TextBlock { FontSize = 7, Foreground = ink, Margin = new Thickness(0, 38, 14, 0), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
        _single.Children.Add(credit); _credit = credit;
        string otherGame = game == "wuwa" ? "endfield" : "wuwa";
        foreach (string world in new[] { "wuwa", "endfield" })
        {
            var side = world == game ? _editedWorld : new Border { Background = otherWorld?.Brush ?? ArtFallback.For(otherGame) };
            side.Clip = WorldClip(world);
            var content = new StackPanel
            {
                HorizontalAlignment = world == "wuwa" ? HorizontalAlignment.Left : HorizontalAlignment.Right,
                Margin = new Thickness(13, 17, 13, 0)
            };
            var align = world == "wuwa" ? TextAlignment.Left : TextAlignment.Right;
            var worldInk = world == "wuwa" ? Brushes.Ivory : Brushes.Black;
            var copyright = world == game ? _worldCredit : new TextBlock();
            copyright.FontSize = 7; copyright.Foreground = worldInk; copyright.TextAlignment = align;
            if (world != game && otherWorld?.Official == true) copyright.Text = Credit(world);
            content.Children.Add(new TextBlock { Text = world == "wuwa" ? "鸣潮" : "终末地", FontSize = 17, FontWeight = FontWeights.Bold, Foreground = worldInk, TextAlignment = align });
            content.Children.Add(new TextBlock { Text = world == "wuwa" ? "WUTHERING WAVES" : "ARKNIGHTS: ENDFIELD", FontSize = 7, Foreground = worldInk, TextAlignment = align });
            content.Children.Add(copyright); side.Child = content; _worlds.Children.Add(side);
        }
        var seam = new System.Windows.Shapes.Line { X1 = .60 * FrameWidth, Y1 = 0, X2 = .40 * FrameWidth, Y2 = FrameHeight,
            Stroke = new LinearGradientBrush(Colors.Yellow, Colors.Wheat, 90), StrokeThickness = 1.2 };
        _worlds.Children.Add(seam);
        _canvas.Children.Add(_single); _canvas.Children.Add(_worlds);
        SetPreviewMode();
        var modes = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        foreach (var (caption, twin) in new[] { ("总览", true), ("单游戏页", false) })
        {
            var radio = new RadioButton { Content = caption, IsChecked = twin == overview, Margin = new Thickness(0, 0, 14, 0),
                Foreground = Foreground, VerticalContentAlignment = VerticalAlignment.Center, GroupName = "ArtPreviewPage" };
            radio.Checked += async (_, _) => { _overview = twin; SetPreviewMode(); await PaintAsync(); };
            modes.Children.Add(radio);
        }
        Add(modes);
        Add(new Border { Child = _canvas, Margin = new Thickness(0, 12, 0, 3), HorizontalAlignment = HorizontalAlignment.Center });
        Add(new TextBlock { Text = "拖动图片调整位置，滚动鼠标滚轮缩放。", FontSize = 11, Foreground = Brushes.LightGray });
        var choices = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 11, 0, 4) };
        var auto = ReminderDelayWindow.MakeButton("自动（最新版本）"); auto.Click += async (_, _) => await Choose(null, null);
        var local = ReminderDelayWindow.MakeButton("本地图片…"); local.Margin = new Thickness(7, 0, 0, 0);
        local.IsEnabled = _cache != null;
        local.Click += async (_, _) =>
        {
            var dlg = new OpenFileDialog { Title = "选择本地背景", Filter = "图片|*.png;*.jpg;*.jpeg;*.webp", CheckFileExists = true };
            if (dlg.ShowDialog(this) == true) await Choose(null, dlg.FileName);
        };
        choices.Children.Add(auto); choices.Children.Add(local); Add(choices); Add(_choice); Add(_thumbs);
        var intensityRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 6) };
        intensityRow.Children.Add(new TextBlock { Text = "背景强度", Margin = new Thickness(0, 0, 10, 0) });
        _intensity.Value = Draft.Intensity; intensityRow.Children.Add(_intensity);
        var amount = new TextBlock { Text = Draft.Intensity + "%", Margin = new Thickness(7, 0, 0, 0) };
        intensityRow.Children.Add(amount); Add(intensityRow);
        _intensity.ValueChanged += async (_, _) => { Draft.Intensity = (int)_intensity.Value; amount.Text = Draft.Intensity + "%"; await PaintAsync(); };
        _status.Foreground = Brushes.LightSalmon; Add(_status);
        var actions = new DockPanel { Margin = new Thickness(0, 9, 0, 0) };
        var defaults = ReminderDelayWindow.MakeButton("恢复默认"); DockPanel.SetDock(defaults, Dock.Left);
        defaults.Click += async (_, _) => { Draft.Crops.Clear(); _intensity.Value = 100; await Choose(null, null, false); }; actions.Children.Add(defaults);
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = ReminderDelayWindow.MakeButton("取消"); cancel.IsCancel = true; right.Children.Add(cancel);
        _save.Margin = new Thickness(6, 0, 0, 0); _save.IsDefault = true;
        _save.Click += async (_, _) =>
        {
            body.IsEnabled = false;
            try
            {
                if (_localPath != null && _cache != null)
                {
                    var imported = await _cache.ImportAsync(_game, _localPath, _savedSelection, _closedToken.Token);
                    if (imported != _localHash) throw new InvalidDataException("本地图片已更改");
                    Draft.Selected = imported;
                }
                if (CurrentKey != null) Draft.Crops[CurrentKey] = _crop;
                if (!_closedToken.IsCancellationRequested) DialogResult = true;
            }
            catch (Exception) { _status.Text = "图片保存失败，请重新选择。"; }
            finally { body.IsEnabled = true; }
        };
        right.Children.Add(_save); actions.Children.Add(right); Add(actions); Content = body;
        _canvas.MouseLeftButtonDown += (_, e) => { _drag = e.GetPosition(_canvas); _canvas.CaptureMouse(); };
        _canvas.MouseLeftButtonUp += (_, _) => { _drag = null; _canvas.ReleaseMouseCapture(); };
        _canvas.LostMouseCapture += (_, _) => _drag = null;
        _canvas.MouseMove += async (_, e) =>
        {
            if (_drag is not { } start || _source == null) return;
            var p = e.GetPosition(_canvas); var anchor = _overview ? OverviewArtFrames.Anchor(_game) : new Point(.70, .22);
            var region = ArtImage.Region(_source.PixelWidth, _source.PixelHeight, FrameWidth, FrameHeight, _crop, anchor.X, anchor.Y);
            _crop = (_crop with
            {
                X = (region.Left + region.Width * anchor.X - (p.X - start.X) * region.Width / FrameWidth) / _source.PixelWidth,
                Y = (region.Top + region.Height * anchor.Y - (p.Y - start.Y) * region.Height / FrameHeight) / _source.PixelHeight
            }).Clamp();
            _drag = p; await PaintAsync();
        };
        _canvas.MouseWheel += async (_, e) => { _crop = (_crop with { Zoom = _crop.Zoom * Math.Pow(1.12, e.Delta / 120d) }).Clamp(); e.Handled = true; await PaintAsync(); };
        Loaded += async (_, _) => await PrepareAsync();
        Closed += (_, _) => { _closedToken.Cancel(); _cache?.PinEditor(_game, null); };
    }

    private void SetPreviewMode()
    {
        _single.Visibility = _overview ? Visibility.Collapsed : Visibility.Visible;
        _worlds.Visibility = _overview ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string Credit(string game) => game == "wuwa" ? "© KURO GAMES" : "© HYPERGRYPH";

    private static StreamGeometry WorldClip(string world)
    {
        var shape = new StreamGeometry();
        using (var path = shape.Open())
        {
            double top = FrameWidth * .60, bottom = FrameWidth * .40;
            if (world == "wuwa")
            {
                path.BeginFigure(new(0, 0), true, true);
                path.PolyLineTo([new(top, 0), new(bottom, FrameHeight), new(0, FrameHeight)], true, false);
            }
            else
            {
                path.BeginFigure(new(top, 0), true, true);
                path.PolyLineTo([new(FrameWidth, 0), new(FrameWidth, FrameHeight), new(bottom, FrameHeight)], true, false);
            }
        }
        shape.Freeze();
        return shape;
    }

    private static StreamGeometry CutCorner(double width, double height)
    {
        var shape = new StreamGeometry();
        using (var path = shape.Open())
        {
            path.BeginFigure(new Point(0, 0), true, true);
            path.PolyLineTo([new(Math.Max(0, width - 12), 0), new(width, 12), new(width, height), new(0, height)], true, false);
        }
        shape.Freeze();
        return shape;
    }
    private readonly TextBlock _credit;

    internal async Task PrepareAsync()
    {
        if (!_initial) return; _initial = false;
        await Choose(Draft.Selected, null, false);
        if (_preview != null && _source != null)
            AddThumbnail(_source, "当前预览图片", async () => await Choose(null, null));
        if (_gallery == null || _cache == null) return;
        try
        {
            var urls = await _gallery(_game, _closedToken.Token);
            foreach (var url in urls.Take(8))
            {
                _closedToken.Token.ThrowIfCancellationRequested();
                try
                {
                    var key = await _cache.OfficialAsync(_game, url, _savedSelection, _closedToken.Token);
                    var path = _cache.FileFor(_game, key)!;
                    var thumbnail = await Task.Run(() => ArtImage.Decode(ArtCache.ReadImage(path), 128), _closedToken.Token);
                    AddThumbnail(thumbnail, "选择公告背景", async () => await Choose(key, null));
                }
                catch (Exception) when (!_closedToken.IsCancellationRequested) { _status.Text = "部分公告图片暂不可用。"; }
            }
        }
        catch (Exception) when (!_closedToken.IsCancellationRequested) { _status.Text = "暂时无法载入公告图片。"; }
        catch (Exception) when (_closedToken.IsCancellationRequested) { }
    }

    private void AddThumbnail(BitmapSource thumbnail, string label, Func<Task> select)
    {
        var button = new Button
        {
            Content = new Image { Source = thumbnail, Stretch = Stretch.UniformToFill },
            Width = 46,
            Height = 32,
            Padding = new Thickness(1),
            Margin = new Thickness(1, 5, 2, 0),
            ToolTip = label
        };
        System.Windows.Automation.AutomationProperties.SetName(button, label);
        button.Click += async (_, _) => await select();
        _thumbs.Children.Add(button);
    }
    private async Task Choose(string? selected, string? local, bool remember = true)
    {
        int version = ++_sourceRevision;
        if (remember && CurrentKey != null) Draft.Crops[CurrentKey] = _crop;
        var path = local ?? _preview ?? _cache?.FileFor(_game, selected ?? _latest);
        _save.IsEnabled = false;
        try
        {
            byte[]? bytes = path == null ? null : await Task.Run(() => ArtCache.ReadImage(path));
            var source = bytes == null ? null : await Task.Run(() => ArtImage.Decode(bytes, 1280));
            if (version != _sourceRevision || _closedToken.IsCancellationRequested) return;
            _source = source; Draft.Selected = selected; _localPath = local;
            _localHash = local != null && bytes != null ? ArtCache.Hash(bytes) : null;
            _crop = Draft.Crop(CurrentKey, _game);
            _cache?.PinEditor(_game, CurrentKey);
            _choice.Text = local != null ? "当前：本地图片" : selected == null ? "当前：自动（最新版本）" :
                path?.EndsWith(".local", StringComparison.Ordinal) == true ? "当前：本地图片" : "当前：手动选择的公告图片";
            _status.Text = ""; await PaintAsync();
        }
        catch (Exception) { if (version == _sourceRevision) _status.Text = "图片无法读取，请选择有效的 PNG、JPEG 或 WebP。"; }
        finally { if (version == _sourceRevision) _save.IsEnabled = true; }
    }
    private async Task PaintAsync()
    {
        var revision = ++_paintRevision;
        var source = _source; var crop = _crop; int strength = Draft.Intensity;
        await _paintLock.WaitAsync();
        try
        {
            if (revision != _paintRevision || _closedToken.IsCancellationRequested) return;
            string copyright = strength > 0 && source != null && _localPath == null && (_preview != null || _cache?.FileFor(_game, CurrentKey)?.EndsWith(".official") == true)
                ? Credit(_game) : "";
            if (_overview)
            {
                var frames = source == null ? null : await Task.Run(() => ArtImage.ProcessOverview(source, _game, crop, strength, FrameWidth, FrameHeight));
                if (revision != _paintRevision || _closedToken.IsCancellationRequested) return;
                _editedWorld.Background = frames?[5] ?? ArtFallback.For(_game);
                _worldCredit.Text = copyright;
                return;
            }
            var layers = source == null ? null : await Task.Run(() => ArtImage.Process(source, _game, crop, strength, FrameWidth, FrameHeight));
            if (revision != _paintRevision || _closedToken.IsCancellationRequested) return;
            _canvas.Background = layers == null ? ArtFallback.For(_game) : new ImageBrush(layers.Shaded) { Stretch = Stretch.Fill };
            foreach (var (tile, glass) in _tiles)
            {
                glass.Image.ImageSource = layers?.Blurred;
                glass.Align(tile, _canvas);
            }
            _credit.Text = copyright;
        }
        catch (Exception) { _status.Text = "图片预览失败。"; }
        finally { _paintLock.Release(); }
    }
}
