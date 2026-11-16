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
using System.Threading;
using System.Threading.Tasks;

namespace FeatherPlayer;

public sealed class NeteaseMusicException(string message, int code = 0) : Exception(message)
{
    public int Code { get; } = code;
}

// Protocol parameters are documented by NeteaseCloudMusicApiEnhanced/api-enhanced.
// Requests go directly to music.163.com and use the user's official website session.
internal sealed class NeteaseMusicClient : IMusicClient
{
    private const string PublicKey = """
        -----BEGIN PUBLIC KEY-----
        MIGfMA0GCSqGSIb3DQEBAQUAA4GNADCBiQKBgQDgtQn2JZ34ZC28NWYpAUd98iZ37BUrX/aKzmFbt7clFSs6sXqHauqKWqdtLkF2KexO40H1YTX8z2lSgBBOAxLsvaklV8k4cBFK9snQXE9/DDaFt6Rr7iVZMldczhC0JNgTz+SHXT6CBHuX3e9SdB1Ua44oncaTWz7OBGLbCiK45wIDAQAB
        -----END PUBLIC KEY-----
        """;
    private static readonly RSAParameters RsaKey = ReadPublicKey();
    private readonly HttpClient _http = new(new SocketsHttpHandler
    {
        UseCookies = false,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(12),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        MaxConnectionsPerServer = 4
    }) { Timeout = TimeSpan.FromSeconds(25), MaxResponseContentBufferSize = 16 * 1024 * 1024 };
    private readonly SemaphoreSlim _writes = new(1, 1);
    private AccountSession? _session;
    public AccountSession? Session => Volatile.Read(ref _session);
    public AudioQuality Quality { get; set; } = AudioQuality.Auto;

    public void SetSession(AccountSession? session)
    {
        if (session != null && (session.Provider != MusicProvider.Netease || string.IsNullOrWhiteSpace(session.MusicKey)))
            throw new NeteaseMusicException("网易云登录凭证不完整，请重新登录。");
        Volatile.Write(ref _session, session);
    }

    public async Task<AccountSession> GetAccountAsync(CancellationToken ct)
    {
        var session = RequireSession();
        var root = await ApiAsync("/api/nuser/account/get", new(), session, ct);
        var profile = At(root, "profile");
        long id = Number(profile, "userId");
        if (id <= 0) throw new NeteaseMusicException("网易云登录状态已失效，请重新登录。", 301);
        if (session.Uin != "0" && session.Uin != id.ToString(CultureInfo.InvariantCulture))
            throw new NeteaseMusicException("网易云返回的账号与当前登录不一致，请重新登录。");
        return session with { Uin = id.ToString(CultureInfo.InvariantCulture), Nickname = Text(profile, "nickname") };
    }

    public async Task<IReadOnlyList<Playlist>> GetPlaylistsAsync(CancellationToken ct)
    {
        var session = RequireSession();
        var result = new List<Playlist>();
        for (int offset = 0; offset < 20000; offset += 200)
        {
            var root = await ApiAsync("/api/user/playlist", new()
            {
                ["uid"] = session.Uin, ["limit"] = 200, ["offset"] = offset, ["includeVideo"] = true
            }, session, ct);
            var page = Items(At(root, "playlist")).ToArray();
            foreach (var item in page)
            {
                string id = Text(item, "id");
                bool owned = Text(At(item, "creator"), "userId") == session.Uin;
                bool liked = owned && Number(item, "specialType") == 5;
                result.Add(new Playlist(id, liked ? "我喜欢" : Text(item, "name"),
                    (int)Number(item, "trackCount"), Text(item, "coverImgUrl"), liked,
                    Writable: owned, Provider: MusicProvider.Netease));
            }
            if (At(root, "more").ValueKind != JsonValueKind.True || page.Length == 0) break;
        }
        return result.DistinctBy(p => p.Id).OrderByDescending(p => p.IsLiked).ToArray();
    }

