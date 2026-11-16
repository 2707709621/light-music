using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FeatherPlayer;

public partial class MainWindow
{
    private readonly MenuDataCache _menuDataCache=new();
    private static readonly TimeSpan MenuRefreshInterval=TimeSpan.FromMinutes(2);
    private string CacheAccount => _api.Session is { } session
        ? session.Provider switch { MusicProvider.Netease => "netease/" + session.Uin, MusicProvider.Kugou => "kugou/v2/" + session.Uin, _ => session.LoginType + "/" + session.Uin }
        : throw new InvalidOperationException("请先登录账号。");
    private Playlist LikedPlaylist => new("liked", "我喜欢", IsLiked: true, Provider: _api.Provider);
    private Task<IReadOnlyList<Song>> CachedPlaylistAsync(Playlist playlist,CancellationToken ct)=>
        _menuDataCache.GetAsync(CacheAccount,PlaylistCacheKey(playlist),MenuRefreshInterval,token=>_api.GetPlaylistSongsAsync(playlist,token),ct,_accountCts.Token);
    private static string PlaylistCacheKey(Playlist playlist) => "playlist/" + (playlist.IsLiked ? "liked" : playlist.Id);
    private Task<IReadOnlyList<Playlist>> CachedPlaylistsAsync(CancellationToken ct)=>
        _menuDataCache.GetAsync(CacheAccount,"playlists",MenuRefreshInterval,token=>_api.GetPlaylistsAsync(token),ct,_accountCts.Token);
    private Task<IReadOnlyList<Song>> CachedRecommendationAsync(bool daily,CancellationToken ct)
    {
        string key=daily?"daily/"+DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd"):"guess";
        return _menuDataCache.GetAsync(CacheAccount,key,daily?TimeSpan.FromHours(24):TimeSpan.FromMinutes(10),
            token=>daily?_api.GetDailySongsAsync(token):_api.GetGuessSongsAsync(token),ct,_accountCts.Token);
    }
}
