using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QRCoder;

namespace EndfieldDesktopWidget;

public sealed class LoginWindow : Window
{
    private readonly SklandClient _client;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _qrCancellation;
    private readonly Image _qrImage = new() { Width = 218, Height = 218, Stretch = Stretch.Uniform };
    private readonly TextBlock _status = new()
    {
        Text = "正在生成二维码…", FontSize = 12, Foreground = Brush("#C6C6C6"),
        TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 15, 0, 0)
    };
    private readonly TextBlock _instructions = new()
    {
        Text = "使用森空岛 App 扫一扫\n确认授权后，将自动读取你的终末地角色。",
        FontSize = 12, Foreground = Brush("#A2A2A2"), TextWrapping = TextWrapping.Wrap,
        LineHeight = 21, Margin = new Thickness(0, 12, 0, 20)
    };
    private readonly Grid _qrPanel = new();
    private readonly StackPanel _tokenPanel = new() { Visibility = Visibility.Collapsed };
    private readonly PasswordBox _tokenInput = new()
    {
        Height = 38, FontSize = 14, Padding = new Thickness(9, 7, 9, 7), MaxLength = 4096,
        Background = Brush("#191919"), Foreground = Brushes.White, BorderBrush = Brush("#555555")
    };
    private readonly TextBlock _tokenError = new()
    {
        Foreground = Brush("#F0B26D"), FontSize = 11, TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 9, 0, 9)
    };
    private readonly Button _retry;
    public string? AccountToken { get; private set; }

    public LoginWindow(SklandClient client)
    {
        _client = client;
        Title = "登录森空岛 · 终末地桌面日常";
        Width = 420;
        Height = 540;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        Topmost = true;
        FontFamily = new FontFamily("Microsoft YaHei UI");
        UseLayoutRounding = true;

        var card = new Border
        {
            Background = Brush("#FA242424"), BorderBrush = Brush("#5A5A5A"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(27, 23, 27, 22)
        };
        var content = new StackPanel();
        var header = new Grid();
        var title = new TextBlock
        {
            Text = "登录森空岛", FontSize = 23, Foreground = Brushes.White, FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        title.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        header.Children.Add(title);
        var close = MakeButton("×");
        close.Width = 30;
        close.Height = 30;
        close.FontSize = 19;
        close.Padding = new Thickness(0);
        close.HorizontalAlignment = HorizontalAlignment.Right;
        close.Click += (_, _) => Close();
        header.Children.Add(close);
        content.Children.Add(header);
        content.Children.Add(_instructions);

        _qrPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(240) });
        _qrPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(62) });
        _qrPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(35) });
        var qrFrame = new Border
        {
            Background = Brushes.White, Width = 240, Height = 240, CornerRadius = new CornerRadius(5),
            HorizontalAlignment = HorizontalAlignment.Center, Child = _qrImage
        };
        _qrPanel.Children.Add(qrFrame);
        Grid.SetRow(_status, 1);
        _qrPanel.Children.Add(_status);
        var qrButtons = new Grid();
        qrButtons.ColumnDefinitions.Add(new ColumnDefinition());
        qrButtons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        qrButtons.ColumnDefinitions.Add(new ColumnDefinition());
        _retry = MakeButton("刷新二维码");
        _retry.Click += async (_, _) => await BeginQrAsync();
        qrButtons.Children.Add(_retry);
        var manual = MakeButton("使用账号 token");
        manual.Click += (_, _) => ShowTokenPanel();
        Grid.SetColumn(manual, 2);
        qrButtons.Children.Add(manual);
        Grid.SetRow(qrButtons, 2);
        _qrPanel.Children.Add(qrButtons);
        content.Children.Add(_qrPanel);

        _tokenPanel.Children.Add(new TextBlock
        {
            Text = "账号 token", FontSize = 12, Foreground = Brush("#D4D4D4"), Margin = new Thickness(0, 12, 0, 9)
        });
        _tokenPanel.Children.Add(_tokenInput);
        _tokenPanel.Children.Add(new TextBlock
        {
            Text = "请粘贴鹰角账号 token，不是手机号、密码或游戏 UID。也可粘贴含 data.content 的账号信息。",
            FontSize = 11, Foreground = Brush("#929292"), TextWrapping = TextWrapping.Wrap,
            LineHeight = 18, Margin = new Thickness(0, 10, 0, 0)
        });
        _tokenPanel.Children.Add(_tokenError);
        var submit = MakeButton("登录并读取日常数据", true);
        submit.Height = 38;
        submit.Click += (_, _) => SubmitToken();
        _tokenPanel.Children.Add(submit);
        var back = MakeButton("返回森空岛扫码");
        back.Margin = new Thickness(0, 12, 0, 0);
        back.Height = 35;
        back.Click += async (_, _) =>
        {
            _tokenPanel.Visibility = Visibility.Collapsed;
            _qrPanel.Visibility = Visibility.Visible;
            _instructions.Text = "使用森空岛 App 扫一扫\n确认授权后，将自动读取你的终末地角色。";
            await BeginQrAsync();
        };
        _tokenPanel.Children.Add(back);
        var accountPage = MakeButton("打开鹰角账号信息页  ↗");
        accountPage.Margin = new Thickness(0, 12, 0, 0);
        accountPage.Height = 35;
        accountPage.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("https://web-api.skland.com/account/info/hg") { UseShellExecute = true }); }
            catch (Exception ex) { _tokenError.Text = ex.Message; }
        };
        _tokenPanel.Children.Add(accountPage);
        var webLogin = MakeButton("先登录森空岛网页版  ↗");
        webLogin.Margin = new Thickness(0, 10, 0, 0);
        webLogin.Height = 32;
        webLogin.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("https://www.skland.com/") { UseShellExecute = true }); }
            catch (Exception ex) { _tokenError.Text = ex.Message; }
        };
        _tokenPanel.Children.Add(webLogin);
        content.Children.Add(_tokenPanel);
        content.Children.Add(new TextBlock
        {
            Text = "登录信息只保存在这台电脑，可随时从组件退出账号。",
            Foreground = Brush("#787878"), FontSize = 10, TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 21, 0, 0)
        });
        card.Child = content;
        Content = card;
        Loaded += async (_, _) =>
        {
            if (App.IsPreview)
            {
                _qrImage.Source = CreateQrImage("hypergryph://scan_login?scanId=preview-not-a-login-session");
                _status.Text = "界面预览 · 此二维码不能登录";
            }
            else await BeginQrAsync();
        };
        Closed += (_, _) =>
        {
            _lifetime.Cancel();
            _qrCancellation?.Cancel();
            _lifetime.Dispose();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; }
            if (e.Key == Key.Enter && _tokenPanel.Visibility == Visibility.Visible) { SubmitToken(); e.Handled = true; }
        };
    }

    private async Task BeginQrAsync()
    {
        if (_lifetime.IsCancellationRequested) return;
        _qrCancellation?.Cancel();
        var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _qrCancellation = request;
        _qrImage.Source = null;
        _status.Text = "正在生成二维码…";
        _status.Foreground = Brush("#C6C6C6");
        _retry.IsEnabled = false;
        try
        {
            var session = await _client.StartQrLoginAsync(request.Token);
            if (request.IsCancellationRequested) return;
            _qrImage.Source = CreateQrImage(session.QrContent);
            _status.Text = "等待森空岛扫码并确认授权";
            _retry.IsEnabled = true;
            var expiresAt = DateTimeOffset.UtcNow.AddMinutes(2);
            while (!request.IsCancellationRequested && DateTimeOffset.UtcNow < expiresAt)
            {
                var token = await _client.PollQrLoginAsync(session, request.Token);
                if (request.IsCancellationRequested) return;
                if (!string.IsNullOrWhiteSpace(token))
                {
                    AccountToken = token;
                    DialogResult = true;
                    return;
                }
                await Task.Delay(1500, request.Token);
            }
            if (!request.IsCancellationRequested)
            {
                _status.Text = "二维码已过期，请刷新后重新扫码";
                _status.Foreground = Brush("#F0B26D");
            }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!request.IsCancellationRequested)
            {
                _status.Text = "二维码暂时不可用：" + ex.Message;
                _status.Foreground = Brush("#F0B26D");
            }
        }
        finally
        {
            request.Dispose();
            if (ReferenceEquals(_qrCancellation, request))
            {
                _qrCancellation = null;
                if (!_lifetime.IsCancellationRequested) _retry.IsEnabled = true;
            }
        }
    }

    internal void ShowTokenPanel()
    {
        _qrCancellation?.Cancel();
        _qrPanel.Visibility = Visibility.Collapsed;
        _tokenPanel.Visibility = Visibility.Visible;
        _instructions.Text = "先登录森空岛网页版，再打开账号信息页。\n复制 data.content 的内容，粘贴到下方。";
        _tokenInput.Focus();
    }

    private void SubmitToken()
    {
        var token = AccountTokenInput.Parse(_tokenInput.Password);
        if (string.IsNullOrWhiteSpace(token))
        {
            _tokenError.Text = "请输入有效的账号 token 内容。";
            return;
        }
        _qrCancellation?.Cancel();
        AccountToken = token;
        _tokenInput.Clear();
        DialogResult = true;
    }

    private static BitmapImage CreateQrImage(string content)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.Q);
        using var png = new PngByteQRCode(data);
        using var stream = new MemoryStream(png.GetGraphic(10));
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static Button MakeButton(string text, bool primary = false)
    {
        var button = new Button
        {
            Content = text, FontSize = 11, Padding = new Thickness(10, 7, 10, 7), Cursor = Cursors.Hand,
            Background = Brush(primary ? "#F1F400" : "#303030"),
            Foreground = Brush(primary ? "#242424" : "#D5D5D5"), BorderBrush = Brush("#555555"),
            BorderThickness = new Thickness(primary ? 0 : 1)
        };
        if (Application.Current?.MainWindow?.TryFindResource("QuietButton") is Style style) button.Style = style;
        return button;
    }

    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
}
