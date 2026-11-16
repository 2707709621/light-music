using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FeatherPlayer;

public interface IMusicClient : IDisposable
{
    AccountSession? Session { get; }
    AudioQuality Quality { get; set; }
    void SetSession(AccountSession? session);
    Task<AccountSession> GetAccountAsync(CancellationToken ct);
    Task<IReadOnlyList<Playlist>> GetPlaylistsAsync(CancellationToken ct);
    Task<IReadOnlyList<Song>> GetPlaylistSongsAsync(Playlist playlist, CancellationToken ct);
    Task<IReadOnlyList<Song>> SearchAsync(string query, CancellationToken ct);
    Task<string> GetPlaybackUrlAsync(Song song, CancellationToken ct);
    Task<PlaybackSource> GetPlaybackSourceAsync(Song song, CancellationToken ct);
    Task<IReadOnlyList<LyricLine>> GetLyricsAsync(Song song, CancellationToken ct);
    Task<string> GetCoverUrlAsync(Song song, CancellationToken ct);
    Task<IReadOnlyList<Song>> GetDailySongsAsync(CancellationToken ct);
    Task<IReadOnlyList<Song>> GetGuessSongsAsync(CancellationToken ct);
    Task<IReadOnlyList<Song>> SetSongInPlaylistAsync(Song song, Playlist playlist, bool add, CancellationToken ct);
}

internal sealed class MusicServiceClient : IMusicClient
{
    private readonly QQMusicClient _qq = new();
    private readonly NeteaseMusicClient _netease = new();
    private readonly KugouMusicClient _kugou = new();
    internal MusicProvider Provider { get; private set; }
    internal static string ProviderName(MusicProvider provider) => provider switch { MusicProvider.Netease => "网易云音乐", MusicProvider.Kugou => "酷狗音乐", _ => "QQ 音乐" };
    internal string Name => ProviderName(Provider);
    private IMusicClient Active => Client(Provider);
    public AccountSession? Session => Active.Session;
    public AudioQuality Quality { get => Active.Quality; set => Active.Quality = value; }
    internal AudioQuality QqQuality { get => _qq.Quality; set => _qq.Quality = value; }
    internal AudioQuality NeteaseQuality { get => _netease.Quality; set => _netease.Quality = value; }
    internal AudioQuality KugouQuality { get => _kugou.Quality; set => _kugou.Quality = value; }
    internal void Select(MusicProvider provider)
    {
        if (!Enum.IsDefined(provider)) throw new ArgumentOutOfRangeException(nameof(provider));
        Provider = provider;
    }
    private IMusicClient Client(MusicProvider provider) => provider switch
    {
        MusicProvider.Qq => _qq,
        MusicProvider.Netease => _netease,
        MusicProvider.Kugou => _kugou,
        _ => throw new ArgumentOutOfRangeException(nameof(provider))
    };
    public void SetSession(AccountSession? session)
    {
        if (session != null && session.Provider != Provider)
            throw new InvalidOperationException("账号与当前音乐源不一致。");
        Active.SetSession(session);
    }
    public Task<AccountSession> GetAccountAsync(CancellationToken ct) => Active.GetAccountAsync(ct);
    public Task<IReadOnlyList<Playlist>> GetPlaylistsAsync(CancellationToken ct) => Active.GetPlaylistsAsync(ct);
    public Task<IReadOnlyList<Song>> GetPlaylistSongsAsync(Playlist playlist, CancellationToken ct) => Active.GetPlaylistSongsAsync(playlist, ct);
    public Task<IReadOnlyList<Song>> SearchAsync(string query, CancellationToken ct) => Active.SearchAsync(query, ct);
    public Task<string> GetPlaybackUrlAsync(Song song, CancellationToken ct) => Client(song.Provider).GetPlaybackUrlAsync(song, ct);
    public Task<PlaybackSource> GetPlaybackSourceAsync(Song song, CancellationToken ct) => Client(song.Provider).GetPlaybackSourceAsync(song, ct);
    public Task<IReadOnlyList<LyricLine>> GetLyricsAsync(Song song, CancellationToken ct) => Client(song.Provider).GetLyricsAsync(song, ct);
    public Task<string> GetCoverUrlAsync(Song song, CancellationToken ct) => Client(song.Provider).GetCoverUrlAsync(song, ct);
    public Task<IReadOnlyList<Song>> GetDailySongsAsync(CancellationToken ct) => Active.GetDailySongsAsync(ct);
    public Task<IReadOnlyList<Song>> GetGuessSongsAsync(CancellationToken ct) => Active.GetGuessSongsAsync(ct);
    public Task<IReadOnlyList<Song>> SetSongInPlaylistAsync(Song song, Playlist playlist, bool add, CancellationToken ct)
    {
        if (song.Provider != Provider || playlist.Provider != Provider)
            throw new InvalidOperationException("歌曲与歌单属于不同音乐源，请重新打开菜单。");
        return Active.SetSongInPlaylistAsync(song, playlist, add, ct);
    }
    public void Dispose() { _qq.Dispose(); _netease.Dispose(); _kugou.Dispose(); }
}
