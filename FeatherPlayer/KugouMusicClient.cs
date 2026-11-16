using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace FeatherPlayer;

public sealed class KugouMusicException(string message, int code = 0) : Exception(message)
{
    public int Code { get; } = code;
}

// Protocol adapted from MakcRe/KuGouMusicApi (MIT). See THIRD-PARTY-NOTICES.
internal sealed class KugouMusicClient : IMusicClient
{
    private const string AndroidSalt = "OIlwieks28dk2k092lksi2UIkp";
    private const string WebSalt = "NVPh5oo715z5DIWAeQlhMDsWXXQV4hwt";
    private const string PublicKey = "MIGfMA0GCSqGSIb3DQEBAQUAA4GNADCBiQKBgQDIAG7QOELSYoIJvTFJhMpe1s/gbjDJX51HBNnEl5HXqTW6lQ7LC8jr9fWZTwusknp+sVGzwd40MwP6U5yDE27M/X1+UR4tvOGOqp94TJtQ1EPnWGWXngpeIW5GxoQGao1rmYWAu6oi1z9XkChrsUdC6DJE5E221wf/4WLFxwAtRQIDAQAB";
    private readonly HttpClient _http = new(new SocketsHttpHandler
    {
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(12),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        MaxConnectionsPerServer = 4
    })
    { Timeout = TimeSpan.FromSeconds(25), MaxResponseContentBufferSize = 12 * 1024 * 1024 };
    private readonly string _device = Guid.NewGuid().ToString("N");
    private readonly SemaphoreSlim _writes = new(1, 1);
    private AccountSession? _session;
    public AccountSession? Session => Volatile.Read(ref _session);
    public AudioQuality Quality { get; set; }

    public void SetSession(AccountSession? session)
    {
        if (session != null && (session.Provider != MusicProvider.Kugou || !long.TryParse(session.Uin, out long id) || id <= 0 || string.IsNullOrWhiteSpace(session.MusicKey)))
            throw new KugouMusicException("酷狗登录凭据不完整，请重新扫码。");
        Volatile.Write(ref _session, session);
    }

    internal async Task<string> CreateQrAsync(CancellationToken ct)
    {
        var root = await RequestAsync("/v2/qrcode", new()
        {
            ["appid"] = 1001,
            ["type"] = 1,
            ["plat"] = 4,
            ["srcappid"] = 2919,
            ["qrcode_txt"] = "https://h5.kugou.com/apps/loginQRCode/html/index.html?appid=1005&"
        }, null, null, ct, host: "https://login-user.kugou.com", web: true).ConfigureAwait(false);
        string key = Text(At(root, "data"), "qrcode", "key");
        if (!Regex.IsMatch(key, @"^[A-Za-z0-9_-]{4,512}$")) throw new KugouMusicException("酷狗未返回有效二维码，请刷新重试。");
        return key;
    }

    internal async Task<(int Status, AccountSession? Session)> CheckQrAsync(string key, CancellationToken ct)
    {
        var root = await RequestAsync("/v2/get_userinfo_qrcode", new()
        {
            ["plat"] = 4,
            ["appid"] = 1005,
            ["srcappid"] = 2919,
            ["qrcode"] = key,
            ["dev"] = "light music"
        }, null, null, ct, host: "https://login-user.kugou.com", web: true).ConfigureAwait(false);
        var data = At(root, "data");
        int status = (int)Number(data, "status");
        if (status != 4) return (status, null);
        string token = Text(data, "token"), uid = Text(data, "userid");
        if (token.Length == 0 || !long.TryParse(uid, out long id) || id <= 0) throw new KugouMusicException("酷狗未返回完整登录凭据，请刷新二维码。");
        return (status, new AccountSession(uid, token, Text(data, "nickname", "nick_name"),
            "mid=" + _device + "; dfid=-", Provider: MusicProvider.Kugou));
    }

    public async Task<AccountSession> GetAccountAsync(CancellationToken ct)
    {
        var session = RequireSession();
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var root = await RequestAsync("/v3/get_my_info", new() { ["plat"] = 1 }, new
        {
            visit_time = now,
            usertype = 1,
            p = EncryptIdentity(session.MusicKey, now),
            userid = long.Parse(session.Uin, CultureInfo.InvariantCulture)
        }, "usercenter.kugou.com", ct, session: session).ConfigureAwait(false);
        EnsureCurrent(session);
        var data = At(root, "data");
        string uid = Text(data, "userid", "user_id");
        if (uid.Length > 0 && uid != session.Uin) throw new KugouMusicException("酷狗返回的账号不一致，请重新登录。");
        string nick = Text(data, "nickname", "nick_name", "username");
        return session with { Nickname = nick.Length > 0 ? nick : session.Nickname };
    }

