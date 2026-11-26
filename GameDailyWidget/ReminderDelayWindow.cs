using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GameDailyWidget;

/// <summary>A local editor only; the host owns persistence and reminder scheduling.</summary>
public sealed class ReminderDelayWindow : Window
{
    private static readonly Brush TextColor = Paint("#F2F0E9");
    private static readonly Brush MutedColor = Paint("#B7B8C2");
    private readonly TextBox _input;
    private readonly TextBlock _error;

    public int SelectedSeconds { get; private set; }

    public ReminderDelayWindow(int seconds)
    {
        SelectedSeconds = LaunchReminderDelay.NormalizeSeconds(seconds);
        Title = "启动提醒延时";
        Width = 430;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = Paint("#22232A");
        Foreground = TextColor;
        FontFamily = new FontFamily("Microsoft YaHei UI");
        FontSize = 13;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

        var body = new StackPanel { Margin = new Thickness(22, 20, 22, 22) };
        body.Children.Add(new TextBlock
        {
            Text = "启动后多久提醒", FontSize = 17, FontWeight = FontWeights.SemiBold,
            Foreground = TextColor, Margin = new Thickness(0, 0, 0, 10)
        });
        body.Children.Add(new TextBlock
        {
            Text = "启用启动提醒后，每次打开软件按此延时发送一次日常进度通知。",
            TextWrapping = TextWrapping.Wrap, Foreground = MutedColor, LineHeight = 21,
            Margin = new Thickness(0, 0, 0, 18)
        });

        var inputRow = new Grid();
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _input = new TextBox
        {
            Text = SelectedSeconds.ToString(CultureInfo.InvariantCulture),
            Background = Paint("#16171C"), Foreground = TextColor, CaretBrush = TextColor,
            BorderBrush = Paint("#777A88"), BorderThickness = new Thickness(1),
            SelectionBrush = Paint("#D6BB87"), SelectionOpacity = .4,
            FontSize = 18, Padding = new Thickness(10, 8, 10, 8),
            VerticalContentAlignment = VerticalAlignment.Center, MaxLength = 16,
            ToolTip = $"整数秒，范围 {LaunchReminderDelay.MinimumSeconds}–{LaunchReminderDelay.MaximumSeconds}"
        };
        inputRow.Children.Add(_input);
        var unit = new TextBlock
        {
            Text = "秒", Foreground = TextColor, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0)
        };
        Grid.SetColumn(unit, 1);
        inputRow.Children.Add(unit);
        body.Children.Add(inputRow);
        body.Children.Add(new TextBlock
        {
            Text = $"范围 {LaunchReminderDelay.MinimumSeconds}–{LaunchReminderDelay.MaximumSeconds} 秒 · 60 秒 = 1 分钟",
            Foreground = MutedColor, FontSize = 12, Margin = new Thickness(0, 7, 0, 0)
        });

        _error = new TextBlock
        {
            Foreground = Paint("#FFB3A8"), FontSize = 12, TextWrapping = TextWrapping.Wrap,
            MinHeight = 34, Margin = new Thickness(0, 7, 0, 0)
        };
        _input.TextChanged += (_, _) => _error.Text = "";
        body.Children.Add(_error);
        body.Children.Add(new TextBlock
        {
            Text = "本次尚在等待时，修改后会从启动时间重新计算；本次已经发送后不会重复提醒。",
            TextWrapping = TextWrapping.Wrap, Foreground = MutedColor, FontSize = 12,
            LineHeight = 20, Margin = new Thickness(0, 0, 0, 20)
        });

        var actions = new DockPanel { LastChildFill = false };
        var reset = MakeButton($"恢复默认 {LaunchReminderDelay.DefaultSeconds} 秒");
        reset.Click += (_, _) =>
        {
            _input.Text = LaunchReminderDelay.DefaultSeconds.ToString(CultureInfo.InvariantCulture);
            _error.Text = "";
            if (PreviewDisplay.Screen == null) _input.Focus();
            _input.SelectAll();
        };
        DockPanel.SetDock(reset, Dock.Left);
        actions.Children.Add(reset);
        var rightActions = new StackPanel { Orientation = Orientation.Horizontal };
        var cancel = MakeButton("取消");
        cancel.IsCancel = true;
        cancel.Margin = new Thickness(0, 0, 8, 0);
        rightActions.Children.Add(cancel);
        var save = MakeButton("保存", primary: true);
        save.IsDefault = true;
        save.Click += (_, _) => SaveSelection();
        rightActions.Children.Add(save);
        DockPanel.SetDock(rightActions, Dock.Right);
        actions.Children.Add(rightActions);
        body.Children.Add(actions);
        Content = body;
        Loaded += (_, _) => { if (PreviewDisplay.Screen == null) _input.Focus(); _input.SelectAll(); };
    }

    internal static bool TryReadSeconds(string? input, out int seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(input)) return false;
        var text = input.Trim();
        foreach (var character in text)
            if (character is < '0' or > '9') return false;
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            || parsed < LaunchReminderDelay.MinimumSeconds || parsed > LaunchReminderDelay.MaximumSeconds)
            return false;
        seconds = parsed;
        return true;
    }

    private void SaveSelection()
    {
        if (!TryReadSeconds(_input.Text, out var seconds))
        {
            _error.Text = $"请输入 {LaunchReminderDelay.MinimumSeconds}–{LaunchReminderDelay.MaximumSeconds} 之间的整数秒。";
            if (PreviewDisplay.Screen == null) _input.Focus();
            _input.SelectAll();
            return;
        }
        SelectedSeconds = seconds;
        DialogResult = true;
    }

    internal static Button MakeButton(string label, bool primary = false)
    {
        var background = Paint(primary ? "#D6BB87" : "#34363F");
        var borderColor = Paint(primary ? "#D6BB87" : "#747784");
        var border = new FrameworkElementFactory(typeof(Border), "ButtonBorder");
        border.SetValue(Border.BackgroundProperty, background);
        border.SetValue(Border.BorderBrushProperty, borderColor);
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        border.SetValue(Border.PaddingProperty, new Thickness(12, 8, 12, 8));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        presenter.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
        border.AppendChild(presenter);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, Paint(primary ? "#E8CFA1" : "#484B57"), "ButtonBorder"));
        template.Triggers.Add(hover);
        var focus = new Trigger { Property = IsKeyboardFocusedProperty, Value = true };
        focus.Setters.Add(new Setter(Border.BorderBrushProperty, Paint("#F4E3B5"), "ButtonBorder"));
        focus.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(2), "ButtonBorder"));
        template.Triggers.Add(focus);
        return new Button
        {
            Content = label, MinWidth = 62, Foreground = primary ? Paint("#17181D") : TextColor,
            Background = background, BorderBrush = borderColor, Template = template,
            Cursor = System.Windows.Input.Cursors.Hand
        };
    }

    internal static Brush Paint(string color)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(color)!;
        brush.Freeze();
        return brush;
    }
}