    public async Task<IReadOnlyList<Song>> GetPlaylistSongsAsync(Playlist playlist, CancellationToken ct)
    {
        var session = RequireSession();
        if (playlist.IsLiked && playlist.Id == "liked")
            playlist = (await GetPlaylistsAsync(ct)).FirstOrDefault(p => p.IsLiked)
                ?? throw new NeteaseMusicException("网易云暂未返回“我喜欢”歌单，请在官方客户端确认。");
        if (playlist.Provider != MusicProvider.Netease || !long.TryParse(playlist.Id, out long id) || id <= 0)
            throw new NeteaseMusicException("网易云歌单编号无效。");
        var root = await ApiAsync("/api/v6/playlist/detail", new() { ["id"] = id, ["n"] = 100000, ["s"] = 0 }, session, ct);
        var detail = At(root, "playlist");
        var ids = Items(At(detail, "trackIds")).Select(item => Number(item, "id")).Where(n => n > 0).ToArray();
        if (ids.Length > 20000) throw new NeteaseMusicException("该歌单超过当前支持的两万首歌曲上限。");
        var songs = ParseSongs(At(detail, "tracks")).ToDictionary(s => s.Id);
        foreach (var batch in ids.Where(id => !songs.ContainsKey(id)).Chunk(200))
        {
            var response = await ApiAsync("/api/v3/song/detail", new()
            {
                ["c"] = JsonSerializer.Serialize(batch.Select(id => new { id }))
            }, session, ct);
            foreach (var song in ParseSongs(At(response, "songs"))) songs[song.Id] = song;
        }
        return ids.Length == 0 ? songs.Values.ToArray() : ids.Where(songs.ContainsKey).Select(id => songs[id]).ToArray();
    }

    public async Task<IReadOnlyList<Song>> SearchAsync(string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<Song>();
        var root = await ApiAsync("/api/cloudsearch/pc", new()
        {
            ["s"] = query.Trim(), ["type"] = 1, ["limit"] = 100, ["offset"] = 0, ["total"] = true
        }, Session, ct);
        return ParseSongs(At(At(root, "result"), "songs"));
    }

    public async Task<string> GetPlaybackUrlAsync(Song song, CancellationToken ct)
        => (await GetPlaybackSourceAsync(song, ct).ConfigureAwait(false)).Url;

    public async Task<PlaybackSource> GetPlaybackSourceAsync(Song song, CancellationToken ct)
    {
        ValidateSong(song);
        var quality = Quality;
        string level = quality switch
        {
            AudioQuality.Standard => "standard",
            AudioQuality.Lossless => "lossless",
            AudioQuality.HiRes => "hires",
            _ => "exhigh"
        };
        var root = await ApiAsync("/api/song/enhance/player/url/v1", new()
        {
            ["ids"] = JsonSerializer.Serialize(new[] { song.Id }), ["level"] = level,
            ["encodeType"] = quality is AudioQuality.Lossless or AudioQuality.HiRes ? "flac" : "mp3"
        }, Session, ct);
        var item = Items(At(root, "data")).FirstOrDefault(item => Number(item, "id") == song.Id);
        string url = Text(item, "url");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new NeteaseMusicException("网易云未返回可播放地址，请检查歌曲的会员、购买或版权权限。", (int)Number(item, "code"));
        string actualQuality = Text(item, "level") switch
        {
            "standard" => "标准",
            "higher" => "较高音质",
            "exhigh" => "高品质",
            "lossless" => "无损 FLAC",
            "hires" => "Hi-Res",
            _ => "平台返回音质"
        };
        long bitrate = Number(item, "br");
        if (bitrate > 0) actualQuality += " · " + (bitrate / 1000) + " kbps";
        if (At(item, "freeTrialInfo").ValueKind == JsonValueKind.Object) actualQuality += " · 试听";
        string address = uri.Scheme == "http" ? new UriBuilder(uri) { Scheme = "https", Port = -1 }.Uri.AbsoluteUri : url;
        return new(address, actualQuality);
    }