    public async Task<IReadOnlyList<Playlist>> GetPlaylistsAsync(CancellationToken ct)
    {
        var session = RequireSession();
        var result = new List<Playlist>();
        for (int page = 1; page <= 100; page++)
        {
            var root = await RequestAsync("/v7/get_all_list", new() { ["plat"] = 1, ["userid"] = session.Uin, ["token"] = session.MusicKey }, new
            {
                userid = session.Uin,
                token = session.MusicKey,
                total_ver = 979,
                type = 2,
                page,
                pagesize = 100
            }, "cloudlist.service.kugou.com", ct, session: session).ConfigureAwait(false);
            var data = At(root, "data");
            var rows = Array(data, "info", "lists", "list").ToArray();
            foreach (var row in rows)
            {
                string id = Text(row, "listid", "list_id"), name = Text(row, "name", "listname");
                if (id.Length == 0 || result.Any(item => item.Id == id)) continue;
                // is_def=2 identifies the heart/favorites list. is_def=1 (listid=1)
                // is the separate default collection, and names can belong to ordinary lists.
                bool liked = Number(row, "is_def") == 2;
                string owner = Text(row, "list_create_userid", "create_userid", "userid");
                bool writable = owner.Length == 0 || owner == session.Uin;
                result.Add(new Playlist(id, liked ? "我喜欢" : name, (int)Number(row, "count", "songcount", "filecount"),
                    Cover(Text(row, "pic", "img", "image")), liked, Writable: writable, Provider: MusicProvider.Kugou));
            }
            if (rows.Length < 100)
            {
                EnsureCurrent(session);
                if (!result.Any(item => item.IsLiked))
                {
                    int likedIndex = result.FindIndex(item => item.Writable && item.Name == "我喜欢");
                    if (likedIndex >= 0) result[likedIndex] = result[likedIndex] with { IsLiked = true };
                }
                return result;
            }
        }
        throw new KugouMusicException("酷狗歌单数量超过读取上限，请缩小范围后重试。");
    }

    private async Task<Playlist> ResolvePlaylistAsync(Playlist playlist, CancellationToken ct)
    {
        if (playlist.Provider != MusicProvider.Kugou) throw new KugouMusicException("请选择酷狗歌单。");
        if (playlist.Id != "liked") return playlist;
        return (await GetPlaylistsAsync(ct).ConfigureAwait(false)).FirstOrDefault(item => item.IsLiked)
            ?? throw new KugouMusicException("账号中未找到“我喜欢”歌单。");
    }

    public async Task<IReadOnlyList<Song>> GetPlaylistSongsAsync(Playlist playlist, CancellationToken ct)
    {
        playlist = await ResolvePlaylistAsync(playlist, ct).ConfigureAwait(false);
        var session = RequireSession();
        var songs = new List<Song>();
        for (int page = 1; page <= 100; page++)
        {
            // This authenticated cloud-list endpoint also returns fileid for later removals.
            // The v3 variant rejects the same signed session with 20028 on affected accounts.
            var root = await RequestAsync("/v4/get_list_all_file", new(), new
            {
                listid = playlist.Id,
                userid = session.Uin,
                type = 0,
                page,
                pagesize = 300,
                area_code = 1,
                allplatform = 1,
                show_cover = 1,
                token = session.MusicKey
            }, "cloudlist.service.kugou.com", ct, session: session).ConfigureAwait(false);
            var data = At(root, "data");
            var rows = Array(data, "info", "songs", "list", "files").ToArray();
            if (rows.Length == 0 && (data.ValueKind != JsonValueKind.Object || Number(data, "count") > songs.Count))
                throw new KugouMusicException("酷狗未返回完整歌单曲目，请稍后重新读取。");
            songs.AddRange(rows.Select(ParseSong).OfType<Song>());
            if (rows.Length < 300) { EnsureCurrent(session); return songs; }
        }
        throw new KugouMusicException("酷狗歌单曲目超过读取上限。");
    }

