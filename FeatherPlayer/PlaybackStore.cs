using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace FeatherPlayer;

internal sealed record PlaybackBookmark(Song Song, double Position, bool WasPlaying, string AccountId, string QueueRevision);
internal sealed record RestoredPlayback(PlaybackBookmark Bookmark, IReadOnlyList<Song> Queue);

// Small position checkpoints and the potentially large queue are written separately.
// No playback URLs, cookies or account keys are persisted here.
internal sealed class PlaybackStore
{
    private sealed record SavedQueue(string Revision, string AccountId, Song[] Songs);
    private readonly string _directory;
    private IReadOnlyList<Song>? _savedQueue;
    private string _revision="", _queueAccount="";
    private string BookmarkFile => Path.Combine(_directory,"playback.json");
    private string QueueFile => Path.Combine(_directory,"playback-queue.json");
    internal PlaybackStore(string? directory=null) => _directory=directory ?? AppPaths.DataDirectory;
    internal RestoredPlayback? Load()
    {
        try
        {
            if(!File.Exists(BookmarkFile) || new FileInfo(BookmarkFile).Length>65536)return null;
            var bookmark=JsonSerializer.Deserialize<PlaybackBookmark>(File.ReadAllText(BookmarkFile));
            if(bookmark==null || !Valid(bookmark.Song) || !double.IsFinite(bookmark.Position))return null;
            bookmark=bookmark with {Position=Math.Max(0,bookmark.Position),AccountId=bookmark.AccountId ?? ""};
            IReadOnlyList<Song> songs=new[]{bookmark.Song};
            if(File.Exists(QueueFile) && new FileInfo(QueueFile).Length<32*1024*1024)
            {
                try
                {
                    var queue=JsonSerializer.Deserialize<SavedQueue>(File.ReadAllText(QueueFile));
                    if(queue?.Revision==bookmark.QueueRevision && queue.AccountId==bookmark.AccountId && queue.Songs is {Length:>0 and <=20000})
                        songs=queue.Songs.Where(Valid).ToArray();
                }
                catch { /* A mismatched/interrupted queue write must not lose the saved song. */ }
            }
            if(!songs.Any(s=>s.Mid==bookmark.Song.Mid))songs=new[]{bookmark.Song};
            _savedQueue=songs;_revision=bookmark.QueueRevision;_queueAccount=bookmark.AccountId;
            return new(bookmark,songs);
        }
        catch { return null; }
    }
    internal void Save(Song song,double position,bool playing,string accountId,IReadOnlyList<Song> queue)
    {
        if(!Valid(song) || !double.IsFinite(position))return;
        Directory.CreateDirectory(_directory);
        if(!ReferenceEquals(_savedQueue,queue) || _queueAccount!=accountId)
        {
            string revision=Guid.NewGuid().ToString("N");
            var songs=queue.Count==0?new[]{song}:queue.Where(Valid).Take(20000).ToArray();
            AtomicWrite(QueueFile,new SavedQueue(revision,accountId,songs));
            _savedQueue=queue;_revision=revision;_queueAccount=accountId;
        }
        AtomicWrite(BookmarkFile,new PlaybackBookmark(song,Math.Max(0,position),playing,accountId,_revision));
    }
    internal void Clear()
    {
        foreach(string file in new[]{BookmarkFile,QueueFile})if(File.Exists(file))File.Delete(file);
        _savedQueue=null;_revision="";_queueAccount="";
    }
    internal static double ClampPosition(double position,double duration) =>
        !double.IsFinite(position) || !double.IsFinite(duration) || duration<=0 ? 0 : Math.Clamp(position,0,Math.Max(0,duration-.05));
    private static bool Valid(Song? song) => song!=null && !string.IsNullOrWhiteSpace(song.Mid) && song.Title!=null &&
        (song.LocalPath==null || Path.IsPathFullyQualified(song.LocalPath));
    private static void AtomicWrite<T>(string path,T value)
    {
        string temporary=path+".tmp";
        File.WriteAllText(temporary,JsonSerializer.Serialize(value));
        File.Move(temporary,path,true);
    }
}
