using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace FeatherPlayer;

public sealed class QQMusicException : Exception
{
    public int Code { get; }
    public bool IsTransient { get; }
    public QQMusicException(string message, int code = 0, bool isTransient = false) : base(message) { Code = code; IsTransient = isTransient; }
}

/// <summary>
/// Direct, read-only QQ Music integration. Protocol envelopes are authored here independently;
/// no public relay, browser engine, password collection, or third-party runtime is required.
/// </summary>
public sealed partial class QQMusicClient : IMusicClient
{
    private const string Gateway = "https://u.y.qq.com/cgi-bin/musicu.fcg";
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120.0.0.0 Safari/537.36";
    private const int PageSize = 200;
    private const int MaxSongPages = 100;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly object _sessionSync = new();
    private AccountSession? _session;
    public AccountSession? Session => Volatile.Read(ref _session);
    public AudioQuality Quality { get; set; } = AudioQuality.Auto;

    public QQMusicClient() : this(null) { }
    internal QQMusicClient(HttpMessageHandler? handler)
    {
        _http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(12),
            MaxConnectionsPerServer = 4
        }) { Timeout = TimeSpan.FromSeconds(25), MaxResponseContentBufferSize = 12 * 1024 * 1024 };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
    }

    public void SetSession(AccountSession? session)
    {
        if (session != null && session.Provider != MusicProvider.Qq)
            throw new QQMusicException("请选择 QQ 音乐账号登录。");
        if (session is not null && (!Regex.IsMatch(session.Uin, @"^\d{1,20}$") || string.IsNullOrWhiteSpace(session.MusicKey)))
            throw new QQMusicException("登录凭证不完整，请重新扫码。");
        lock (_sessionSync) Volatile.Write(ref _session, session);
    }

    public async Task<QrLogin> CreateQrAsync(CancellationToken cancellationToken)
    {
        string url = Query("https://ssl.ptlogin2.qq.com/ptqrshow", new()
        {
            ["appid"] = "716027609", ["e"] = "2", ["l"] = "M", ["s"] = "3", ["d"] = "72", ["v"] = "4",
            ["t"] = Random.Shared.NextDouble().ToString(CultureInfo.InvariantCulture), ["daid"] = "383", ["pt_3rd_aid"] = "100497308"
        });
        using var request = Request(HttpMethod.Get, url, null, "https://xui.ptlogin2.qq.com/");
        using HttpResponseMessage response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        Dictionary<string, string> cookies = ReadCookies(response);
        if (!cookies.TryGetValue("qrsig", out string? signature) || string.IsNullOrWhiteSpace(signature))
            throw new QQMusicException("QQ 未返回有效二维码签名，请稍后重试。");
        byte[] image = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (image.Length is < 50 or > 1_048_576) throw new QQMusicException("QQ 返回的二维码图片无效。");
        return new QrLogin(image, signature, DateTimeOffset.UtcNow, CookieHeader(cookies));
    }

    public async Task<QrLoginResult> CheckQrAsync(QrLogin qr, CancellationToken cancellationToken)
    {
        if (DateTimeOffset.UtcNow - qr.CreatedAt > TimeSpan.FromMinutes(3))
            return new("expired", "二维码已过期，请刷新。");
        string url = Query("https://ssl.ptlogin2.qq.com/ptqrlogin", new()
        {
            ["u1"] = "https://graph.qq.com/oauth2.0/login_jump", ["ptqrtoken"] = Hash33(qr.Signature).ToString(CultureInfo.InvariantCulture),
            ["ptredirect"] = "0", ["h"] = "1", ["t"] = "1", ["g"] = "1", ["from_ui"] = "1", ["ptlang"] = "2052",
            ["action"] = "0-0-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
            ["js_ver"] = "20102616", ["js_type"] = "1", ["pt_uistyle"] = "40", ["aid"] = "716027609", ["daid"] = "383",
            ["pt_3rd_aid"] = "100497308", ["has_onekey"] = "1"
        });
        using var request = Request(HttpMethod.Get, url, qr.Cookie.Length > 0 ? qr.Cookie : "qrsig=" + qr.Signature, "https://xui.ptlogin2.qq.com/");
        using HttpResponseMessage response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        string raw = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        Match callback = Regex.Match(raw, @"ptuiCB\((.*?)\)", RegexOptions.Singleline);
        MatchCollection values = Regex.Matches(callback.Success ? callback.Groups[1].Value : "", "'((?:\\\\.|[^'])*)'");
        if (values.Count < 1) throw new QQMusicException("无法识别 QQ 扫码状态，请刷新二维码。");
        string state = values[0].Groups[1].Value;
        switch (state)
        {
            case "66": return new("waiting", "请使用手机 QQ 扫码。");
            case "67": return new("scanned", "已扫码，请在手机 QQ 确认登录。");
            case "65": return new("expired", "二维码已过期，请刷新。");
            case "68": return new("cancelled", "你已取消登录，可刷新二维码重试。");
            case "0": break;
            default: throw new QQMusicException("QQ 暂未允许本次登录，请稍后重试。", int.TryParse(state, out int n) ? n : 0);
        }
        if (values.Count < 3) throw new QQMusicException("QQ 登录授权响应不完整，请重新扫码。");
        string jump = WebUtility.HtmlDecode(values[2].Groups[1].Value.Replace("\\/", "/"));
        if (!Uri.TryCreate(jump, UriKind.Absolute, out Uri? jumpUri) || !IsQqHost(jumpUri.Host))
            throw new QQMusicException("QQ 登录返回了无法识别的授权地址。");
        var args = QueryArguments(jumpUri.Query);
        if (!args.TryGetValue("uin", out string? uin) || !args.TryGetValue("ptsigx", out string? sigx) || !Regex.IsMatch(uin, @"^\d+$"))
            throw new QQMusicException("QQ 登录授权参数缺失，请重新扫码。");
        if (jumpUri.Scheme != Uri.UriSchemeHttps) jumpUri = new UriBuilder(jumpUri) { Scheme = "https", Port = -1 }.Uri;
        var loginCookies = ReadCookies(response);
        foreach (string entry in qr.Cookie.Split(';'))
        {
            int equals = entry.IndexOf('=');
            if (equals > 0) loginCookies.TryAdd(entry[..equals].Trim(), entry[(equals + 1)..].Trim());
        }
        loginCookies["qrsig"] = qr.Signature;
        AccountSession session = await AuthorizeAsync(jumpUri, loginCookies, cancellationToken).ConfigureAwait(false);
        return new("success", "QQ 授权成功，正在读取账号。", session);
    }

    private async Task<AccountSession> AuthorizeAsync(Uri callback, Dictionary<string, string> loginCookies, CancellationToken cancellationToken)
    {
        // The callback carries server-selected host and parameters. Reconstructing it can
        // discard fields required for issuing graph.qq.com's p_skey.
        var cookies = new Dictionary<string, string>(loginCookies, StringComparer.Ordinal);
        var trace = new List<object>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        Uri target = callback;
        for (int hop = 0; hop < 6; hop++)
        {
            if (!IsQqHost(target.Host) || target.Scheme != Uri.UriSchemeHttps || !visited.Add(target.AbsoluteUri)) break;
            using var signatureRequest = Request(HttpMethod.Get, target.AbsoluteUri, CookieHeader(cookies), "https://xui.ptlogin2.qq.com/");
            using var signatureResponse = await LoginStepAsync("QQ 票据交换", () => SendAsync(signatureRequest, cancellationToken, allowRedirect: true)).ConfigureAwait(false);
            var received = ReadCookies(signatureResponse);
            foreach (var pair in received) if (pair.Value.Length > 0) cookies[pair.Key] = pair.Value;
            trace.Add(new { hop, status = (int)signatureResponse.StatusCode, cookieNames = received.Keys.ToArray() });
            if (cookies.TryGetValue("p_skey", out string? ticket) && ticket.Length > 0) break;
            var redirect = signatureResponse.Headers.Location;
            if (redirect == null || !Uri.TryCreate(target, redirect, out Uri? next) || !IsQqHost(next.Host)) break;
            target = next.Scheme == "http" ? new UriBuilder(next) { Scheme = "https", Port = -1 }.Uri : next;
        }
        if (!cookies.TryGetValue("p_skey", out string? pSkey) || pSkey.Length == 0)
        {
            try { System.IO.Directory.CreateDirectory(AppPaths.DataDirectory); System.IO.File.WriteAllText(System.IO.Path.Combine(AppPaths.DataDirectory, "login-ticket-diagnostic.json"), JsonSerializer.Serialize(trace)); } catch { }
            throw new QQMusicException("QQ 授权跳转完成后仍未返回音乐票据（已检查 " + trace.Count + " 次响应），请重新扫码。");
        }
        using var authorization = Request(HttpMethod.Post, "https://graph.qq.com/oauth2.0/authorize", CookieHeader(cookies), "https://graph.qq.com/");
        authorization.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["response_type"] = "code", ["client_id"] = "100497308",
            ["redirect_uri"] = "https://y.qq.com/portal/wx_redirect.html?login_type=1&surl=https://y.qq.com/",
            ["scope"] = "get_user_info,get_app_friends", ["state"] = "state", ["switch"] = "", ["from_ptlogin"] = "1", ["src"] = "1",
            ["update_auth"] = "1", ["openapi"] = "1010_1030", ["g_tk"] = Hash33(pSkey, 5381).ToString(CultureInfo.InvariantCulture),
            ["auth_time"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture), ["ui"] = Guid.NewGuid().ToString()
        });
        using HttpResponseMessage authResponse = await LoginStepAsync("QQ 音乐 OAuth 授权", () => SendAsync(authorization, cancellationToken, allowRedirect: true)).ConfigureAwait(false);
        Uri? location = authResponse.Headers.Location;
        if (location is null || !location.IsAbsoluteUri || !IsQqHost(location.Host))
            throw new QQMusicException("QQ 音乐授权未完成，请重新扫码并确认授权。");
        var parameters = QueryArguments(location.Query);
        if (!parameters.TryGetValue("code", out string? code) || string.IsNullOrWhiteSpace(code))
            throw new QQMusicException("QQ 音乐未返回授权码，请重新扫码。");
        JsonElement data = await LoginStepAsync("音乐账号凭据交换", () => RpcAsync("QQConnectLogin.LoginServer", "QQLogin", new { code }, null, cancellationToken,
            new Dictionary<string, object?> { ["tmeLoginType"] = 2 })).ConfigureAwait(false);
        return ParseSession(data);
    }

    private static async Task<T> LoginStepAsync<T>(string step, Func<Task<T>> action)
    {
        try { return await action().ConfigureAwait(false); }
        catch (QQMusicException ex) { throw new QQMusicException(step + "：" + ex.Message, ex.Code, ex.IsTransient); }
    }

    public async Task<AccountSession> GetAccountAsync(CancellationToken cancellationToken)
    {
        AccountSession session = await ReadySessionAsync(cancellationToken).ConfigureAwait(false);
        JsonElement data;
        try { data = await RpcAsync("music.UserInfo.userInfoServer", "GetLoginUserInfo", new { }, session, cancellationToken).ConfigureAwait(false); }
        catch (QQMusicException) { data = default; } // The official web login can be validated by its authenticated web profile below.
        RequireCode(At(data, "info"), "验证登录");
        string returnedUin = FindText(data, "musicid", "str_musicid");
        if (NormalizeAccountId(returnedUin).Length > 0 && NormalizeAccountId(returnedUin) != NormalizeAccountId(session.Uin))
            throw new QQMusicException("返回的账号与当前登录不一致，请重新扫码。");
        string nickname = FindText(data, "nick", "nickname", "nickName", "name");
        bool identityConfirmed = NormalizeAccountId(returnedUin).Length > 0 && NormalizeAccountId(returnedUin) == NormalizeAccountId(session.Uin) || nickname.Length > 0;
        string euin = FindText(data, "encryptUin", "encrypt_uin", "euin", "encrypted_uin");
        AccountSession verified = session with
        {
            Nickname = nickname.Length > 0 ? WebUtility.HtmlDecode(nickname) : session.Nickname,
            EncryptUin = euin.Length > 0 ? euin : session.EncryptUin
        };
        // The authenticated homepage also exposes the encrypted UIN required by private favorites.
        if (verified.EncryptUin.Length == 0)
        {
            string url = Query("https://c6.y.qq.com/rsc/fcgi-bin/fcg_get_profile_homepage.fcg", new()
            {
                ["g_tk"] = Hash33(session.MusicKey, 5381).ToString(CultureInfo.InvariantCulture), ["format"] = "json", ["inCharset"] = "utf-8",
                ["outCharset"] = "utf-8", ["notice"] = "0", ["cid"] = "205360838", ["needNewCode"] = "0", ["loginUin"] = session.Uin,
                ["hostUin"] = "0", ["userid"] = session.Uin, ["reqfrom"] = "1"
            });
            JsonElement profile = await GetJsonAsync(url, SessionCookie(session), cancellationToken).ConfigureAwait(false);
            RequireCode(profile, "读取账号");
            JsonElement creator = At(profile, "data", "creator");
            string creatorUin = Text(creator, "musicid");
            string creatorQqUin = NormalizeAccountId(Text(creator, "uin", "hostuin"));
            if (NormalizeAccountId(creatorUin).Length > 0 && NormalizeAccountId(creatorUin) != NormalizeAccountId(session.Uin))
                throw new QQMusicException("返回的账号与当前登录不一致，请重新扫码。");
            if (creatorQqUin.Length > 0 && NormalizeAccountId(session.QqUin).Length > 0 && creatorQqUin != NormalizeAccountId(session.QqUin))
                throw new QQMusicException("官网账号标识与当前 QQ 登录不一致，请切换到同一账号。");
            euin = Text(creator, "encrypt_uin", "encryptUin", "euin");
            nickname = Text(creator, "nick", "nickname");
            identityConfirmed |= NormalizeAccountId(creatorUin).Length > 0 && NormalizeAccountId(creatorUin) == NormalizeAccountId(session.Uin) || nickname.Length > 0;
            verified = verified with
            {
                EncryptUin = euin.Length > 0 ? euin : verified.EncryptUin,
                Nickname = nickname.Length > 0 ? WebUtility.HtmlDecode(nickname) : verified.Nickname
            };
        }
        if (!identityConfirmed) throw new QQMusicException("QQ 音乐没有返回已验证的账号信息，请重新扫码授权。");
        lock (_sessionSync)
        {
            if (!ReferenceEquals(Session, session)) throw new OperationCanceledException("账号已经变化，已忽略旧账号请求。");
            SetSession(verified);
        }
        return verified;
    }

    public async Task<IReadOnlyList<Playlist>> GetPlaylistsAsync(CancellationToken cancellationToken)
    {
        AccountSession session = await ReadySessionAsync(cancellationToken).ConfigureAwait(false);
        if (session.EncryptUin.Length == 0)
        {
            session = await GetAccountAsync(cancellationToken).ConfigureAwait(false);
            if (session.EncryptUin.Length == 0) throw new QQMusicException("QQ 未返回收藏列表所需的账号标识，请重新登录。");
        }
        var result = new List<Playlist> { new("liked", "我喜欢", IsLiked: true) };
        var ids = new HashSet<string>(StringComparer.Ordinal) { "liked" };
        JsonElement created = await RpcAsync("music.musicasset.PlaylistBaseRead", "GetPlaylistByUin", new { uin = session.Uin }, session, cancellationToken).ConfigureAwait(false);
        foreach (JsonElement item in Items(At(created, "v_playlist")))
        {
            int dirid = Number(item, "dirid", "dirId");
            if (dirid == 201)
            {
                result[0] = result[0] with { Count = Number(item, "songnum", "songNum", "song_cnt") };
                continue;
            }
            string id = Text(item, "tid", "id", "dissid");
            if (id is "" or "0") id = dirid > 0 ? "dir:" + dirid.ToString(CultureInfo.InvariantCulture) : "";
            AddPlaylist(result, ids, item, id, true);
        }
        if (At(created, "bFinish").ValueKind == JsonValueKind.False || Text(created, "bFinish") == "0")
            throw new QQMusicException("QQ 返回的自建歌单尚未完整；本次未将部分结果显示为全部，请稍后刷新。");
        var pageSignatures = new HashSet<string>(StringComparer.Ordinal);
        for (int page = 0; page < 100; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            JsonElement favorites = await RpcAsync("music.musicasset.PlaylistFavRead", "CgiGetPlaylistFavInfo",
                new { uin = session.EncryptUin, offset = page * PageSize, size = PageSize }, session, cancellationToken).ConfigureAwait(false);
            if (Number(favorites, "hide") != 0 || At(favorites, "hide").ValueKind == JsonValueKind.True)
                throw new QQMusicException("QQ 返回的收藏歌单不可见，请检查账号授权。");
            JsonElement[] entries = Items(At(favorites, "v_list")).ToArray();
            foreach (JsonElement item in entries) AddPlaylist(result, ids, item, Text(item, "tid", "id", "dissid"));
            bool hasMore = Number(favorites, "hasmore") != 0;
            if (!hasMore) { EnsureCurrent(session); return result; }
            string signature = string.Join(",", entries.Select(e => Text(e, "tid", "id", "dissid")));
            if (entries.Length == 0 || !pageSignatures.Add(signature))
                throw new QQMusicException("QQ 收藏歌单分页没有继续返回数据，请稍后刷新；未静默截断。");
            await Task.Delay(80, cancellationToken).ConfigureAwait(false);
        }
        throw new QQMusicException("收藏歌单超过本版 20,000 个读取上限；请缩小范围，未静默截断。");
    }

    public async Task<IReadOnlyList<Song>> GetPlaylistSongsAsync(Playlist playlist, CancellationToken cancellationToken)
    {
        AccountSession? session = Session;
        if (session is not null) session = await ReadySessionAsync(cancellationToken).ConfigureAwait(false);
        bool personalDirectory = playlist.IsLiked || playlist.Id.StartsWith("dir:", StringComparison.Ordinal);
        if (personalDirectory && session is null) throw new QQMusicException("读取个人歌单前请先登录 QQ 音乐。");
        if (personalDirectory && session!.EncryptUin.Length == 0) session = await GetAccountAsync(cancellationToken).ConfigureAwait(false);
        long disstid = 0;
        int dirid = playlist.IsLiked ? 201 : 0;
        if (playlist.Id.StartsWith("dir:", StringComparison.Ordinal))
        {
            if (!int.TryParse(playlist.Id.AsSpan(4), out dirid) || dirid <= 0) throw new QQMusicException("歌单目录编号无效。");
        }
        else if (!playlist.IsLiked && (!long.TryParse(playlist.Id, out disstid) || disstid <= 0))
            throw new QQMusicException("歌单编号无效。");
        var result = new List<Song>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pages = new HashSet<string>(StringComparer.Ordinal);
        int offset = 0;
        for (int page = 0; page < MaxSongPages; page++)
        {
            var param = new Dictionary<string, object?>
            {
                ["disstid"] = disstid, ["dirid"] = dirid, ["tag"] = true, ["song_begin"] = offset, ["song_num"] = PageSize,
                ["userinfo"] = true, ["orderlist"] = true, ["onlysonglist"] = false
            };
            if (personalDirectory) param["enc_host_uin"] = session!.EncryptUin;
            JsonElement data = await RpcAsync("music.srfDissInfo.DissInfo", "CgiGetDiss", param, session, cancellationToken).ConfigureAwait(false);
            int subcode = Number(data, "subcode");
            if (subcode != 0) throw new QQMusicException("QQ 未允许读取这个歌单，请检查歌单是否私密或已经失效。", subcode);
            JsonElement[] entries = Items(At(data, "songlist")).ToArray();
            foreach (JsonElement item in entries)
            {
                Song? song = ParseSong(item);
                if (song is not null && seen.Add(song.Mid)) result.Add(song);
            }
            offset += entries.Length;
            int total = Number(data, "total_song_num", "total");
            bool hasMore = Number(data, "hasmore") != 0 || (total > 0 && offset < total);
            if (!hasMore && (entries.Length < PageSize || total > 0)) { EnsureCurrent(session); return result; }
            if (entries.Length == 0)
            {
                if (hasMore) throw new QQMusicException("QQ 未完整返回歌单曲目，请稍后刷新；未静默截断。");
                EnsureCurrent(session);
                return result;
            }
            string signature = string.Join(",", entries.Select(e => Text(UnwrapSong(e), "mid", "songmid")));
            if (!pages.Add(signature)) throw new QQMusicException("QQ 返回了重复的歌单分页，请稍后刷新；未静默截断。");
            await Task.Delay(80, cancellationToken).ConfigureAwait(false);
        }
        throw new QQMusicException("歌单超过本版 20,000 首读取上限；未静默截断为完整歌单。");
    }

    public async Task<IReadOnlyList<Song>> SearchAsync(string keyword, CancellationToken cancellationToken)
    {
        keyword = keyword.Trim();
        if (keyword.Length == 0) return Array.Empty<Song>();
        if (keyword.Length > 200) throw new QQMusicException("搜索词过长，请缩短到 200 字以内。");
        JsonElement data = default;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                // Desktop comm is important: ct=24 currently returns an empty successful result.
                data = await RpcAsync("music.search.SearchCgiService", "DoSearchForQQMusicDesktop",
                    new { query = keyword, search_type = 0, num_per_page = 60, page_num = 1 }, Session, cancellationToken,
                    new Dictionary<string, object?> { ["ct"] = 19, ["cv"] = 2201 }).ConfigureAwait(false);
                break;
            }
            catch (QQMusicException ex) when (ex.Code == 2001 && attempt < 2)
            {
                await Task.Delay(350 * (attempt + 1), cancellationToken).ConfigureAwait(false);
            }
        }
        return Items(At(data, "body", "song", "list")).Select(ParseSong).Where(s => s is not null).Cast<Song>().ToArray();
    }

    public async Task<string> GetPlaybackUrlAsync(Song song, CancellationToken cancellationToken)
        => (await GetPlaybackSourceAsync(song, cancellationToken).ConfigureAwait(false)).Url;

    public async Task<PlaybackSource> GetPlaybackSourceAsync(Song song, CancellationToken cancellationToken)
    {
        try
        {
            return await ResolvePlaybackAsync(song, cancellationToken).ConfigureAwait(false);
        }
        catch (QQMusicException error) when (error.Code is 104003 or 104009 &&
            Session is { RefreshKey.Length: > 0, RefreshToken.Length: > 0 })
        {
            // Refresh once after server-side expiry, even when the local expiry is unknown.
            var rejected = Session!;
            await ReadySessionAsync(cancellationToken, rejected).ConfigureAwait(false);
            return await ResolvePlaybackAsync(song, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<PlaybackSource> ResolvePlaybackAsync(Song song, CancellationToken cancellationToken)
    {
        if (song.LocalPath is not null) return new(new Uri(song.LocalPath).AbsoluteUri, "本地文件");
        var quality = Quality;
        if (!Regex.IsMatch(song.Mid, @"^[A-Za-z0-9]{10,30}$")) throw new QQMusicException("歌曲编号无效。");
        AccountSession? session = Session;
        if (session is not null) session = await ReadySessionAsync(cancellationToken).ConfigureAwait(false);
        string mediaMid = song.MediaMid;
        if (mediaMid.Length == 0)
        {
            try
            {
            JsonElement detail = await RpcAsync("music.pf_song_detail_svr", "get_song_detail_yqq", new { song_mid = song.Mid }, session, cancellationToken).ConfigureAwait(false);
            mediaMid = Text(At(detail, "track_info", "file"), "media_mid");
            }
            catch(QQMusicException ex) when(ex.Code is 104003 or 104009) { /* The standard MID filename does not require this optional metadata call. */ }
        }
        if (mediaMid.Length == 0) mediaMid = song.Mid + song.Mid;
        string guid = NewGuidNumber();
        string[] formats = quality switch
        {
            AudioQuality.Standard => new[] { "M500.mp3" },
            AudioQuality.High => new[] { "M800.mp3" },
            AudioQuality.Lossless => new[] { "F000.flac" },
            _ => new[] { "M800.mp3", "M500.mp3", "C400.m4a" }
        };
        string[] filenames = formats.Select(format => format.Insert(4, mediaMid)).ToArray();
        int lastCode = 0;
        foreach ((string module, string method,int clientType,bool webIdentity,bool minimal) in new[]
        {
            ("music.vkey.GetEVkey", "CgiGetEVkey",24,false,true),
            ("music.vkey.GetVkey", "UrlGetVkey",24,false,true),
            ("music.vkey.GetVkey", "UrlGetVkey",19,true,false),
            ("vkey.GetVkeyServer", "CgiGetVkey",19,true,false),
        })
        {
            try
            {
                var requestSession=session;
                if(webIdentity && session!=null)
                {
                    string ReadCookie(string name)=>session.Cookie.Split(';').Select(p=>p.Trim()).Where(p=>p.StartsWith(name+"=",StringComparison.Ordinal)).Select(p=>p[(name.Length+1)..]).FirstOrDefault() ?? "";
                    string webUin=NormalizeAccountId(ReadCookie(session.LoginType==1?"wxuin":"uin"));
                    if(webUin.Length==0)webUin=NormalizeAccountId(session.QqUin);
                    string webKey=ReadCookie("qm_keyst");
                    requestSession=session with{Uin=webUin.Length>0?webUin:session.Uin,MusicKey=webKey.Length>0?webKey:session.MusicKey};
                }
                // Newer endpoints accept the documented minimal envelope. Keep legacy
                // web-only fields on the legacy calls instead of mixing client protocols.
                var parameters = new Dictionary<string, object?>
                {
                    ["uin"] = requestSession?.Uin ?? "0", ["guid"] = guid,
                    ["filename"] = filenames, ["songmid"] = filenames.Select(_ => song.Mid).ToArray(),
                    ["songtype"] = new int[filenames.Length], ["ctx"] = 0
                };
                if (!minimal)
                {
                    parameters["loginflag"] = session is null ? 0 : 1;
                    parameters["platform"] = "20";
                    parameters["xcdn"] = 1;
                }
                JsonElement data = await RpcAsync(module, method, parameters,
                    requestSession, cancellationToken, new Dictionary<string, object?>
                    {
                        ["ct"] = clientType, ["cv"] = 0, ["uin"] = requestSession?.Uin ?? "0"
                    }).ConfigureAwait(false);
                JsonElement[] roots = Items(At(data, "sip")).ToArray();
                foreach (JsonElement item in Items(At(data, "midurlinfo")))
                {
                    int result = Number(item, "result");
                    if (result != 0) lastCode = result;
                    string purl = Text(item, "purl");
                    if (purl.Length == 0 || result != 0) continue;
                    if (Uri.TryCreate(purl, UriKind.Absolute, out Uri? absolute) && IsPlaybackUri(absolute)) return PlaybackResult(absolute.AbsoluteUri, session);
                    foreach (JsonElement root in roots)
                    {
                        string cdn = root.ValueKind == JsonValueKind.String ? root.GetString() ?? "" : "";
                        if (!Uri.TryCreate(cdn, UriKind.Absolute, out Uri? cdnUri) || !IsPlaybackUri(cdnUri)) continue;
                        if (Uri.TryCreate(cdnUri, purl, out Uri? playback) && IsPlaybackUri(playback)) return PlaybackResult(playback.AbsoluteUri, session);
                    }
                    if (Uri.TryCreate(new Uri("https://isure.stream.qqmusic.qq.com/"), purl, out Uri? fallback) && IsPlaybackUri(fallback))
                        return PlaybackResult(fallback.AbsoluteUri, session);
                }
            }
            catch (QQMusicException ex) when (ex.Code is 104003 or 104009 or 2001)
            {
                lastCode = ex.Code;
            }
        }
        if(lastCode==104003 && session!=null)throw new QQMusicException("QQ 音乐拒绝了这首歌的播放授权（104003）。已尝试兼容取链；请确认账号的歌曲权限，若官网可播，请重新登录更新播放凭据。",lastCode);
        throw new QQMusicException(session is null
            ? "QQ 音乐未提供这首歌的播放地址，请先登录账号后重试。"
            : "QQ 音乐未提供可播放地址，可能受会员、购买、版权或地区权限限制。", lastCode);
    }

    public async Task<string> GetCoverUrlAsync(Song song, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(song.CoverUrl)) return song.CoverUrl;
        string album = song.AlbumMid;
        if (string.IsNullOrWhiteSpace(album))
        {
            var detail = await RpcAsync("music.pf_song_detail_svr", "get_song_detail_yqq", new { song_mid = song.Mid }, Session, ct).ConfigureAwait(false);
            album = Text(At(detail, "track_info", "album"), "pmid", "mid");
        }
        return Regex.IsMatch(album, "^[A-Za-z0-9]{10,30}$")
            ? "https://y.gtimg.cn/music/photo_new/T002R300x300M000" + album + ".jpg" : "";
    }

    public async Task<IReadOnlyList<LyricLine>> GetLyricsAsync(Song song, CancellationToken cancellationToken)
    {
        if (song.LocalPath is not null || string.IsNullOrEmpty(song.Mid)) return Array.Empty<LyricLine>();
        string url = Query("https://c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg", new()
        {
            ["songmid"] = song.Mid, ["format"] = "json", ["nobase64"] = "1", ["g_tk"] = "5381", ["loginUin"] = "0",
            ["hostUin"] = "0", ["inCharset"] = "utf8", ["outCharset"] = "utf-8", ["notice"] = "0", ["platform"] = "yqq.json", ["needNewCode"] = "0"
        });
        JsonElement data = await GetJsonAsync(url, null, cancellationToken).ConfigureAwait(false);
        RequireCode(data, "读取歌词");
        string lrc = Text(data, "lyric");
        if (lrc.Length == 0) return Array.Empty<LyricLine>();
        if (!lrc.Contains('['))
        {
            try { lrc = Encoding.UTF8.GetString(Convert.FromBase64String(lrc)); }
            catch (FormatException) { return Array.Empty<LyricLine>(); }
        }
        return LyricTimeline.Parse(WebUtility.HtmlDecode(lrc));
    }

    private async Task<AccountSession> ReadySessionAsync(CancellationToken cancellationToken, AccountSession? rejected = null)
    {
        AccountSession session = Session ?? throw new QQMusicException("请先登录 QQ 音乐。");
        if (!ReferenceEquals(session, rejected) && (session.ExpiresAt == 0 || session.ExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 120)) return session;
        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            session = Session ?? throw new QQMusicException("账号已退出，请重新登录。");
            if (!ReferenceEquals(session, rejected) && (session.ExpiresAt == 0 || session.ExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 120)) return session;
            if (session.RefreshKey.Length == 0 || session.RefreshToken.Length == 0)
                throw new QQMusicException("QQ 音乐登录已过期，请重新扫码。");
            JsonElement data = await RpcAsync("music.login.LoginServer", "Login", new
            {
                openid = session.OpenId, access_token = session.AccessToken, refresh_token = session.RefreshToken,
                expired_in = session.ExpiresAt, str_musicid = session.Uin, musicid = long.Parse(session.Uin, CultureInfo.InvariantCulture),
                musickey = session.MusicKey, refresh_key = session.RefreshKey, loginMode = 2
            }, session, cancellationToken).ConfigureAwait(false);
            AccountSession refreshed = ParseSession(data);
            if (refreshed.Uin != session.Uin) throw new QQMusicException("刷新返回的账号不一致，请重新登录。");
            refreshed = refreshed with
            {
                Nickname = refreshed.Nickname.Length > 0 ? refreshed.Nickname : session.Nickname,
                EncryptUin = refreshed.EncryptUin.Length > 0 ? refreshed.EncryptUin : session.EncryptUin,
                QqUin = session.QqUin,
                RefreshKey = refreshed.RefreshKey.Length > 0 ? refreshed.RefreshKey : session.RefreshKey,
                RefreshToken = refreshed.RefreshToken.Length > 0 ? refreshed.RefreshToken : session.RefreshToken,
                OpenId = refreshed.OpenId.Length > 0 ? refreshed.OpenId : session.OpenId,
                AccessToken = refreshed.AccessToken.Length > 0 ? refreshed.AccessToken : session.AccessToken
            };
            lock (_sessionSync)
            {
                if (!ReferenceEquals(Session, session)) throw new OperationCanceledException("账号已经变化，已忽略旧账号刷新。");
                SetSession(refreshed);
                SessionStore.Save(refreshed);
            }
            return refreshed;
        }
        finally { _refreshLock.Release(); }
    }

    private async Task<JsonElement> RpcAsync(string module, string method, object param, AccountSession? session, CancellationToken cancellationToken,
        Dictionary<string, object?>? overrides = null)
    {
        var comm = new Dictionary<string, object?>
        {
            ["ct"] = 24, ["cv"] = 0, ["format"] = "json", ["uin"] = long.TryParse(session?.Uin, out long numericUin) ? numericUin : 0L,
            ["g_tk"] = session is null ? 5381 : Hash33(session.MusicKey, 5381), ["g_tk_new_20200303"] = session is null ? 5381 : Hash33(session.MusicKey, 5381),
            ["inCharset"] = "utf-8", ["outCharset"] = "utf-8", ["notice"] = 0, ["needNewCode"] = 0
        };
        if (session is not null)
        {
            comm["authst"] = session.MusicKey;
            comm["qq"] = session.Uin;
            comm["tmeLoginType"] = session.LoginType;
        }
        if (overrides is not null) foreach (var pair in overrides) comm[pair.Key] = pair.Value;
        var envelope = new Dictionary<string, object?>
        {
            ["comm"] = comm,
            ["req_0"] = new { module, method, param }
        };
        using var request = Request(HttpMethod.Post, Gateway, session is null ? null : SessionCookie(session));
        request.Content = new StringContent(JsonSerializer.Serialize(envelope), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        JsonElement root = ParseJson(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        RequireCode(root, "QQ 音乐请求");
        JsonElement block = At(root, "req_0");
        if (block.ValueKind != JsonValueKind.Object) throw new QQMusicException("QQ 音乐返回的数据格式已变化，请稍后重试。");
        RequireCode(block, "QQ 音乐请求");
        JsonElement data = At(block, "data");
        if (data.ValueKind == JsonValueKind.Undefined) throw new QQMusicException("QQ 音乐未返回所需数据。");
        RequireCode(data, "QQ 音乐请求");
        return data;
    }

    private async Task<JsonElement> GetJsonAsync(string url, string? cookie, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Get, url, cookie);
        using HttpResponseMessage response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        return ParseJson(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken, bool allowRedirect = false)
    {
        try
        {
            HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
            int status = (int)response.StatusCode;
            if (response.IsSuccessStatusCode || allowRedirect && status is >= 300 and <= 399) return response;
            response.Dispose();
            throw new QQMusicException($"QQ 音乐请求失败（HTTP {status}），请稍后重试。", status);
        }
        catch (HttpRequestException)
        {
            // Never expose URLs or request headers: OAuth and signed playback URLs contain secrets.
            throw new QQMusicException("无法连接 QQ 音乐，请检查网络或代理设置。", isTransient: true);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new QQMusicException("QQ 音乐请求超时，请稍后重试。", isTransient: true);
        }
    }

    private static HttpRequestMessage Request(HttpMethod method, string url, string? cookie, string referer = "https://y.qq.com/")
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Referrer = new Uri(referer);
        if (!string.IsNullOrEmpty(cookie)) request.Headers.TryAddWithoutValidation("Cookie", cookie);
        return request;
    }

    private static AccountSession ParseSession(JsonElement data)
    {
        for (int depth = 0; depth < 2 && Text(data, "musickey").Length == 0 && At(data, "data").ValueKind == JsonValueKind.Object; depth++)
        { RequireCode(data, "音乐账号凭据交换"); data = At(data, "data"); RequireCode(data, "音乐账号凭据交换"); }
        string uin = Text(data, "str_musicid", "musicid");
        string key = Text(data, "musickey");
        if (!Regex.IsMatch(uin, @"^\d{1,20}$") || uin.All(c => c == '0') || string.IsNullOrWhiteSpace(key))
            throw new QQMusicException("QQ 音乐没有返回完整登录凭证，请重新扫码。");
        int loginType = Number(data, "loginType");
        if (loginType == 0) loginType = key.StartsWith("W_X", StringComparison.Ordinal) ? 1 : 2;
        long created = Long(data, "musickeyCreateTime");
        long ttl = Long(data, "keyExpiresIn");
        long expires = created > 0 && ttl > 0 ? created + ttl : 0;
        string cookie = $"uin={uin}; qqmusic_uin={uin}; qqmusic_key={key}; qm_keyst={key}; tmeLoginType={loginType}";
        return new AccountSession(uin, key, Text(data, "nick", "nickname", "nickName"), cookie,
            Text(data, "refresh_key"), Text(data, "refresh_token"), expires, Text(data, "openid"), Text(data, "access_token"), Text(data, "encryptUin", "encrypt_uin"), loginType);
    }

    private static string SessionCookie(AccountSession session)
    {
        if (!string.IsNullOrWhiteSpace(session.Cookie) && !session.Cookie.Contains('\r') && !session.Cookie.Contains('\n')) return session.Cookie;
        // Persist only music-scoped cookies; QQ OAuth p_skey never enters the saved session.
        return $"uin={session.Uin}; qqmusic_uin={session.Uin}; qqmusic_key={session.MusicKey}; qm_keyst={session.MusicKey}; tmeLoginType={session.LoginType}";
    }

    private static void AddPlaylist(List<Playlist> result, HashSet<string> ids, JsonElement item, string id, bool owned=false)
    {
        if (id.Length == 0 || id == "0" || !ids.Add(id)) return;
        string name = WebUtility.HtmlDecode(Text(item, "title", "dissname", "diss_name", "name", "dirName"));
        if (name.Length == 0) name = "未命名歌单";
        result.Add(new Playlist(id, name, Number(item, "songnum", "songNum", "song_cnt", "song_count"), Text(item, "picurl", "picUrl", "cover", "logo"), DirectoryId:Number(item,"dirid","dirId"), Writable:owned));
    }

    private static JsonElement UnwrapSong(JsonElement item)
    {
        if (Text(item, "mid", "songmid").Length > 0) return item;
        foreach (string key in new[] { "track_info", "songInfo", "songinfo", "song" })
        {
            JsonElement nested = At(item, key);
            if (nested.ValueKind == JsonValueKind.Object) return nested;
        }
        return item;
    }

    private static Song? ParseSong(JsonElement item)
    {
        item = UnwrapSong(item);
        string mid = Text(item, "mid", "songmid");
        string title = WebUtility.HtmlDecode(Text(item, "title", "name", "songname"));
        if (mid.Length == 0 || title.Length == 0) return null;
        string artist = string.Join(" / ", Items(At(item, "singer")).Select(s => WebUtility.HtmlDecode(Text(s, "name", "title"))).Where(s => s.Length > 0));
        JsonElement album = At(item, "album");
        return new Song(mid, title, artist.Length > 0 ? artist : "未知歌手",
            WebUtility.HtmlDecode(Text(album, "title", "name")) is { Length: > 0 } albumTitle ? albumTitle : Text(item, "albumname"),
            Number(item, "interval", "duration"), Text(album, "mid") is { Length: > 0 } albumMid ? albumMid : Text(item, "albummid"),
            Text(At(item, "file"), "media_mid"), Long(item, "id", "songid"));
    }

    private static void RequireCode(JsonElement data, string operation)
    {
        int code = Number(data, "code");
        if (code == 0) return;
        string message = code switch
        {
            1000 or 104400 or 104401 => "QQ 音乐登录已过期，请重新扫码。",
            20279 => "QQ 音乐登录设备数量已达上限，请在官方客户端管理设备后重试。",
            20277 or 20278 or 20450 => "QQ 音乐账号暂时无法授权，请在官方客户端检查账号状态。",
            104604 or 2001 => "QQ 音乐暂时限制了本次请求，请稍后重试。",
            _ => operation + "未成功（业务码 " + code.ToString(CultureInfo.InvariantCulture) + "）。"
        };
        throw new QQMusicException(message, code);
    }

    private static JsonElement ParseJson(string raw)
    {
        raw = raw.Trim();
        if (!raw.StartsWith('{') && !raw.StartsWith('['))
        {
            int start = raw.IndexOf('('), end = raw.LastIndexOf(')');
            if (start >= 0 && end > start) raw = raw[(start + 1)..end];
        }
        try { using JsonDocument document = JsonDocument.Parse(raw); return document.RootElement.Clone(); }
        catch (JsonException) { throw new QQMusicException("QQ 音乐返回的数据无法解析，请稍后重试。"); }
    }

    private static JsonElement At(JsonElement item, params string[] path)
    {
        foreach (string segment in path)
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(segment, out item)) return default;
        return item;
    }

    private static IEnumerable<JsonElement> Items(JsonElement item) => item.ValueKind == JsonValueKind.Array ? item.EnumerateArray() : Enumerable.Empty<JsonElement>();
    private static string Text(JsonElement item, params string[] names)
    {
        foreach (string name in names)
        {
            JsonElement value = At(item, name);
            string text = value.ValueKind switch { JsonValueKind.String => value.GetString() ?? "", JsonValueKind.Number => value.GetRawText(), _ => "" };
            if (text.Length > 0) return text;
        }
        return "";
    }
    private static long Long(JsonElement item, params string[] names) => long.TryParse(Text(item, names), NumberStyles.Integer, CultureInfo.InvariantCulture, out long n) ? n : 0;
    private static int Number(JsonElement item, params string[] names) => (int)Math.Clamp(Long(item, names), int.MinValue, int.MaxValue);
    private static string FindText(JsonElement item, params string[] names)
    {
        string direct = Text(item, names);
        if (direct.Length > 0) return direct;
        if (item.ValueKind == JsonValueKind.Object)
            foreach (JsonProperty property in item.EnumerateObject())
                if (property.Value.ValueKind == JsonValueKind.Object)
                {
                    string found = FindText(property.Value, names);
                    if (found.Length > 0) return found;
                }
        return "";
    }

    private static int Hash33(string text, int seed = 0)
    {
        uint value = (uint)seed;
        foreach (char ch in text) value = unchecked(value * 33 + ch);
        return (int)(value & 0x7fffffff);
    }
    internal static string NormalizeAccountId(string? value) => ulong.TryParse(value?.Trim().TrimStart('o', 'O'), NumberStyles.None, CultureInfo.InvariantCulture, out ulong id) && id > 0 ? id.ToString(CultureInfo.InvariantCulture) : "";
    private static string NewGuidNumber() => RandomNumberGenerator.GetInt32(100_000_000, int.MaxValue).ToString(CultureInfo.InvariantCulture);
    private static bool IsQqHost(string host) => host.Equals("qq.com", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".qq.com", StringComparison.OrdinalIgnoreCase);
    private static bool IsPlaybackUri(Uri uri) => uri.Scheme is "https" or "http" &&
        (IsQqHost(uri.Host) || uri.Host.Equals("qqmusic.qq.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".qqmusic.qq.com", StringComparison.OrdinalIgnoreCase));

    private static string Query(string url, Dictionary<string, string> values) => url + "?" + string.Join("&", values.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
    private static Dictionary<string, string> QueryArguments(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string entry in query.TrimStart('?').Split('&'))
        {
            int separator = entry.IndexOf('=');
            if (separator > 0) result[Uri.UnescapeDataString(entry[..separator])] = Uri.UnescapeDataString(entry[(separator + 1)..].Replace("+", " "));
        }
        return result;
    }
    private static Dictionary<string, string> ReadCookies(HttpResponseMessage response)
    {
        var cookies = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? headers)) return cookies;
        foreach (string header in headers)
            foreach (Match match in Regex.Matches(header, @"(?:^|,(?=\s*[^;,=\s]+=))\s*([^;,=\s]+)=([^;]*?)(?=;|,(?=\s*[^;,=\s]+=)|$)"))
                cookies[match.Groups[1].Value] = match.Groups[2].Value.Trim();
        return cookies;
    }
    private static string CookieHeader(Dictionary<string, string> cookies) => string.Join("; ", cookies.Select(pair => pair.Key + "=" + pair.Value));
    private PlaybackSource PlaybackResult(string url, AccountSession? session)
    {
        EnsureCurrent(session);
        string file = new Uri(url).Segments[^1];
        string quality = file.Length >= 4 ? file[..4] switch
        {
            "M800" => "高品质 · MP3 320 kbps",
            "M500" => "标准 · MP3 128 kbps",
            "C400" => "流畅 · AAC 96 kbps",
            "F000" => "无损 FLAC",
            _ => "平台返回音质"
        } : "平台返回音质";
        return new(url, quality);
    }
    private void EnsureCurrent(AccountSession? session)
    {
        AccountSession? current = Session;
        if (session?.Uin != current?.Uin || session?.MusicKey != current?.MusicKey)
            throw new OperationCanceledException("账号已经变化，已忽略旧账号请求。");
    }
    public void Dispose() { _http.Dispose(); _refreshLock.Dispose(); }
}