    public async Task<IReadOnlyList<Song>> SearchAsync(string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query)) return System.Array.Empty<Song>();
        var root = await RequestAsync("/v2/search/song", new()
        {
            ["keyword"] = query.Trim(),
            ["page"] = 1,
            ["pagesize"] = 100,
            ["platform"] = "AndroidFilter",
            ["iscorrection"] = 1,
            ["privilegefilter"] = 0,
            ["area_code"] = 1,
            ["dopicfull"] = 1
        }, null, "complexsearch.kugou.com", ct).ConfigureAwait(false);
        return Array(At(root, "data"), "lists", "info", "list").Select(ParseSong).OfType<Song>().ToArray();
    }

    public async Task<string> GetPlaybackUrlAsync(Song song, CancellationToken ct) =>
        (await GetPlaybackSourceAsync(song, ct).ConfigureAwait(false)).Url;

    public async Task<PlaybackSource> GetPlaybackSourceAsync(Song song, CancellationToken ct)
    {
        ValidateSong(song);
        AudioQuality selected = Quality;
        try
        {
            return await ResolvePlaybackAsync(song, selected == AudioQuality.Auto ? AudioQuality.High : selected, ct).ConfigureAwait(false);
        }
        catch (KugouMusicException error) when (selected == AudioQuality.Auto && error.Code is not (401 or 403 or 429))
        {
            return await ResolvePlaybackAsync(song, AudioQuality.Standard, ct).ConfigureAwait(false);
        }
    }

    private async Task<PlaybackSource> ResolvePlaybackAsync(Song song, AudioQuality selected, CancellationToken ct)
    {
        var session = Session;
        string quality = selected switch { AudioQuality.High => "320", AudioQuality.Lossless => "flac", AudioQuality.HiRes => "high", _ => "128" };
        var root = await RequestAsync("/v5/url", new()
        {
            ["album_id"] = long.TryParse(song.AlbumMid, out long album) ? album : 0,
            ["album_audio_id"] = song.Id,
            ["hash"] = song.Mid.ToLowerInvariant(),
            ["area_code"] = 1,
            ["ssa_flag"] = "is_fromtrack",
            ["version"] = 11430,
            ["page_id"] = 151369488,
            ["quality"] = quality,
            ["behavior"] = "play",
            ["pid"] = 2,
            ["cmd"] = 26,
            ["pidversion"] = 3001,
            ["IsFreePart"] = 0,
            ["ppage_id"] = "463467626,350369493,788954147",
            ["cdnBackup"] = 1,
            ["module"] = "",
            ["clientver"] = 11430
        }, null, "trackercdn.kugou.com", ct, session: session, playbackKey: true).ConfigureAwait(false);
        EnsureCurrent(session);
        string url = Array(root, "url", "backupUrl", "backup_url").Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : null)
            .FirstOrDefault(ValidAudioUrl) ?? Text(root, "url");
        if (!ValidAudioUrl(url)) throw new KugouMusicException("酷狗未返回可播放地址，请检查账号的歌曲及音质权限。");
        long bitrate = Number(root, "bitRate", "bitrate");
        return new PlaybackSource(url, bitrate > 0 ? "酷狗 · " + (bitrate / 1000) + " kbps" : "酷狗 · " + AudioQualityNames.Name(selected));
    }

    public Task<string> GetCoverUrlAsync(Song song, CancellationToken ct) => Task.FromResult(song.CoverUrl);

    public async Task<IReadOnlyList<LyricLine>> GetLyricsAsync(Song song, CancellationToken ct)
    {
        ValidateSong(song);
        var root = await RequestAsync("/v1/search", new()
        {
            ["album_audio_id"] = song.Id,
            ["duration"] = song.DurationSeconds * 1000L,
            ["hash"] = song.Mid,
            ["keyword"] = song.Artist + " - " + song.Title,
            ["lrctxt"] = 1,
            ["man"] = "no"
        }, null, null, ct, host: "https://lyrics.kugou.com").ConfigureAwait(false);
        var candidate = Array(root, "candidates").FirstOrDefault();
        string id = Text(candidate, "id"), access = Text(candidate, "accesskey");
        if (id.Length == 0 || access.Length == 0) return System.Array.Empty<LyricLine>();
        var lyric = await RequestAsync("/download", new()
        {
            ["ver"] = 1,
            ["client"] = "android",
            ["id"] = id,
            ["accesskey"] = access,
            ["fmt"] = "lrc",
            ["charset"] = "utf8"
        }, null, null, ct, host: "https://lyrics.kugou.com").ConfigureAwait(false);
        string content = Text(lyric, "content");
        return content.Length == 0 ? System.Array.Empty<LyricLine>() : LyricTimeline.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(content)));
    }

    public async Task<IReadOnlyList<Song>> GetDailySongsAsync(CancellationToken ct)
    {
        var session = RequireSession();
        var root = await RequestAsync("/everyday_song_recommend", new() { ["platform"] = "ios" }, new { platform = "android", userid = session.Uin },
            "everydayrec.service.kugou.com", ct, session: session).ConfigureAwait(false);
        EnsureCurrent(session);
        return Array(At(root, "data"), "song_list", "songs", "list", "info").Select(ParseSong).OfType<Song>().ToArray();
    }

    public async Task<IReadOnlyList<Song>> GetGuessSongsAsync(CancellationToken ct)
    {
        var session = RequireSession();
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var root = await RequestAsync("/v2/personal_recommend", new(), new
        {
            appid = 1005,
            clienttime = now,
            mid = Device(session),
            action = "play",
            recommend_source_locked = 0,
            song_pool_id = 0,
            callerid = 0,
            m_type = 1,
            platform = "ios",
            area_code = 1,
            remain_songcnt = 0,
            clientver = 20489,
            is_overplay = 0,
            mode = "normal",
            fakem = "ca981cfc583a4c37f28d2d49000013c16a0a",
            key = Md5("1005" + AndroidSalt + "20489" + now),
            userid = session.Uin,
            kguid = session.Uin,
            token = session.MusicKey
        }, "persnfm.service.kugou.com", ct, session: session).ConfigureAwait(false);
        EnsureCurrent(session);
        return Array(At(root, "data"), "song_list", "songs", "list", "info").Select(ParseSong).OfType<Song>().ToArray();
    }

    public async Task<IReadOnlyList<Song>> SetSongInPlaylistAsync(Song song, Playlist playlist, bool add, CancellationToken ct)
    {
        ValidateSong(song);
        await _writes.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var session = RequireSession();
            playlist = await ResolvePlaylistAsync(playlist, ct).ConfigureAwait(false);
            if (!playlist.Writable) throw new KugouMusicException("只能修改自己创建的歌单。");
            var current = await GetPlaylistSongsAsync(playlist, ct).ConfigureAwait(false);
            var existing = current.FirstOrDefault(item => item.Mid.Equals(song.Mid, StringComparison.OrdinalIgnoreCase));
            if (add && existing != null || !add && existing == null) return current;
            if (add)
            {
                await RequestAsync("/cloudlist.service/v6/add_song", new()
                {
                    ["last_time"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    ["last_area"] = "gztx",
                    ["userid"] = session.Uin,
                    ["token"] = session.MusicKey
                }, new
                {
                    userid = session.Uin,
                    token = session.MusicKey,
                    listid = playlist.Id,
                    list_ver = 0,
                    type = 0,
                    slow_upload = 1,
                    scene = "false;null",
                    data = new[] { new { number = 1, name = song.Artist + " - " + song.Title, hash = song.Mid, size = 0, sort = 0, timelen = 0, bitrate = 0,
                        album_id = long.TryParse(song.AlbumMid, out long album) ? album : 0, mixsongid = song.Id } }
                }, null, ct, session: session).ConfigureAwait(false);
            }
            else
            {
                if (!long.TryParse(existing!.PlaylistFileId, out long fileId) || fileId <= 0) throw new KugouMusicException("酷狗未返回歌单文件编号，请重新读取歌单后移除。");
                await RequestAsync("/v4/delete_songs", new(), new
                {
                    listid = playlist.Id,
                    userid = session.Uin,
                    token = session.MusicKey,
                    type = 0,
                    list_ver = 0,
                    data = new[] { new { fileid = fileId } }
                }, "cloudlist.service.kugou.com", ct, session: session).ConfigureAwait(false);
            }
            EnsureCurrent(session);
            return await GetPlaylistSongsAsync(playlist, ct).ConfigureAwait(false);
        }
        finally { _writes.Release(); }
    }

    private AccountSession RequireSession() => Session ?? throw new KugouMusicException("请先登录酷狗音乐。");
    private void EnsureCurrent(AccountSession? snapshot)
    {
        if (!ReferenceEquals(snapshot, Session)) throw new OperationCanceledException("酷狗账号已切换，已忽略旧请求。");
    }
    private string Device(AccountSession? session)
    {
        string value = session?.Cookie.Split(';').Select(item => item.Trim()).FirstOrDefault(item => item.StartsWith("mid=", StringComparison.Ordinal))?[4..] ?? _device;
        return Regex.IsMatch(value, "^[a-fA-F0-9]{32}$") ? value : _device;
    }
    private static void ValidateSong(Song song)
    {
        if (song.Provider != MusicProvider.Kugou || !Regex.IsMatch(song.Mid, "^[a-fA-F0-9]{32}$")) throw new KugouMusicException("酷狗歌曲编号无效，请重新读取列表。");
    }
    private static bool ValidAudioUrl(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" &&
        (uri.Host.EndsWith(".kugou.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".kugou.net", StringComparison.OrdinalIgnoreCase));
    private static string Md5(string value) => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string EncryptIdentity(string token, long timestamp)
    {
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(PublicKey), out _);
        var key = rsa.ExportParameters(false);
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(new { token, clienttime = timestamp });
        byte[] padded = new byte[key.Modulus!.Length];
        if (data.Length > padded.Length) throw new KugouMusicException("酷狗登录凭据长度无效，请重新扫码。");
        data.CopyTo(padded, 0);
        var encrypted = BigInteger.ModPow(new BigInteger(padded, true, true), new BigInteger(key.Exponent!, true, true), new BigInteger(key.Modulus, true, true)).ToByteArray(true, true);
        return Convert.ToHexString(encrypted).PadLeft(key.Modulus.Length * 2, '0');
    }

    private async Task<JsonElement> RequestAsync(string path, Dictionary<string, object?> parameters, object? body, string? router, CancellationToken ct,
        string host = "https://gateway.kugou.com", bool web = false, AccountSession? session = null, bool playbackKey = false)
    {
        session ??= Session;
        string mid = Device(session);
        var query = new Dictionary<string, object?>
        {
            ["dfid"] = "-",
            ["mid"] = mid,
            ["uuid"] = "-",
            ["appid"] = 1005,
            ["clientver"] = 20489,
            ["clienttime"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
        if (session != null) { query["token"] = session.MusicKey; query["userid"] = session.Uin; }
        foreach (var pair in parameters) query[pair.Key] = pair.Value;
        if (playbackKey) query["key"] = Md5(Convert.ToString(query["hash"], CultureInfo.InvariantCulture) + "57ae12eb6890223e355ccfcb74edf70d" + query["appid"] + mid + (session?.Uin ?? "0"));
        string json = body == null ? "" : JsonSerializer.Serialize(body, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        string salt = web ? WebSalt : AndroidSalt;
        string sign = string.Concat(query.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "=" + Convert.ToString(pair.Value, CultureInfo.InvariantCulture)));
        query["signature"] = Md5(salt + sign + json + salt);
        string url = host + path + "?" + string.Join("&", query.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(Convert.ToString(pair.Value, CultureInfo.InvariantCulture) ?? "")));
        using var request = new HttpRequestMessage(body == null ? HttpMethod.Get : HttpMethod.Post, url);
        request.Headers.TryAddWithoutValidation("User-Agent", "Android15-1070-11083-46-0-DiscoveryDRADProtocol-wifi");
        request.Headers.TryAddWithoutValidation("mid", mid);
        request.Headers.TryAddWithoutValidation("dfid", "-");
        request.Headers.TryAddWithoutValidation("clienttime", Convert.ToString(query["clienttime"], CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("kg-rc", "1"); request.Headers.TryAddWithoutValidation("kg-thash", "5d816a0");
        request.Headers.TryAddWithoutValidation("kg-rec", "1"); request.Headers.TryAddWithoutValidation("kg-rf", "B9EDA08A64250DEFFBCADDEE00F8F25F");
        if (router != null) request.Headers.TryAddWithoutValidation("x-router", router);
        if (body != null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        try
        {
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new KugouMusicException("酷狗请求失败（HTTP " + (int)response.StatusCode + "）。", (int)response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var root = document.RootElement.Clone();
            int error = (int)Number(root, "error_code", "errcode");
            if (error == 20028)
                throw new KugouMusicException("酷狗要求安全验证（业务码 20028）。请在酷狗官方客户端完成验证后重试。", error);
            // The lyrics service uses errcode=200 for success, unlike the gateway's error_code=0.
            bool lyricsSuccess = host == "https://lyrics.kugou.com" && error == 200;
            if (error != 0 && !lyricsSuccess || At(root, "status").ValueKind == JsonValueKind.Number && Number(root, "status") == 0)
                throw new KugouMusicException("酷狗请求未成功（业务码 " + error + "），请检查登录状态或稍后重试。", error);
            return root;
        }
        catch (HttpRequestException) { throw new KugouMusicException("无法连接酷狗音乐，请检查网络或代理。"); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new KugouMusicException("酷狗音乐请求超时，请稍后重试。"); }
    }

    private static JsonElement At(JsonElement root, string key) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out var value) ? value : default;
    private static string Text(JsonElement root, params string[] names)
    {
        foreach (string name in names)
        {
            var value = At(root, name);
            if (value.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(value.GetString())) return value.GetString()!;
            if (value.ValueKind == JsonValueKind.Number) return value.GetRawText();
        }
        return "";
    }
    private static long Number(JsonElement root, params string[] names) => long.TryParse(Text(root, names), NumberStyles.Any, CultureInfo.InvariantCulture, out long value) ? value : 0;
    private static IEnumerable<JsonElement> Array(JsonElement root, params string[] names)
    {
        if (root.ValueKind == JsonValueKind.Array) return root.EnumerateArray().ToArray();
        foreach (string name in names) if (At(root, name).ValueKind == JsonValueKind.Array) return At(root, name).EnumerateArray().ToArray();
        return System.Array.Empty<JsonElement>();
    }
    private static string Cover(string url) => url.Replace("{size}", "300", StringComparison.Ordinal);
    private static Song? ParseSong(JsonElement item)
    {
        var original = item;
        var baseInfo = At(item, "base");
        if (baseInfo.ValueKind == JsonValueKind.Object) item = baseInfo;
        var audio = At(item, "audio_info");
        var album = At(item, "album_info");
        if (album.ValueKind != JsonValueKind.Object) album = At(item, "albuminfo");
        string hash = Text(item, "hash", "Hash", "filehash", "FileHash");
        if (hash.Length == 0) hash = Text(audio, "hash");
        string name = WebUtility.HtmlDecode(Regex.Replace(Text(item, "songname", "SongName", "name", "filename", "FileName", "audio_name"), "<[^>]+>", ""));
        string artist = Text(item, "singername", "SingerName", "author_name");
        if (artist.Length == 0) artist = string.Join(" / ", Array(item, "authors", "singerinfo").Select(author => Text(author, "author_name", "name")));
        if (artist.Length == 0 && name.Contains(" - ")) { int split = name.IndexOf(" - ", StringComparison.Ordinal); artist = name[..split]; name = name[(split + 3)..]; }
        if (!Regex.IsMatch(hash, "^[a-fA-F0-9]{32}$") || name.Length == 0) return null;
        long duration = Number(item, "timelen", "time_length");
        if (duration == 0) duration = Number(audio, "timelength");
        if (duration == 0) duration = Number(item, "duration", "Duration") * 1000;
        string pic = Text(item, "cover", "img", "Image", "album_sizable_cover", "sizable_cover");
        if (pic.Length == 0) pic = Text(album, "sizable_cover", "cover");
        string albumName = Text(item, "album_name", "AlbumName", "albumname");
        if (albumName.Length == 0) albumName = Text(album, "name", "album_name");
        return new Song(hash.ToUpperInvariant(), name, artist, albumName, (int)Math.Clamp(duration / 1000, 0, int.MaxValue),
            Text(item, "album_id", "AlbumID"), Id: Number(item, "mixsongid", "MixSongID", "album_audio_id"), Provider: MusicProvider.Kugou,
            CoverUrl: Cover(pic), PlaylistFileId: Text(original, "fileid", "file_id") is { Length: > 0 } fileId ? fileId : Text(item, "fileid", "file_id"));
    }
    public void Dispose() { _http.Dispose(); _writes.Dispose(); }
}
