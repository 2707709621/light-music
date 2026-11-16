using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FeatherPlayer;

public sealed partial class QQMusicClient
{
    private readonly SemaphoreSlim _playlistWriteLock=new(1,1);

    public async Task<IReadOnlyList<Song>> GetDailySongsAsync(CancellationToken ct)
    {
        var session=await ReadySessionAsync(ct).ConfigureAwait(false);
        var feed=await RpcAsync("music.recommend.RecommendFeed","get_recommend_feed",new{direction=0,page=1,s_num=0,v_cache=Array.Empty<string>(),v_uniq=Array.Empty<string>()},session,ct).ConfigureAwait(false);
        var cards=Items(At(feed,"v_shelf")).SelectMany(s=>Items(At(s,"v_niche")))
            .SelectMany(n=>Items(At(n,"v_card"))).Where(c=>Number(c,"subtype")==510).ToArray();
        if(cards.Length!=1)throw new QQMusicException("QQ 暂未返回此账号的每日30首，请稍后重新展开。");
        string id=Text(cards[0],"id");
        if(!long.TryParse(id,out long numeric) || numeric<=0)throw new QQMusicException("每日推荐歌单编号无效。");
        long timestamp=Number(cards[0],"time");
        var today=DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)).Date;
        if(timestamp>0 && DateTimeOffset.FromUnixTimeSeconds(timestamp).ToOffset(TimeSpan.FromHours(8)).Date!=today)
            throw new QQMusicException("QQ 返回的每日推荐已过期，请稍后重新展开。");
        EnsureCurrent(session);
        return await GetPlaylistSongsAsync(new Playlist(id,"每日30首"),ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Song>> GetGuessSongsAsync(CancellationToken ct)
    {
        var session=await ReadySessionAsync(ct).ConfigureAwait(false);
        var songs=new List<Song>();var seen=new HashSet<string>(StringComparer.Ordinal);
        for(int batch=0;batch<4 && songs.Count<20;batch++)
        {
            System.Text.Json.JsonElement data;
            try
            {
                data=await RpcAsync("music.radioProxy.MbTrackRadioSvr","get_radio_track",new{},session,ct,
                    new Dictionary<string,object?>{["ct"]=19,["cv"]=0,["uin"]=string.IsNullOrEmpty(session.QqUin)?session.Uin:session.QqUin}).ConfigureAwait(false);
            }
            catch(QQMusicException error) when(error.Code==700000)
            {
                data=await RpcAsync("music.radioProxy.MbTrackRadioSvr","get_radio_track",new{id=99,num=5,@from=0,scene=0,song_ids=Array.Empty<long>()},session,ct,
                    new Dictionary<string,object?>{["ct"]=24,["cv"]=4747474,["platform"]="yqq.json",["uin"]=session.Uin}).ConfigureAwait(false);
            }
            int before=songs.Count;
            foreach(var item in Items(At(data,"tracks")))
            {
                var song=ParseSong(item);
                if(song!=null && seen.Add(song.Mid))songs.Add(song);
            }
            if(songs.Count==before)break;
        }
        EnsureCurrent(session);
        return songs.Take(20).ToArray();
    }

    public async Task<IReadOnlyList<Song>> SetSongInPlaylistAsync(Song song,Playlist playlist,bool add,CancellationToken ct)
    {
        if(song.LocalPath!=null)throw new QQMusicException("本地文件暂不能添加到 QQ 在线歌单。");
        int directory=playlist.IsLiked?201:playlist.DirectoryId;
        if(!playlist.IsLiked && (!playlist.Writable || directory<=0))throw new QQMusicException("只能添加到当前账号创建的歌单。");
        await _playlistWriteLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var session=await GetAccountAsync(ct).ConfigureAwait(false);
            long songId=song.Id;
            if(songId<=0)
            {
                var detail=await RpcAsync("music.pf_song_detail_svr","get_song_detail_yqq",new{song_mid=song.Mid},session,ct).ConfigureAwait(false);
                long.TryParse(Text(At(detail,"track_info"),"id","songid"),out songId);
            }
            if(songId<=0)throw new QQMusicException("歌曲缺少可写入歌单的编号。");
            EnsureCurrent(session);ct.ThrowIfCancellationRequested();
            long.TryParse(playlist.Id,out long tid);
            var data=await WritePlaylistRequestAsync(session,add?"AddSonglist":"DelSonglist",new{dirId=directory,tid,bFmtUtf8=true,v_songInfo=new[]{new{songId,songType=0}}},ct).ConfigureAwait(false);
            if(At(data,"retCode").ValueKind==System.Text.Json.JsonValueKind.Undefined || Number(data,"retCode")!=0)
                throw new QQMusicException("QQ 未确认歌单操作成功，请重新展开查看状态。",Number(data,"retCode"));
            EnsureCurrent(session);
            var actual=await GetPlaylistSongsAsync(playlist,ct).ConfigureAwait(false);
            if(actual.Any(s=>s.Mid==song.Mid)!=add)throw new QQMusicException("操作已提交，重新读取时尚未确认结果，请重新展开歌单查看。");
            return actual;
        }
        finally{_playlistWriteLock.Release();}
    }
}
