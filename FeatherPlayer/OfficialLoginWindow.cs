using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace FeatherPlayer;

// The official website performs QQ/WeChat authorization. We never synthesize p_skey.
public sealed class OfficialLoginWindow : Window
{
    private readonly WebView2 _browser = new();
    private readonly TextBlock _status = new() { Text = "正在打开 QQ 音乐官方网站…", Margin = new Thickness(12), TextWrapping = TextWrapping.Wrap };
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly CancellationTokenSource _closed = new();
    private bool _checking, _isClosed;
    private string _rejectedKey = "";
    private long _retryAfter;
    private readonly MusicProvider _provider;
    private readonly string _website;
    public AccountSession? Session { get; private set; }

    public OfficialLoginWindow(MusicProvider provider = MusicProvider.Qq)
    {
        _provider = provider;
        _website = provider == MusicProvider.Netease ? "https://music.163.com/" : "https://y.qq.com/";
        string name = provider == MusicProvider.Netease ? "网易云音乐" : "QQ 音乐";
        _status.Text = "正在打开" + name + "官方网站…";
        Title = name + "官方网页登录"; Width = 1000; Height = 740; MinWidth = 700; MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterScreen; ShowInTaskbar = false;
        Background = Brushes.White; Foreground = Brushes.Black;
        var root = new DockPanel();
        var bar = new DockPanel { Background = Brushes.WhiteSmoke };
        var check = new Button { Content = "我已登录，检查账号", Margin = new Thickness(6), Padding = new Thickness(12, 6, 12, 6) };
        check.Click += async (_, _) => { _rejectedKey = ""; _retryAfter=0; await CheckAsync(); };
        DockPanel.SetDock(check, Dock.Right); bar.Children.Add(check); bar.Children.Add(_status);
        DockPanel.SetDock(bar, Dock.Top); root.Children.Add(bar); root.Children.Add(_browser); Content = root;
        Loaded += async (_, _) =>
        {
            try
            {
                string profile = _provider == MusicProvider.Netease ? "NeteaseWebLogin" : "OfficialWebLogin";
                var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(AppPaths.DataDirectory, profile));
                if (_isClosed) return;
                await _browser.EnsureCoreWebView2Async(environment);
                if (_isClosed) return;
                _browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
                _browser.CoreWebView2.NewWindowRequested += (_, args) =>
                {
                    args.Handled = true;
                    if (OfficialUri(args.Uri)) _browser.CoreWebView2.Navigate(args.Uri);
                };
                _browser.CoreWebView2.NavigationStarting += (_, args) => { if (!OfficialUri(args.Uri)) args.Cancel = true; };
                _browser.CoreWebView2.NavigationCompleted += (_, args) =>
                {
                    if (!args.IsSuccess) _status.Text = "官方网站加载失败：" + args.WebErrorStatus + "。可关闭后重新打开。";
                };
                _browser.CoreWebView2.Navigate(_provider == MusicProvider.Netease ? _website : "https://y.qq.com/portal/profile.html");
                _status.Text = _provider == MusicProvider.Netease
                    ? "请在网易云官方页面点击“登录”，扫码或使用官网提供的方式登录，成功后自动同步。"
                    : "请在官方页面点击“登录”，选择 QQ 或微信并完成扫码；成功后自动同步。";
                _poll.Tick += async (_, _) => await CheckAsync(); _poll.Start();
            }
            catch (Exception ex) { if (!_isClosed) _status.Text = "无法启动官方登录页面（" + ex.GetType().Name + "）。需要 Microsoft Edge WebView2 Runtime。"; }
        };
        Closed += (_, _) => { _isClosed = true; _poll.Stop(); _closed.Cancel(); _browser.Dispose(); };
    }

    private async System.Threading.Tasks.Task CheckAsync()
    {
        if (_checking || _isClosed || _browser.CoreWebView2 == null || Environment.TickCount64<_retryAfter) return;
        _checking = true;
        try
        {
            var cookies = await _browser.CoreWebView2.CookieManager.GetCookiesAsync(_website);
            if (_isClosed) return;
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var cookie in cookies) if (!string.IsNullOrEmpty(cookie.Value)) values[cookie.Name] = cookie.Value;
            if (_provider == MusicProvider.Netease)
            {
                if (!values.TryGetValue("MUSIC_U", out string? musicKey) || musicKey.Length == 0) return;
                var allowed = new HashSet<string> { "MUSIC_U", "__csrf", "__remember_me", "NMTID" };
                string cookieHeader = string.Join("; ", values.Where(p => allowed.Contains(p.Key)).Select(p => p.Key + "=" + p.Value));
                using var netease = new NeteaseMusicClient();
                netease.SetSession(new AccountSession("0", musicKey, "", cookieHeader, Provider: MusicProvider.Netease));
                _status.Text = "正在验证网易云账号…";
                AcceptSession(await netease.GetAccountAsync(_closed.Token));
                return;
            }
            string Read(params string[] names) => names.Select(n => values.GetValueOrDefault(n, "")).FirstOrDefault(v => v.Length > 0) ?? "";
            string key = Read("qm_keyst", "qqmusic_key"), uin = Read("qqmusic_uin", "uin", "wxuin").TrimStart('o');
            if (key.Length == 0 || !long.TryParse(uin, out long id) || id <= 0 || _rejectedKey == key) return;
            uin = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            int loginType = int.TryParse(Read("tmeLoginType"), out int type) && type > 0 ? type : key.StartsWith("W_X", StringComparison.Ordinal) ? 1 : 2;
            var names = new HashSet<string>(new[] { "qqmusic_uin", "qqmusic_key", "qm_keyst", "uin", "wxuin", "wxopenid", "tmeLoginType", "psrf_qqaccess_token", "psrf_qqopenid", "psrf_qqunionid", "psrf_access_token_expiresAt", "login_type" });
            string header = string.Join("; ", values.Where(pair => names.Contains(pair.Key)).Select(pair => pair.Key + "=" + pair.Value));
            var candidate = new AccountSession(uin, key, "", header, LoginType: loginType, QqUin: QQMusicClient.NormalizeAccountId(Read("uin")));
            _status.Text = "官网已返回登录状态，正在验证账号…";
            using var verify = new QQMusicClient(); verify.SetSession(candidate);
            var verified = await verify.GetAccountAsync(_closed.Token);
            AcceptSession(verified);
        }
        catch (OperationCanceledException) { }
        catch (NeteaseMusicException ex)
        {
            _retryAfter = Environment.TickCount64 + 8000;
            if (!_isClosed) _status.Text = "网易云账号尚未验证成功，稍后自动重试。" + ex.Message;
        }
        catch (QQMusicException ex) { _rejectedKey = ""; _retryAfter=Environment.TickCount64+8000; if (!_isClosed) { _status.Text = "音乐账号尚未验证成功，稍后自动重试：" + ex.Message; try { File.WriteAllText(Path.Combine(AppPaths.DataDirectory, "login-result.json"), System.Text.Json.JsonSerializer.Serialize(new { verified = false, message = ex.Message, code = ex.Code, time = DateTimeOffset.UtcNow })); } catch { } } }
        catch { _retryAfter=Environment.TickCount64+8000; if (!_isClosed) { _status.Text = "账号同步暂未完成，稍后自动重试；也可点击“我已登录，检查账号”。"; } }
        finally { _checking = false; }
    }

    private void AcceptSession(AccountSession session)
    {
        if (_isClosed) return;
        Session = session;
        try { SessionStore.Save(session); } catch { }
        try { File.WriteAllText(Path.Combine(AppPaths.DataDirectory, "login-result.json"), System.Text.Json.JsonSerializer.Serialize(new { verified = true, provider = _provider.ToString(), method = "official-web", time = DateTimeOffset.UtcNow })); } catch { }
        DialogResult = true;
    }

    private bool OfficialUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https") return false;
        if (_provider == MusicProvider.Netease)
            return uri.Host.Equals("163.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".163.com", StringComparison.OrdinalIgnoreCase);
        return uri.Host.Equals("qq.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".qq.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".weixin.qq.com", StringComparison.OrdinalIgnoreCase);
    }
}
