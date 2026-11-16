using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QRCoder;

namespace FeatherPlayer;

internal sealed class KugouLoginWindow : Window
{
    private readonly KugouMusicClient _client = new();
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _request;
    private readonly Image _image = new() { Width = 224, Height = 224 };
    private readonly TextBlock _status = new() { Text = "正在生成二维码…", TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 18, 0, 12) };
    private readonly Button _refresh = new() { Content = "刷新二维码", Padding = new Thickness(16, 8, 16, 8), Margin = new Thickness(0, 8, 0, 0) };
    private int _pending;
    private bool _disposed;
    public AccountSession? Session { get; private set; }

    internal KugouLoginWindow()
    {
        Title = "扫码登录酷狗音乐";
        Width = 380; Height = 470; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;
        FontFamily = new FontFamily("Microsoft YaHei UI");
        Background = new SolidColorBrush(SystemTheme.Tone(SystemTheme.MenuDark ? 13u : 98u, 8));
        Foreground = SystemTheme.MenuInk;
        var panel = new StackPanel { Margin = new Thickness(30, 24, 30, 20) };
        panel.Children.Add(new TextBlock { Text = "登录酷狗音乐", FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
        panel.Children.Add(new TextBlock { Text = "请使用酷狗音乐 App 扫码并确认登录", Foreground = SystemTheme.MenuSecondary, Margin = new Thickness(0, 0, 0, 18) });
        panel.Children.Add(new Border { Child = _image, Background = Brushes.White, Padding = new Thickness(8), CornerRadius = new CornerRadius(8), HorizontalAlignment = HorizontalAlignment.Center });
        panel.Children.Add(_status);
        panel.Children.Add(_refresh);
        Content = panel;
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.NearestNeighbor);
        _refresh.Click += async (_, _) => await BeginAsync();
        Loaded += async (_, _) => await BeginAsync();
        Closed += (_, _) => { _lifetime.Cancel(); _request?.Cancel(); ReleaseIfClosed(); };
    }

    private void ReleaseIfClosed()
    {
        if (!_disposed && _lifetime.IsCancellationRequested && _pending == 0)
        {
            _disposed = true;
            _client.Dispose();
            _lifetime.Dispose();
        }
    }

    private async Task BeginAsync()
    {
        _request?.Cancel();
        _pending++;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        request.CancelAfter(TimeSpan.FromMinutes(4));
        _request = request;
        _refresh.IsEnabled = false;
        _image.Source = null;
        _status.Text = "正在生成二维码…";
        try
        {
            string key = await _client.CreateQrAsync(request.Token);
            using var generator = new QRCodeGenerator();
            using var code = generator.CreateQrCode("https://h5.kugou.com/apps/loginQRCode/html/index.html?qrcode=" + Uri.EscapeDataString(key), QRCodeGenerator.ECCLevel.Q);
            using var png = new PngByteQRCode(code);
            using var stream = new MemoryStream(png.GetGraphic(8));
            var bitmap = new BitmapImage();
            bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
            request.Token.ThrowIfCancellationRequested();
            _image.Source = bitmap;
            _refresh.IsEnabled = true;
            _status.Text = "等待酷狗音乐 App 扫码…";
            while (true)
            {
                await Task.Delay(2000, request.Token);
                var result = await _client.CheckQrAsync(key, request.Token);
                request.Token.ThrowIfCancellationRequested();
                if (result.Status == 0) { _status.Text = "二维码已过期，请刷新。"; return; }
                if (result.Session != null)
                {
                    _status.Text = "正在验证账号…";
                    _client.SetSession(result.Session);
                    var account = await _client.GetAccountAsync(request.Token);
                    request.Token.ThrowIfCancellationRequested();
                    SessionStore.Save(account);
                    Session = account;
                    DialogResult = true;
                    return;
                }
                _status.Text = result.Status == 2 ? "已扫码，请在手机上确认登录。" : "等待酷狗音乐 App 扫码…";
            }
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_request, request) && !_lifetime.IsCancellationRequested) _status.Text = "二维码已过期，请刷新。";
        }
        catch (Exception error)
        {
            if (ReferenceEquals(_request, request) && !_lifetime.IsCancellationRequested)
                _status.Text = error is KugouMusicException ? error.Message : "酷狗登录暂未完成，请刷新重试。";
        }
        finally
        {
            if (ReferenceEquals(_request, request))
            {
                _request = null;
                if (!_lifetime.IsCancellationRequested) _refresh.IsEnabled = true;
            }
            _pending--;
            ReleaseIfClosed();
        }
    }
}
