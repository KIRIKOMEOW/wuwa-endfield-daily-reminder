using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;

namespace GameDailyWidget;

public sealed partial class MainWindow
{
    private static readonly Brush Ink = Paint("#141414");
    private static readonly Brush SafetyYellow = Paint("#F5E200");
    private static readonly Brush PaperMuted = Paint("#46453F");
    private static readonly Brush NightMuted = Paint("#D8D5DC");
    private static readonly Brush Sand = Paint("#F3E3BD");
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas");
    private static readonly Brush Hazard = MakeHazardBrush();
    private readonly Border _titleGlow = new() { Margin = new Thickness(-7, -3, -7, -3), IsHitTestVisible = false };
    private readonly Border _fillGlow = new()
    {
        Height = 11,
        HorizontalAlignment = HorizontalAlignment.Left,
        Margin = new Thickness(0, -4, 0, -4),
        CornerRadius = new CornerRadius(5),
        IsHitTestVisible = false
    };
    private readonly Path _streakDiamond = new()
    {
        Data = Geometry.Parse("M 0.5,3 L 3,0.5 5.5,3 3,5.5 Z"),
        Stroke = Gold,
        StrokeThickness = .8,
        Width = 6,
        Height = 6,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 4, 0),
        IsHitTestVisible = false
    };
    private readonly TextBlock _subtitle = Label("", 9, Gold);
    private readonly TextBlock _version = Label("", 11, TextColor);
    private readonly TextBlock _verCode = Label("VER.", 9, Muted, FontWeights.SemiBold);
    private readonly Border _versionBar = new();
    private readonly TextBlock _accountName = Label("", 11, TextColor, FontWeights.SemiBold);
    private readonly TextBlock _accountIdentity = Label("", 11, Muted);
    private readonly TextBlock _accountArrow = Label("▾", 11, Muted);
    private readonly TextBlock _dailyTriangle = Label("▲ ", 10, Paint("#B48700"));
    private readonly TextBlock _dailyLabel = Label("每日活跃度", 11, Muted);
    private readonly TextBlock _staminaCode = Label("", 9, Gold);
    private readonly TextBlock _dailyCode = Label("", 9, Gold);
    private readonly Border _staminaBox = new(), _dailyBox = new(), _streakChip = new(), _warningChip = new(), _segments = new();
    private readonly List<Border> _metricHighlights = [];
    private readonly Border _edgeOverlay = new() { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly TextBlock _ticks = Label("— — — — — —  SYS.STATUS", 8, Muted);
    private readonly Border _rule = new();
    private readonly Button _efSwitch = MakeButton("终末地", "切换到终末地");
    private readonly List<Button> _windowButtons = [];
    private readonly TextBlock _warningIcon = Label("! ", 10, Warning, FontWeights.Bold);
    private readonly Grid _measures = new(), _header = new(), _footer = new();
    private readonly Button _controlsAnchor = MakeButton("⚙", "统一设置");
    private string? _appliedSkin;
    private ContextMenu? _accountMenu;
    private readonly Action<string> _copyText;

    private bool IsEndfield => _currentGame == "endfield";
    private Brush FaceText => IsEndfield ? Ink : TextColor;
    private Brush FaceMuted => IsEndfield ? PaperMuted : NightMuted;
    private Brush FaceAccentText => IsEndfield ? Ink : Sand;
    private Brush TrackBrush => IsEndfield ? Hazard : Paint("#38FFFFFF");

    private static void Place(Grid grid, FrameworkElement element, double top, HorizontalAlignment alignment = HorizontalAlignment.Left)
    {
        element.VerticalAlignment = VerticalAlignment.Top;
        element.HorizontalAlignment = alignment;
        element.Margin = new Thickness(0, top, 0, 0);
        grid.Children.Add(element);
    }

    private static void Trim(TextBlock text)
    {
        text.TextTrimming = TextTrimming.CharacterEllipsis;
        text.TextWrapping = TextWrapping.NoWrap;
    }

    private Grid BuildSkinLayout()
    {
        var root = new Grid();
        root.SizeChanged += (_, _) => root.Clip = new RectangleGeometry(new Rect(root.RenderSize), 13, 13);
        root.Children.Add(_artSurface);
        _rail.Width = 15;
        _rail.HorizontalAlignment = HorizontalAlignment.Left;
        _rail.Background = SafetyYellow;
        _rail.BorderBrush=Ink; _rail.BorderThickness=new Thickness(0,0,2,0);
        var word = Label("E N D F I E L D", 9, Ink, FontWeights.Bold);
        word.FontFamily = new FontFamily("Bahnschrift");
        word.LayoutTransform = new RotateTransform(-90);
        word.HorizontalAlignment = HorizontalAlignment.Center;
        _rail.Child = word;
        root.Children.Add(_rail);

        // At 100% the compact face has a 163 DIP header, 104 DIP gauges,
        // 61 DIP status area and 28 DIP footer plus the outside margin.
        foreach (var height in new[] { 163d, 104, 61 })
            _layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(height) });
        _layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });
        _layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(_layout);
        BuildSkinHeader();
        Add(_layout, _header, 0);
        _measures.ColumnDefinitions.Add(new ColumnDefinition());
        _measures.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        _measures.ColumnDefinitions.Add(new ColumnDefinition());
        BuildMetric(_staminaBox, _staminaCode, _staminaLabel, _stamina, _staminaMax, _recovery, false);
        BuildMetric(_dailyBox, _dailyCode, _dailyLabel, _daily, _dailyMax, _dailyState, true);
        Grid.SetColumn(_dailyBox, 2);
        _measures.Children.Add(_staminaBox);
        _measures.Children.Add(_dailyBox);
        Add(_layout, _measures, 1);

        var tags = new Grid();
        var line = new Grid { Height = 24, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 10, 0, 0) };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition());
        Trim(_streak);
        _streak.FontSize = 10.5;
        _streakChip.MaxWidth = 157;
        _streakChip.Padding = new Thickness(8, 0, 8, 0);
        var streakContent = new DockPanel();
        streakContent.Children.Add(_streakDiamond);
        streakContent.Children.Add(_streak);
        _streakChip.Child = streakContent;
        _streakChip.Margin = new Thickness(0, 0, 7, 0);
        line.Children.Add(_streakChip);
        Trim(_summary);
        _summary.FontSize = 10.5;
        _summaryButton.Content = _summary;
        _summaryButton.HorizontalAlignment = HorizontalAlignment.Left;
        _summaryButton.Padding = new Thickness(9, 0, 9, 0);
        _summaryButton.Click += (_, _) => ToggleEvents();
        Grid.SetColumn(_summaryButton, 1);
        line.Children.Add(_summaryButton);
        tags.Children.Add(line);
        Trim(_status);
        var warning = new DockPanel();
        warning.Children.Add(_warningIcon);
        warning.Children.Add(_status);
        _warningChip.Child = warning;
        _warningChip.Height = 20;
        _warningChip.Padding = new Thickness(7, 0, 7, 0);
        var notices=new StackPanel {Orientation=Orientation.Horizontal};
        _warningChip.MaxWidth=285;notices.Children.Add(_warningChip);
        _faceNotice=MakeFraming(null);_faceNotice.Margin=new Thickness(5,0,0,0);notices.Children.Add(_faceNotice);
        Place(tags,notices,37);
        Add(_layout, tags, 2);
        _detailScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _detailScroll.Content = _details;
        _detailScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _detailsPanel.Child = _detailScroll;
        _detailsPanel.Visibility = Visibility.Collapsed;
        _detailsPanel.Padding = new Thickness(10, 6, 10, 7);
        _detailsPanel.Margin = new Thickness(0, 0, 0, 8);
        Add(_layout, _detailsPanel, 3);

        var segmentButtons = new StackPanel { Orientation = Orientation.Horizontal };
        _flip.Content = "鸣潮";
        _flip.ToolTip = "切换到鸣潮";
        foreach (var b in new[] { _flip, _efSwitch })
        {
            b.FontSize = 11;
            b.Padding = new Thickness(10, 0, 10, 0);
            segmentButtons.Children.Add(b);
        }
        _segments.Child = segmentButtons;
        _segments.HorizontalAlignment = HorizontalAlignment.Left;
        _footer.Children.Add(_segments);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var b in new[] { _eventsButton, _poolsButton, _expand, _refresh })
        {
            b.FontSize = 11;
            b.Padding = new Thickness(10, 0, 10, 0);
            b.Margin = new Thickness(6, 0, 0, 0);
            buttons.Children.Add(b);
        }
        _poolsButton.Click += (_,_) => TogglePools();
        _eventsButton.Click += (_, _) => ToggleEvents();
        _footer.Children.Add(buttons);
        ToolTipService.SetShowOnDisabled(_refresh, true);
        Add(_layout, _footer, 4);
        _depth.IsHitTestVisible=false; root.Children.Add(_tileShadows); root.Children.Add(_depth);
        root.LayoutUpdated+=(_,_)=> { if(!_closed && _singlePage!=null) RefreshDepth(); };
        return root;
    }

    private readonly Button _backButton = MakeButton("‹ 总览","返回双生总览");
    private void BuildSkinHeader()
    {
        _backButton.Height = 20;
        _backButton.FontSize = 10;
        _backButton.Padding = new Thickness(6,0,7,0);
        _backButton.Click += (_,_) => ShowOverview();
        Place(_header,_backButton,6);
        var nameLine = new StackPanel { Orientation = Orientation.Horizontal };
        _title.FontWeight = FontWeights.Bold;
        var ink = Color.FromRgb(214, 187, 135);
        _titleGlow.Background = new RadialGradientBrush(Color.FromArgb(78, ink.R, ink.G, ink.B), Color.FromArgb(0, ink.R, ink.G, ink.B));
        _titleGlow.Background.Freeze();
        var titleLayer = new Grid();
        titleLayer.Children.Add(_titleGlow);
        titleLayer.Children.Add(_title);
        nameLine.Children.Add(titleLayer);
        var badge = new Border
        {
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(4, 1, 4, 1),
            Margin = new Thickness(8, 2, 0, 0),
            Background = Paint("#A62B2834"),
            VerticalAlignment = VerticalAlignment.Center,
            Child = _demoMarker
        };
        _demoMarker.Foreground = TextColor;
        badge.SetBinding(VisibilityProperty, new System.Windows.Data.Binding(nameof(Visibility)) { Source = _demoMarker });
        nameLine.Children.Add(badge);
        Place(_header, nameLine, 37);
        Place(_header, _subtitle, 70);
        Trim(_version);
        var versionRow = new DockPanel();
        _verCode.FontFamily = Mono;
        _verCode.Margin = new Thickness(0, 0, 5, 0);
        versionRow.Children.Add(_verCode);
        versionRow.Children.Add(_version);
        _versionBar.Child = versionRow;
        _versionBar.Height = 21;
        _versionBar.MaxWidth = 245;
        _versionBar.Padding = new Thickness(7, 0, 10, 0);
        Place(_header, _versionBar, 88);
        _account.HorizontalContentAlignment = HorizontalAlignment.Left;
        _account.MaxWidth = 244;
        _account.Height = 22;
        _account.Padding = new Thickness(0);
        _account.Background = Brushes.Transparent;
        var accountLine = new Grid();
        accountLine.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MaxWidth = 109 });
        accountLine.ColumnDefinitions.Add(new ColumnDefinition());
        accountLine.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Trim(_accountName); Trim(_accountIdentity);
        _accountName.MaxWidth = 103;
        _accountIdentity.MaxWidth = 124;
        _accountName.Margin = new Thickness(0, 0, 6, 0);
        _accountArrow.Margin = new Thickness(4, 0, 0, 0);
        accountLine.Children.Add(_accountName);
        Grid.SetColumn(_accountIdentity, 1); accountLine.Children.Add(_accountIdentity);
        Grid.SetColumn(_accountArrow, 2); accountLine.Children.Add(_accountArrow);
        _account.Content = accountLine;
        Place(_header, _account, 113);
        _login.Height = 24;
        Place(_header, _login, 113);
        _ticks.FontFamily = Mono;
        Place(_header, _ticks, 145);
        _rule.Width = 150; _rule.Height = 1;
        _rule.Background = Gradient("#00D6BB87", "#B3D6BB87");
        Place(_header, _rule, 141, HorizontalAlignment.Right);

        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        var hide = MakeButton("—", "隐藏到托盘，继续更新");
        var close = MakeButton("×", "隐藏到托盘，继续更新；彻底退出请右键托盘图标");
        _controlsAnchor.Click += (_, _) => ShowSettings();
        hide.Click += (_, _) => Hide();
        close.Click += (_, _) => Hide();
        foreach (var b in new[] { _controlsAnchor, hide, close })
        {
            b.Width = b.Height = 24;
            b.Padding = new Thickness(0);
            b.Margin = new Thickness(6, 0, 0, 0);
            b.FontSize = 12;
            _windowButtons.Add(b);
            controls.Children.Add(b);
        }
        Place(_header, controls, 13, HorizontalAlignment.Right);
    }

    private void BuildMetric(Border box, TextBlock code, TextBlock label, TextBlock value, TextBlock max, TextBlock hint, bool daily)
    {
        box.Padding = new Thickness(13, 9, 13, 8);
        var body = new Grid();
        var heading = new StackPanel { Orientation = Orientation.Horizontal };
        code.FontFamily = Mono;
        code.Margin = new Thickness(0, 0, 6, 0);
        heading.Children.Add(code); heading.Children.Add(label);
        Place(body, heading, 0);
        var values = new StackPanel { Orientation = Orientation.Horizontal, Height = 48 };
        value.FontSize = 40;
        value.VerticalAlignment = VerticalAlignment.Center;
        max.FontSize = 15;
        max.Margin = new Thickness(5, 0, 0, 5);
        max.VerticalAlignment = VerticalAlignment.Bottom;
        values.Children.Add(value); values.Children.Add(max);
        Place(body, values, 17);
        if (daily)
        {
            _progressTrack.Margin = new Thickness(0);
            var glow = new LinearGradientBrush { StartPoint = new Point(.5, 0), EndPoint = new Point(.5, 1) };
            glow.GradientStops.Add(new GradientStop(Color.FromArgb(0, 214, 187, 135), 0));
            glow.GradientStops.Add(new GradientStop(Color.FromArgb(118, 214, 187, 135), .5));
            glow.GradientStops.Add(new GradientStop(Color.FromArgb(0, 214, 187, 135), 1));
            glow.Freeze();
            _fillGlow.Background = glow;
            _fillGlow.SetBinding(WidthProperty, new Binding(nameof(ActualWidth)) { Source = _progress });
            _progressTrack.Children.Add(_fillGlow);
            _progressTrack.Children.Add(_progress);
            Place(body, _progressTrack, 65, HorizontalAlignment.Stretch);
        }
        Trim(hint);
        hint.FontSize = 10.5;
        var hintLine = new DockPanel();
        if (daily) hintLine.Children.Add(_dailyTriangle);
        hintLine.Children.Add(hint);
        Place(body, hintLine, daily ? 74 : 67, HorizontalAlignment.Stretch);
        var layers = new Grid();
        var highlight = new Border
        {
            Height = 1,
            Background = Gradient("#0AFFFFFF", "#48FFFFFF"),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(-6, -9, -6, 0),
            IsHitTestVisible = false
        };
        _metricHighlights.Add(highlight);
        layers.Children.Add(highlight);
        layers.Children.Add(body);
        box.Child = layers;
        box.SizeChanged += (_, _) => ClipTile(box);
    }

    private static Brush Gradient(string start, string end)
    {
        var brush = new LinearGradientBrush((Color)ColorConverter.ConvertFromString(start), (Color)ColorConverter.ConvertFromString(end), 0);
        brush.Freeze();
        return brush;
    }

    private static DrawingBrush MakeHazardBrush()
    {
        var tile = new DrawingGroup();
        using (var drawing = tile.Open())
        {
            drawing.DrawRectangle(Paint("#D9D6CC"), null, new Rect(0, 0, 14, 14));
            var pen = new Pen(Paint("#C9C5B9"), 5);
            drawing.DrawLine(pen, new Point(-7, 14), new Point(7, 0));
            drawing.DrawLine(pen, new Point(7, 14), new Point(21, 0));
        }
        var brush = new DrawingBrush(tile)
        {
            TileMode = TileMode.Tile,
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, 14, 14),
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, 14, 14)
        };
        brush.Freeze();
        return brush;
    }
    private static Geometry Polygon(params Point[] points)
    {
        var result = new StreamGeometry();
        using (var path = result.Open())
        {
            path.BeginFigure(points[0], true, true);
            path.PolyLineTo(points[1..], true, false);
        }
        result.Freeze();
        return result;
    }

    private void ClipTile(Border tile)
    {
        double w = tile.ActualWidth, h = tile.ActualHeight;
        tile.Clip = IsEndfield && w > 14 && h > 14 ? Polygon(new(0, 0), new(w - 14, 0), new(w, 14), new(w, h), new(0, h)) : null;
    }

    private readonly Dictionary<double, ControlTemplate> _buttonChrome = [];
    private ControlTemplate Chrome(double radius)
    {
        if (_buttonChrome.TryGetValue(radius, out var template)) return template;
        template = (ControlTemplate)XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Button">
              <Grid>
                <Border x:Name="Body" Background="{TemplateBinding Background}" Padding="{TemplateBinding Padding}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="RADIUS">
                  <ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" VerticalAlignment="Center" RecognizesAccessKey="True"/>
                </Border>
                <Border x:Name="Focus" IsHitTestVisible="False" BorderThickness="0" CornerRadius="RADIUS" BorderBrush="{TemplateBinding Foreground}"/>
              </Grid>
              <ControlTemplate.Triggers>
                <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Focus" Property="BorderThickness" Value="1"/>HOVER</Trigger>
                <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Focus" Property="BorderThickness" Value="2"/></Trigger>
                <Trigger Property="IsPressed" Value="True"><Setter TargetName="Body" Property="Opacity" Value=".75"/></Trigger>
                <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value=".55"/></Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate>
            """.Replace("HOVER",radius==0?"<Setter TargetName=\"Focus\" Property=\"Background\" Value=\"#0D141414\"/>":"").Replace("RADIUS", radius.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        _buttonChrome[radius] = template;
        return template;
    }

    private Brush Glass(FrameworkElement element, string tint) => GlassBrush(element, (Color)ColorConverter.ConvertFromString(tint));

    private void SkinButton(Button b, bool primary = false)
    {
        b.Template = Chrome(IsEndfield ? 0 : 7);
        b.BorderThickness = new Thickness(1);
        b.BorderBrush = IsEndfield ? Ink : Paint("#3DFFFFFF");
        b.Foreground = FaceText;
        b.Background = IsEndfield && primary ? SafetyYellow : Glass(b, IsEndfield ? "#AEFAFAF6" : "#80101028");
        b.FontWeight = IsEndfield && primary ? FontWeights.SemiBold : FontWeights.Normal;
    }

    private Button PanelButton(string text, string tip)
    {
        var b = MakeButton(text, tip);
        SkinButton(b);
        return b;
    }

    // WPF text runs have no CSS-style letter spacing. The fixed advances
    // belong to the heading only and remain in logical DIPs at every scale.
    private static void TrackLetters(TextBlock label, string value, double spacing)
    {
        label.Inlines.Clear();
        for (int i = 0; i < value.Length; i++)
        {
            if (i != 0) label.Inlines.Add(new InlineUIContainer(new Border { Width = spacing }));
            label.Inlines.Add(new Run(value[i].ToString()));
        }
        System.Windows.Automation.AutomationProperties.SetName(label, value);
    }

    private void ApplyFaceSkin()
    {
        if (_appliedSkin == _currentGame) return;
        _appliedSkin = _currentGame;
        bool light = IsEndfield;
        SetPanelDepth();
        foreach(var b in new[]{_eventsButton,_poolsButton,_expand,_refresh}) { b.Height=light?24:double.NaN; b.VerticalAlignment=VerticalAlignment.Top; if(!light)b.VerticalAlignment=VerticalAlignment.Stretch; }
        TrackLetters(_title, light ? "终末地" : "鸣潮", light ? 1 : 4);
        _titleGlow.Visibility = _fillGlow.Visibility = _streakDiamond.Visibility = light ? Visibility.Collapsed : Visibility.Visible;
        _title.FontSize = light ? 23 : 24;
        _title.Foreground = light ? Ink : Sand;
        TrackLetters(_subtitle, light ? "// ARKNIGHTS: ENDFIELD" : "WUTHERING WAVES", light ? 1.5 : 3);
        _subtitle.FontFamily = light ? Mono : new FontFamily("Segoe UI");
        _subtitle.FontSize = light ? 9 : 9.5;
        _subtitle.Foreground = light ? PaperMuted : Sand;
        _rail.Visibility = _ticks.Visibility = _verCode.Visibility = light ? Visibility.Visible : Visibility.Collapsed;
        _rule.Visibility = light ? Visibility.Collapsed : Visibility.Visible;
        if (light) TrackLetters(_ticks, "— — — — — — SYS.STATUS", 1.5);
        _ticks.Foreground = PaperMuted;
        _layout.Margin = new Thickness(light ? 29 : 19, 0, light ? 17 : 19, 14);
        _measures.Margin = new Thickness(0, 0, light ? 10 : 4, 0);
        _measures.ColumnDefinitions[1].Width = new GridLength(light ? 10 : 12);
        _card.BorderBrush = Paint(light ? "#40000000" : "#59D6BB87");
        _card.Background = Paint(light ? "#EEECE4" : "#111122");
        _version.Foreground = light ? SafetyYellow : Paint("#E9DDF7");
        _version.FontWeight = light ? FontWeights.SemiBold : FontWeights.Normal;
        _versionBar.BorderThickness = new Thickness(light ? 0 : 2, 0, 0, 0);
        _versionBar.BorderBrush = Gold;
        _versionBar.Background = light ? Ink : Gradient("#A0281E50", "#00281E50");
        _versionBar.SizeChanged -= VersionSizeChanged;
        _versionBar.SizeChanged += VersionSizeChanged;
        ClipVersion();
        _account.Template = Chrome(3);
        _account.BorderThickness = new Thickness(0);
        _account.Foreground = _accountName.Foreground = FaceText;
        _accountIdentity.Foreground = _accountArrow.Foreground = FaceMuted;

        foreach (var button in _windowButtons) SkinButton(button);
        SkinButton(_login, true);
        foreach (var b in new[] { _backButton, _expand, _eventsButton, _poolsButton, _flip, _efSwitch }) SkinButton(b);
        SkinButton(_refresh, true);
        _segments.CornerRadius = new CornerRadius(light ? 0 : 8);
        _segments.BorderThickness = new Thickness(1);
        _segments.BorderBrush = light ? Ink : Paint("#44FFFFFF");
        _segments.Background = Glass(_segments, light ? "#ADFFFFFF" : "#80101028");
        foreach (var b in new[] { _flip, _efSwitch })
        {
            bool selected = light == (b == _efSwitch);
            b.Template = Chrome(0);
            b.BorderThickness = new Thickness(0);
            b.Background = selected ? (light ? Ink : Paint("#E5D6BB87")) : Brushes.Transparent;
            b.Foreground = selected ? (light ? SafetyYellow : Ink) : FaceMuted;
            b.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
        }
        _segments.SizeChanged -= SegmentsSizeChanged;
        _segments.SizeChanged += SegmentsSizeChanged;
        ClipSegments();
        foreach (var box in new[] { _staminaBox, _dailyBox, _detailsPanel })
        {
            box.CornerRadius = new CornerRadius(light ? 0 : 10);
            box.BorderThickness = new Thickness(1);
            box.BorderBrush = Paint(light ? "#24000000" : "#29FFFFFF");
            box.Background = Glass(box, light ? "#9EFAFAF6" : "#66101028");
        }
        foreach (var line in _metricHighlights) line.Visibility = light ? Visibility.Collapsed : Visibility.Visible;
        ClipTile(_staminaBox); ClipTile(_dailyBox);
        TrackLetters(_staminaCode, light ? "SANITY" : "◇", 1.5);
        TrackLetters(_dailyCode, light ? "ACTIVITY" : "◇", 1.5);
        _staminaCode.Foreground = _dailyCode.Foreground = light ? PaperMuted : Gold;
        _staminaLabel.Foreground = _dailyLabel.Foreground = light ? Ink : NightMuted;
        foreach (var n in new[] { _stamina, _daily })
        {
            n.FontFamily = new FontFamily(light ? "Bahnschrift, Segoe UI" : "Segoe UI Variable Display, Segoe UI");
            n.FontWeight = light ? FontWeights.SemiBold : FontWeights.Light;
            n.Foreground = FaceText;
        }
        _staminaMax.Foreground = _dailyMax.Foreground = FaceMuted;
        _progressTrack.Height = _progress.Height = light ? 6 : 3;
        _progressTrack.Background = TrackBrush;
        _progress.Background = light ? Ink : Gold;
        _progress.CornerRadius = new CornerRadius(light ? 0 : 1.5);
        _detailScroll.Resources[typeof(ScrollBar)] = PanelScrollBar(light);
        _streakChip.CornerRadius = _warningChip.CornerRadius = new CornerRadius(light ? 0 : 12);
        _streakChip.BorderThickness = _warningChip.BorderThickness = new Thickness(1);
        _streakChip.Background = light ? Ink : Glass(_streakChip, "#80101028");
        _streakChip.BorderBrush = light ? Ink : Paint("#3DFFFFFF");
        _streak.Foreground = TextColor;
        _warningChip.Background = light ? SafetyYellow : Glass(_warningChip, "#BB422431");
        _warningChip.BorderBrush = light ? Ink : Paint("#AAEBAC91");
        _status.Foreground = _warningIcon.Foreground = light ? Ink : Paint("#FFE0CA");
        _summaryButton.Template = Chrome(light ? 0 : 12);
        _summaryButton.BorderThickness = new Thickness(1);
        SetChipColor(false);
        RefreshArt();
    }

    private void VersionSizeChanged(object sender, SizeChangedEventArgs e) => ClipVersion();
    private void ClipVersion()
    {
        var w = _versionBar.ActualWidth; var h = _versionBar.ActualHeight;
        _versionBar.Clip = IsEndfield && w > 8 && h > 0 ? Polygon(new(0, 0), new(w, 0), new(w - 8, h), new(0, h)) : null;
    }

    private void SegmentsSizeChanged(object sender, SizeChangedEventArgs e) => ClipSegments();
    private void ClipSegments() => _segments.Clip = IsEndfield ? null : new RectangleGeometry(new Rect(_segments.RenderSize), 8, 8);

    private bool? _chipHot;
    private string? _chipFace;
    private void SetChipColor(bool urgent)
    {
        if (_chipHot == urgent && _chipFace == _currentGame) return;
        _chipHot = urgent; _chipFace = _currentGame;
        _summary.Foreground = IsEndfield ? (urgent ? Ink : TextColor) : urgent ? Paint("#FFC2AE") : TextColor;
        _summaryButton.Foreground = _summary.Foreground;
        _summaryButton.Background = IsEndfield ? (urgent ? SafetyYellow : Ink) : Glass(_summaryButton, "#80101028");
        _summaryButton.BorderBrush = IsEndfield ? _summaryButton.Background : Paint(urgent ? "#7FFFAA8C" : "#3DFFFFFF");
    }

    private void RefreshSkinText(GameViewState state, DateTimeOffset now)
    {
        var remaining = DailyReminderSchedule.Remaining(now);
        _dailyState.Text = state.DailyState switch
        {
            "complete" => "已达标",
            "incomplete" when state.MaxDaily is { } max && state.Daily is { } daily => $"还差 {Math.Max(0, max - daily)} · 距刷新 {remaining}",
            "incomplete" => $"活跃度未达标 · 距刷新 {remaining}",
            "locked" => "尚未解锁",
            _ => "状态未知"
        };
        _dailyState.Foreground = state.DailyState is "complete" or "incomplete" ? FaceAccentText : FaceMuted;
        _dailyState.ToolTip = _dailyState.Text;
        _dailyTriangle.Visibility = IsEndfield && state.DailyState == "incomplete" ? Visibility.Visible : Visibility.Collapsed;
        _recovery.ToolTip = _recovery.Text;
        var version = VersionCaption.For(_eventStore?.Data, _currentGame, now);
        _version.Text = version;
        _versionBar.ToolTip = version;
        _versionBar.Visibility = version.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        bool stale = !_preview && !state.Demo && (state.Stale || state.UpdatedAt is { } updated && now - updated > TimeSpan.FromMinutes(IsEndfield ? 15 : state.RefreshMinutes + 1));
        var message = _localNotes.GetValueOrDefault(_currentGame);
        if (message == null && !string.IsNullOrWhiteSpace(state.Error))
            message = state.UpdatedAt is { } prior ? $"更新失败 · 上次 {LocalTime(prior)}，数据可能过期 · {state.Error}" : state.Error;
        if (message == null && (state.Busy || _actions.Contains(_currentGame)))
            message = state.UpdatedAt is { } previous ? $"正在更新 · 上次 {LocalTime(previous)}" : "正在更新";
        if (message == null && stale)
            message = state.UpdatedAt is { } old ? $"上次 {LocalTime(old)}，数据可能过期" : "数据可能过期，请刷新";
        _status.Text = message?.ReplaceLineEndings(" ") ?? "";
        _warningChip.ToolTip = message;
        _warningChip.Visibility = message == null ? Visibility.Collapsed : Visibility.Visible;
        _refresh.ToolTip = $"刷新当前游戏的数据 · {message ?? state.Status} · 每 {state.RefreshMinutes} 分钟更新";
    }

    private void ShowAccountMenu()
    {
        var menu = AccountMenuForPreview();
        menu.PlacementTarget = _account;
        menu.Placement = PlacementMode.Bottom;
        _accountMenu = menu;
        menu.IsOpen = true;
        menu.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }

    internal ContextMenu AccountMenuForPreview()
    {
        var controller = CurrentController;
        var state = controller.State;
        var menu = new ContextMenu { FontSize = 11, PlacementTarget = _account };
        foreach (var account in state.Accounts)
        {
            var name = Label(account.Label, 11, TextColor);
            name.MaxWidth = 248;
            Trim(name);
            var item = new MenuItem { Header = name, ToolTip = account.Label, IsCheckable = true, IsChecked = account.Key == state.SelectedAccountKey };
            System.Windows.Automation.AutomationProperties.SetName(item, account.Label);
            item.Click += async (_, _) => await SelectAccountAsync(account);
            menu.Items.Add(item);
        }
        if (state.Accounts.Count > 0) menu.Items.Add(new Separator());
        var copy = new MenuItem { Header = IsEndfield ? "复制角色 ID" : "复制特征码", IsEnabled = !string.IsNullOrWhiteSpace(state.CopyIdentity) };
        copy.Click += (_, _) => CopyIdentity();
        menu.Items.Add(copy);
        var login = new MenuItem { Header = IsEndfield ? "扫码登录" : "重新扫描", IsEnabled = !state.Busy && !_actions.Contains(_currentGame) };
        login.Click += async (_, _) => await RunActionAsync(controller, c => c.LoginAsync(this), previewBlock: true);
        menu.Items.Add(login);
        DarkMenuTheme.Apply(menu);
        return menu;
    }

    private readonly Dictionary<bool, ControlTemplate> _claimTemplates = [];
    private ControlTemplate ClaimTemplate()
    {
        if (_claimTemplates.TryGetValue(IsEndfield, out var template)) return template;
        template = (ControlTemplate)XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="CheckBox">
              <StackPanel Orientation="Horizontal" Background="Transparent">
                <Border x:Name="Box" Width="13" Height="13" CornerRadius="RADIUS" BorderThickness="1" BorderBrush="EDGE" Background="BOX">
                  <Path x:Name="Mark" Data="M1,5 L4,8 L10,2" Stroke="CHECK" StrokeThickness="1.7" Visibility="Hidden"/>
                </Border>
                <ContentPresenter VerticalAlignment="Center" Margin="4,0,0,0"/>
              </StackPanel>
              <ControlTemplate.Triggers>
                <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Box" Property="BorderBrush" Value="HOVER"/></Trigger>
                <Trigger Property="IsChecked" Value="True">
                  <Setter TargetName="Box" Property="Background" Value="FILLED"/>
                  <Setter TargetName="Mark" Property="Visibility" Value="Visible"/>
                </Trigger>
                <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Box" Property="BorderThickness" Value="2"/></Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate>
            """.Replace("RADIUS", IsEndfield ? "0" : "2").Replace("EDGE", IsEndfield ? "#141414" : "#D6BB87")
               .Replace("HOVER", IsEndfield ? "#756800" : "#F3E3BD").Replace("FILLED", IsEndfield ? "#141414" : "#A0111022")
               .Replace("BOX", IsEndfield ? "#FAFAF6" : "#A0111022").Replace("CHECK", IsEndfield ? "#F5E200" : "#F3D9A1"));
        _claimTemplates[IsEndfield] = template;
        return template;
    }

    internal void SetPreviewFailure()
    {
        if (_preview) Note("更新失败 · 演示连接暂时不可用，请稍后刷新");
    }

    internal void SetCollapsedSkinStrip(bool collapsed) =>
        _edgeOverlay.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
}