    public async Task<string> GetCoverUrlAsync(Song song, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(song.CoverUrl)) return song.CoverUrl;
        ValidateSong(song);
        var root = await ApiAsync("/api/v3/song/detail", new()
        {
            ["c"] = JsonSerializer.Serialize(new[] { new { id = song.Id } })
        }, Session, ct);
        return ParseSongs(At(root, "songs")).FirstOrDefault()?.CoverUrl ?? "";
    }

    public async Task<IReadOnlyList<LyricLine>> GetLyricsAsync(Song song, CancellationToken ct)
    {
        ValidateSong(song);
        var root = await ApiAsync("/api/song/lyric", new()
        {
            ["id"] = song.Id, ["lv"] = -1, ["tv"] = -1, ["rv"] = -1, ["kv"] = -1
        }, Session, ct);
        return LyricTimeline.Parse(Text(At(root, "lrc"), "lyric"));
    }

    public async Task<IReadOnlyList<Song>> GetDailySongsAsync(CancellationToken ct)
    {
        var root = await ApiAsync("/api/v3/discovery/recommend/songs", new(), RequireSession(), ct);
        return ParseSongs(At(At(root, "data"), "dailySongs"));
    }

    public async Task<IReadOnlyList<Song>> GetGuessSongsAsync(CancellationToken ct)
    {
        var root = await ApiAsync("/api/v1/radio/get", new(), RequireSession(), ct);
        return ParseSongs(At(root, "data"));
    }

    public async Task<IReadOnlyList<Song>> SetSongInPlaylistAsync(Song song, Playlist playlist, bool add, CancellationToken ct)
    {
        ValidateSong(song);
        var session = RequireSession();
        if (playlist.Provider != MusicProvider.Netease) throw new NeteaseMusicException("请选择网易云歌单。");
        await _writes.WaitAsync(ct);
        try
        {
            EnsureSession(session);
            var target = (await GetPlaylistsAsync(ct)).FirstOrDefault(p => playlist.IsLiked ? p.IsLiked : p.Id == playlist.Id);
            if (target == null || !target.Writable) throw new NeteaseMusicException("该歌单不属于当前网易云账号。");
            var before = await GetPlaylistSongsAsync(target, ct);
            if (before.Any(s => s.Id == song.Id) == add) return before;
            if (target.IsLiked)
                await ApiAsync("/api/radio/like", new()
                {
                    ["trackId"] = song.Id, ["like"] = add, ["alg"] = "itembased", ["time"] = 3
                }, session, ct);
            else
                await ApiAsync("/api/playlist/manipulate/tracks", new()
                {
                    ["pid"] = target.Id, ["op"] = add ? "add" : "del",
                    ["trackIds"] = JsonSerializer.Serialize(new[] { song.Id }), ["imme"] = true
                }, session, ct);
            var after = await GetPlaylistSongsAsync(target, ct);
            EnsureSession(session);
            if (after.Any(s => s.Id == song.Id) != add)
                throw new NeteaseMusicException("网易云尚未确认歌单变化，请稍后重新展开查看。");
            return after;
        }
        finally { _writes.Release(); }
    }

    private async Task<JsonElement> ApiAsync(string path, Dictionary<string, object> data, AccountSession? session, CancellationToken ct)
    {
        string csrf = CookieValue(session?.Cookie ?? "", "__csrf");
        data["csrf_token"] = csrf;
        var encrypted = Encrypt(data);
        string url = "https://music.163.com/weapi/" + path[5..] + "?csrf_token=" + Uri.EscapeDataString(csrf);
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Referrer = new Uri("https://music.163.com/");
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/130.0.0.0 Safari/537.36");
        request.Headers.TryAddWithoutValidation("Origin", "https://music.163.com");
        request.Headers.TryAddWithoutValidation("Cookie", "os=pc; appver=3.0.18; " + (session?.Cookie ?? ""));
        request.Content = new FormUrlEncodedContent(encrypted);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var root = document.RootElement.Clone();
        ct.ThrowIfCancellationRequested();
        EnsureSession(session);
        int code = (int)Number(root, "code");
        if (code != 200)
        {
            string message = code == 301 ? "网易云登录已过期，请重新登录。" : Text(root, "message");
            if (message.Length == 0) message = "网易云请求未成功，请稍后重试。";
            throw new NeteaseMusicException(message, code);
        }
        return root;
    }

    private static Dictionary<string, string> Encrypt(Dictionary<string, object> data)
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        string secret = new(Enumerable.Range(0, 16).Select(_ => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]).ToArray());
        string inner = EncryptAes(JsonSerializer.Serialize(data), "0CoJUm6Qyw8W8jud");
        byte[] reversed = Encoding.ASCII.GetBytes(new string(secret.Reverse().ToArray()));
        var value = new BigInteger(reversed, isUnsigned: true, isBigEndian: true);
        var exponent = new BigInteger(RsaKey.Exponent!, isUnsigned: true, isBigEndian: true);
        var modulus = new BigInteger(RsaKey.Modulus!, isUnsigned: true, isBigEndian: true);
        byte[] encrypted = BigInteger.ModPow(value, exponent, modulus).ToByteArray(isUnsigned: true, isBigEndian: true);
        return new()
        {
            ["params"] = EncryptAes(inner, secret),
            ["encSecKey"] = Convert.ToHexString(encrypted).ToLowerInvariant().PadLeft(RsaKey.Modulus!.Length * 2, '0')
        };
    }
    private static string EncryptAes(string value, string key)
    {
        using var aes = Aes.Create();
        aes.Key = Encoding.ASCII.GetBytes(key);
        return Convert.ToBase64String(aes.EncryptCbc(Encoding.UTF8.GetBytes(value), Encoding.ASCII.GetBytes("0102030405060708")));
    }
    private static RSAParameters ReadPublicKey()
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(PublicKey);
        return rsa.ExportParameters(false);
    }
    private AccountSession RequireSession() => Session ?? throw new NeteaseMusicException("请先登录网易云音乐。", 301);
    private void EnsureSession(AccountSession? session)
    {
        if (!ReferenceEquals(session, Session)) throw new OperationCanceledException("网易云账号已切换。");
    }
    private static void ValidateSong(Song song)
    {
        if (song.Provider != MusicProvider.Netease || song.Id <= 0) throw new NeteaseMusicException("网易云歌曲编号无效。");
    }
    private static string CookieValue(string header, string name) => header.Split(';').Select(s => s.Trim())
        .FirstOrDefault(s => s.StartsWith(name + "=", StringComparison.Ordinal))?[(name.Length + 1)..] ?? "";
    private static JsonElement At(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var item) ? item : default;
    private static IEnumerable<JsonElement> Items(JsonElement value) => value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : Enumerable.Empty<JsonElement>();
    private static string Text(JsonElement value, string name)
    {
        var item = At(value, name);
        return item.ValueKind switch
        {
            JsonValueKind.String => item.GetString() ?? "",
            JsonValueKind.Number => item.GetRawText(),
            _ => ""
        };
    }
    private static long Number(JsonElement value, string name) => long.TryParse(Text(value, name), out long number) ? number : 0;
    private static IReadOnlyList<Song> ParseSongs(JsonElement array) => Items(array).Select(item =>
    {
        var album = At(item, "al");
        if (album.ValueKind != JsonValueKind.Object) album = At(item, "album");
        var artists = At(item, "ar");
        if (artists.ValueKind != JsonValueKind.Array) artists = At(item, "artists");
        long id = Number(item, "id");
        long duration = Number(item, "dt");
        if (duration == 0) duration = Number(item, "duration");
        return new Song("netease:" + id.ToString(CultureInfo.InvariantCulture), Text(item, "name"),
            string.Join(" / ", Items(artists).Select(artist => Text(artist, "name"))), Text(album, "name"),
            (int)Math.Clamp(duration / 1000, 0, int.MaxValue), Id: id, Provider: MusicProvider.Netease, CoverUrl: Text(album, "picUrl"));
    }).Where(song => song.Id > 0 && song.Title.Length > 0).DistinctBy(song => song.Id).ToArray();

    public void Dispose() { _http.Dispose(); }
}
