using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FeatherPlayer;

public sealed class QrWindow : Window
{
    private readonly QQMusicClient _api;
    private readonly Image _image = new() { Width = 232, Height = 232, Stretch = Stretch.Uniform };
    private readonly TextBlock _status = new() { Text = "正在获取二维码…", TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 18, 0, 14) };
    private readonly Button _refresh = new() { Content = "刷新二维码", Margin = new Thickness(0, 0, 0, 12) };
    private CancellationTokenSource? _flow;
    public AccountSession? Session { get; private set; }

    public QrWindow(QQMusicClient api)
    {
        _api = api;
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.NearestNeighbor);
        Title = "扫码登录 QQ 音乐"; Width = 360; Height = 570;
        WindowStyle = WindowStyle.SingleBorderWindow; Topmost = true;
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.Manual;
        Loaded += (_, _) =>
        {
            var position = System.Windows.Forms.Cursor.Position;
            var area = System.Windows.Forms.Screen.FromPoint(position).WorkingArea;
            double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            Left = Math.Clamp(position.X / scale - Width / 2, area.Left / scale, Math.Max(area.Left / scale, area.Right / scale - Width));
            Top = Math.Max(area.Top / scale, area.Bottom / scale - Height - 8);
        };
        ShowInTaskbar = false;
        Background = (Brush)Application.Current.FindResource("Bg");
        Foreground = (Brush)Application.Current.FindResource("Text");
        FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
        var body = new StackPanel { Margin = new Thickness(32, 25, 32, 16) };
        var back = new Button { Content = "← 返回", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 0, 10) };
        back.Click += (_, _) => Close(); body.Children.Add(back);
        body.Children.Add(new TextBlock { Text = "找回你的音乐", FontSize = 23, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 9) });
        body.Children.Add(new TextBlock { Text = "使用手机 QQ 扫码，并在手机上确认登录", FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(160, 178, 165)), TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 0, 0, 20) });
        body.Children.Add(new Border { Child = _image, Background = Brushes.White, Padding = new Thickness(8), HorizontalAlignment = HorizontalAlignment.Center, CornerRadius = new CornerRadius(9) });
        body.Children.Add(_status); body.Children.Add(_refresh);
        body.Children.Add(new TextBlock { Text = "登录凭据仅加密保存在本机，可随时退出登录。", FontSize = 10, TextAlignment = TextAlignment.Center, Foreground = Brushes.Gray });
        Content = body;
        _refresh.Click += async (_, _) => await BeginAsync();
        Loaded += async (_, _) => await BeginAsync();
        Closed += (_, _) => { _flow?.Cancel(); _flow?.Dispose(); };
    }

    private async Task BeginAsync()
    {
        _flow?.Cancel(); _flow?.Dispose();
        var source = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        _flow = source;
        _status.Text = "正在获取二维码…"; _refresh.IsEnabled = false; _image.Source = null;
        try
        {
            var qr = await _api.CreateQrAsync(source.Token);
            var bitmap = new BitmapImage();
            using (var stream = new MemoryStream(qr.Image))
            {
                bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
            }
            source.Token.ThrowIfCancellationRequested();
            _image.Source = bitmap; _refresh.IsEnabled = true; _status.Text = "等待手机 QQ 扫码";
            int failures = 0;
            while (!source.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), source.Token);
                QrLoginResult result;
                try { result = await _api.CheckQrAsync(qr, source.Token); failures = 0; }
                catch (QQMusicException ex) when (ex.IsTransient && failures++ < 2)
                {
                    if (source.IsCancellationRequested || !ReferenceEquals(_flow, source) || !IsVisible) return;
                    _status.Text = ex.Message + " 正在重试（" + failures + "/3）…";
                    continue;
                }
                if (source.IsCancellationRequested || !ReferenceEquals(_flow, source) || !IsVisible) return;
                _status.Text = result.Message;
                if (result.Session != null)
                {
                    Session = result.Session;
                    DialogResult = true;
                    return;
                }
                if (result.State is "expired" or "denied" or "rejected" or "error" or "cancelled") return;
            }
        }
        catch (OperationCanceledException) { if (ReferenceEquals(_flow, source) && IsVisible) _status.Text = "二维码已过期，请刷新。"; }
        catch (QQMusicException ex)
        {
            if (ReferenceEquals(_flow, source) && IsVisible)
            {
                _status.Text = ex.Message + (ex.Code == 0 ? "" : "（错误码 " + ex.Code + "）");
                try { Directory.CreateDirectory(AppPaths.DataDirectory); File.WriteAllText(Path.Combine(AppPaths.DataDirectory, "last-login-error.json"), System.Text.Json.JsonSerializer.Serialize(new { message = ex.Message, code = ex.Code, transient = ex.IsTransient, time = DateTimeOffset.UtcNow })); } catch { }
            }
        }
        catch { if (ReferenceEquals(_flow, source) && IsVisible) _status.Text = "登录响应处理失败，请刷新后重试。"; }
        finally { if (ReferenceEquals(_flow, source) && IsVisible) _refresh.IsEnabled = true; }
    }
}
