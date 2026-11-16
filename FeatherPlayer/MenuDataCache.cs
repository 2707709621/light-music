using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FeatherPlayer;

internal sealed class MenuDataCache
{
    private sealed class Entry
    {
        internal object? Value;
        internal string Fingerprint="";
        internal DateTimeOffset NextCheck;
        internal DateTimeOffset Used=DateTimeOffset.UtcNow;
        internal int Revision;
        internal Task<object?>? Pending;
        internal Exception? Error;
        internal int Bytes;
    }
    private sealed record Stored(DateTimeOffset SavedAt,JsonElement Value);
    private readonly Dictionary<string,Entry> _entries=new();
    private readonly object _gate=new();
    private static string Hash(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..24];
    private static string FilePath(string account,string key)=>Path.Combine(AppPaths.DataDirectory,"MenuCache",Hash(account),Hash(key)+".json");

    internal async Task<T> GetAsync<T>(string account,string key,TimeSpan interval,Func<CancellationToken,Task<T>> load,CancellationToken caller,CancellationToken lifetime) where T:class
    {
        Entry entry;
        Task<object?>? pending;
        lock(_gate)
        {
            string identity=account+"|"+key;
            if(!_entries.TryGetValue(identity,out entry!))
            {
                entry=new Entry();_entries[identity]=entry;
                try
                {
                    string path=FilePath(account,key);
                    if(File.Exists(path) && new FileInfo(path).Length<8*1024*1024)
                    {
                        var stored=JsonSerializer.Deserialize<Stored>(File.ReadAllText(path));
                        if(stored!=null){entry.Value=stored.Value.Deserialize<T>();entry.NextCheck=stored.SavedAt+interval;entry.Fingerprint=Hash(stored.Value.GetRawText());entry.Bytes=Encoding.UTF8.GetByteCount(stored.Value.GetRawText());}
                    }
                }
                catch { }
                foreach(var old in _entries.Where(p=>p.Key!=identity && p.Value.Pending?.IsCompleted!=false).OrderBy(p=>p.Value.Used).Take(Math.Max(0,_entries.Count-32)).ToArray())_entries.Remove(old.Key);
            }
            entry.Used=DateTimeOffset.UtcNow;
            if(entry.Pending?.IsCompleted!=false && entry.Used>=entry.NextCheck)
                entry.Pending=RefreshAsync(entry,account,key,interval,load,lifetime,entry.Revision);
            if(entry.Value is T cached)return cached;
            pending=entry.Pending;
        }
        if(pending!=null)await pending.WaitAsync(caller);
        caller.ThrowIfCancellationRequested();lifetime.ThrowIfCancellationRequested();
        lock(_gate)return entry.Value as T ?? throw entry.Error ?? new QQMusicException("暂时无法读取数据，请稍后重新展开。");
    }
    private async Task<object?> RefreshAsync<T>(Entry entry,string account,string key,TimeSpan interval,Func<CancellationToken,Task<T>> load,CancellationToken token,int revision) where T:class
    {
        try
        {
            T value=await load(token).ConfigureAwait(false);token.ThrowIfCancellationRequested();
            lock(_gate)
            {
                if(entry.Revision==revision)Store(entry,account,key,value,interval);
                return entry.Value;
            }
        }
        catch(Exception error)
        {
            lock(_gate)
            {
                if(entry.Revision==revision){entry.Error=error;entry.NextCheck=DateTimeOffset.UtcNow.AddSeconds(30);}
                return entry.Value;
            }
        }
    }
    internal void Set<T>(string account,string key,T value,TimeSpan interval) where T:class
    {
        lock(_gate)
        {
            string identity=account+"|"+key;
            if(!_entries.TryGetValue(identity,out var entry))_entries[identity]=entry=new Entry();
            entry.Revision++;Store(entry,account,key,value,interval);
        }
    }
    internal void Invalidate(string account,string key)
    {
        lock(_gate)
        {
            if(_entries.TryGetValue(account+"|"+key,out var entry)){entry.Revision++;entry.Value=null;entry.NextCheck=DateTimeOffset.MinValue;entry.Pending=null;}
            try{File.Delete(FilePath(account,key));}catch { }
        }
    }
    internal T? Peek<T>(string account,string key) where T:class
    {
        lock(_gate)return _entries.TryGetValue(account+"|"+key,out var entry)?entry.Value as T:null;
    }
    private void Store<T>(Entry entry,string account,string key,T value,TimeSpan interval) where T:class
    {
        string json=JsonSerializer.Serialize(value);string fingerprint=Hash(json);
        entry.Error=null;entry.NextCheck=DateTimeOffset.UtcNow+interval;
        if(entry.Value!=null && entry.Fingerprint==fingerprint)return;
        entry.Value=value;entry.Fingerprint=fingerprint;
        entry.Bytes=Encoding.UTF8.GetByteCount(json);
        foreach(var old in _entries.Where(p=>!ReferenceEquals(p.Value,entry) && p.Value.Pending?.IsCompleted!=false).OrderBy(p=>p.Value.Used).ToArray())
        {
            if(_entries.Count<=24 && _entries.Values.Sum(e=>(long)e.Bytes)<=12*1024*1024)break;
            _entries.Remove(old.Key);
        }
        try
        {
            string path=FilePath(account,key);string directory=Path.GetDirectoryName(path)!;Directory.CreateDirectory(directory);
            if(Encoding.UTF8.GetByteCount(json)>8*1024*1024)return;
            string temporary=path+".tmp";
            File.WriteAllText(temporary,JsonSerializer.Serialize(new Stored(DateTimeOffset.UtcNow,JsonSerializer.SerializeToElement(value))));File.Move(temporary,path,true);
            foreach(var old in Directory.EnumerateFiles(directory,"*.json").OrderByDescending(File.GetLastWriteTimeUtc).Skip(32))File.Delete(old);
        }
        catch { }
    }
}
