using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace FeatherPlayer;

public enum MusicProvider { Qq, Netease, Kugou }
public enum AudioQuality { Auto, Standard, High, Lossless, HiRes }
public sealed record PlaybackSource(string Url, string QualityLabel);

internal static class AudioQualityNames
{
    internal static string Name(AudioQuality quality) => quality switch
    {
        AudioQuality.Standard => "标准",
        AudioQuality.High => "高品质",
        AudioQuality.Lossless => "无损 FLAC",
        AudioQuality.HiRes => "Hi-Res",
        _ => "自动"
    };
}

public sealed record Song(string Mid, string Title, string Artist, string Album, int DurationSeconds, string AlbumMid = "", string MediaMid = "", long Id = 0, string? LocalPath = null, MusicProvider Provider = MusicProvider.Qq, string CoverUrl = "", string PlaylistFileId = "")
{
    public string DurationText => DurationSeconds <= 0 ? "—" : TimeSpan.FromSeconds(DurationSeconds).ToString(@"m\:ss", CultureInfo.InvariantCulture);
    public string Subtitle => string.IsNullOrWhiteSpace(Album) ? Artist : Artist + " · " + Album;
}
public sealed record Playlist(string Id, string Name, int Count = 0, string CoverUrl = "", bool IsLiked = false, int DirectoryId=0,bool Writable=false, MusicProvider Provider = MusicProvider.Qq);
public sealed record AccountSession(string Uin, string MusicKey, string Nickname, string Cookie, string RefreshKey = "", string RefreshToken = "", long ExpiresAt = 0, string OpenId = "", string AccessToken = "", string EncryptUin = "", int LoginType = 2, string QqUin = "", MusicProvider Provider = MusicProvider.Qq);
public sealed record QrLogin(byte[] Image, string Signature, DateTimeOffset CreatedAt, string Cookie = "");
public sealed record QrLoginResult(string State, string Message, AccountSession? Session = null);
public sealed record LyricLine(double Seconds, string Text);
public sealed record PlaybackState(Song? Song, bool IsPlaying, string Lyric, double Position, double Duration, double LineStart = 0, double LineEnd = 0, System.Windows.Media.ImageSource? Cover = null);

public static class AppPaths
{
    internal static string? DiagnosticDirectory { get; set; }
    public static string DataDirectory => DiagnosticDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FeatherPlayer");
    public static string SessionFile => Path.Combine(DataDirectory, "session.dpapi");
}
