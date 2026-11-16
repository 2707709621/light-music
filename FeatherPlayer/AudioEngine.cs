using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace FeatherPlayer;

/// <summary>
/// A dispatcher-owned wrapper around the Windows media pipeline. It has no polling timer.
/// A fresh player per source prevents delayed native events from an old source affecting a new one.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private PlaybackSession? _session;
    private bool _disposed;
    private double _volume = 0.65;
    private CancellationTokenSource? _openRequest;
    private bool _needsLocalMedia;
    public string PlaybackTransport { get; private set; } = "none";

    public event Action? Changed;
    public event Action? Ended;
    public event Action<string>? Error;
    public bool IsPlaying { get; private set; }
    public bool IsOpening { get; private set; }
    public double Position => _session is { Opened: true } s ? Math.Max(0, s.Player.Position.TotalSeconds) : 0;
    public double Duration => _session is { Opened: true } s && s.Player.NaturalDuration.HasTimeSpan
        ? Math.Max(0, s.Player.NaturalDuration.TimeSpan.TotalSeconds) : 0;

    public double Volume
    {
        get => _volume;
        set
        {
            Verify();
            if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
            _volume = Math.Clamp(value, 0, 1);
            if (_session is { } s) s.Player.Volume = _volume;
        }
    }

    /// <summary>Open, wait for decoding metadata, and remain paused. Call Play explicitly afterwards.</summary>
    public async Task OpenAsync(string uriOrPath, CancellationToken cancellationToken = default)
    {
        Verify();
        cancellationToken.ThrowIfCancellationRequested();
        Uri source = ValidateSource(uriOrPath);
        _openRequest?.Cancel();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _openRequest = request;
        RemoteAudioFile? file = null;
        try
        {
            bool remote = source.Scheme is "http" or "https";
            if (!remote || !_needsLocalMedia)
            {
                try
                {
                    PlaybackTransport = remote ? "direct-http" : "local-file";
                    await OpenCoreAsync(source, request.Token);
                    return;
                }
                catch (InvalidOperationException error) when (remote && CanRetryAsLocalFile(error))
                {
                    request.Token.ThrowIfCancellationRequested();
                    _needsLocalMedia = true;
                }
            }
            PlaybackTransport = "managed-download";
            ReleaseCurrent();
            IsOpening = true;
            Changed?.Invoke();
            file = await RemoteAudioFile.DownloadAsync(source, request.Token);
            request.Token.ThrowIfCancellationRequested();
            await OpenCoreAsync(new Uri(file.Path), request.Token);
            request.Token.ThrowIfCancellationRequested();
            if (_session == null) throw new InvalidOperationException("音频在缓存打开后已关闭，请重试。");
            _session.RemoteFile = file;
            file = null;
        }
        finally
        {
            file?.Dispose();
            if (ReferenceEquals(_openRequest, request))
            {
                _openRequest = null;
                if (_session == null)
                {
                    IsOpening = false;
                    Changed?.Invoke();
                }
            }
        }
    }

    private static bool CanRetryAsLocalFile(Exception error)
    {
        // WPF localizes "pack URI" as "包 URI" in Chinese. Its network gate
        // reports the generic InvalidOperationException code, so try the same
        // audio once as a local file without depending on translated wording.
        for (Exception? current = error; current != null; current = current.InnerException)
            if (current is InvalidOperationException && current.HResult == unchecked((int)0x80131509)) return true;
        return false;
    }

    private async Task OpenCoreAsync(Uri source, CancellationToken cancellationToken)
    {
        Verify();
        cancellationToken.ThrowIfCancellationRequested();
        ReleaseCurrent();
        var session = new PlaybackSession(new MediaPlayer { Volume = _volume });
        _session = session;
        IsOpening = true;
        IsPlaying = false;

        session.OpenHandler = (_, _) =>
        {
            if (_session != session || !IsOpening) return;
            session.Opened = true;
            IsOpening = false;
            session.Player.Pause();
            session.Completion.TrySetResult();
            Changed?.Invoke();
        };
        session.FailedHandler = (_, args) =>
        {
            if (_session != session) return;
            string message = (source.Scheme is "http" or "https") && CanRetryAsLocalFile(args.ErrorException)
                ? "网络音频打开失败，正在切换兼容播放。"
                : "Windows 无法解码或读取这段音频（错误码 0x" + args.ErrorException.HResult.ToString("X8") + "）。";
            session.Completion.TrySetException(new InvalidOperationException(message,args.ErrorException));
            ReleaseCurrent();
            Changed?.Invoke();
            Error?.Invoke(message);
        };
        session.EndHandler = (_, _) =>
        {
            if (_session != session) return;
            session.HasEnded = true;
            IsPlaying = false;
            Changed?.Invoke();
            Ended?.Invoke();
        };
        session.Player.MediaOpened += session.OpenHandler;
        session.Player.MediaFailed += session.FailedHandler;
        session.Player.MediaEnded += session.EndHandler;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        using var registration = timeout.Token.Register(() =>
        {
            void CancelOnDispatcher()
            {
                if (_session != session || session.Completion.Task.IsCompleted) return;
                if (cancellationToken.IsCancellationRequested)
                    session.Completion.TrySetCanceled(cancellationToken);
                else
                    session.Completion.TrySetException(new TimeoutException("等待音频打开超时，请检查网络后重试。"));
                ReleaseCurrent();
                Changed?.Invoke();
            }
            if (_dispatcher.CheckAccess()) CancelOnDispatcher();
            else if (!_dispatcher.HasShutdownStarted)
                _dispatcher.BeginInvoke(DispatcherPriority.Normal, (Action)CancelOnDispatcher);
        });
        try
        {
            session.Player.Open(source);
            Changed?.Invoke();
            await session.Completion.Task;
        }
        catch
        {
            if (_session == session)
            {
                ReleaseCurrent();
                Changed?.Invoke();
            }
            throw;
        }
    }

    public void Toggle()
    {
        Verify();
        if (IsPlaying) Pause(); else Play();
    }

    public void Play()
    {
        Verify();
        if (_session is not { Opened: true } s || IsOpening) return;
        if (s.HasEnded)
        {
            s.Player.Position = TimeSpan.Zero;
            s.HasEnded = false;
        }
        s.Player.Play();
        IsPlaying = true;
        Changed?.Invoke();
    }

    public void Pause()
    {
        Verify();
        if (_session is not { Opened: true } s) return;
        s.Player.Pause();
        IsPlaying = false;
        Changed?.Invoke();
    }

    public void Seek(double seconds)
    {
        Verify();
        if (!double.IsFinite(seconds)) throw new ArgumentOutOfRangeException(nameof(seconds));
        if (_session is not { Opened: true } s || IsOpening) return;
        double duration = Duration;
        s.Player.Position = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, duration > 0 ? duration : double.MaxValue));
        s.HasEnded = false;
        Changed?.Invoke();
    }

    public void Close()
    {
        Verify();
        _openRequest?.Cancel();
        ReleaseCurrent();
        Changed?.Invoke();
    }

    public void Dispose()
    {
        _dispatcher.VerifyAccess();
        if (_disposed) return;
        _openRequest?.Cancel();
        ReleaseCurrent();
        _disposed = true;
    }

    private void Verify()
    {
        _dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private void ReleaseCurrent()
    {
        var old = _session;
        _session = null;
        IsPlaying = false;
        IsOpening = false;
        if (old is null) return;
        old.Player.MediaOpened -= old.OpenHandler;
        old.Player.MediaFailed -= old.FailedHandler;
        old.Player.MediaEnded -= old.EndHandler;
        old.Completion.TrySetCanceled();
        old.Player.Close();
        old.RemoteFile?.Dispose();
    }

    private static Uri ValidateSource(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) throw new ArgumentException("音频地址不能为空。", nameof(source));
        if (Path.IsPathFullyQualified(source))
        {
            string path = Path.GetFullPath(source);
            if (!File.Exists(path)) throw new FileNotFoundException("找不到指定的本地音频文件。");
            return new Uri(path, UriKind.Absolute);
        }
        if (Uri.TryCreate(source, UriKind.Absolute, out Uri? uri) &&
            (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) &&
            string.IsNullOrEmpty(uri.UserInfo)) return uri;
        throw new ArgumentException("只支持 HTTP(S) 音频地址或明确的本地文件路径。", nameof(source));
    }

    private sealed class PlaybackSession(MediaPlayer player)
    {
        internal MediaPlayer Player { get; } = player;
        internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Opened;
        internal bool HasEnded;
        internal RemoteAudioFile? RemoteFile;
        internal EventHandler? OpenHandler;
        internal EventHandler<ExceptionEventArgs>? FailedHandler;
        internal EventHandler? EndHandler;
    }
}

/// <summary>LRC timeline: stable ordering and an allocation-free upper-bound lookup during playback.</summary>
public static partial class LyricTimeline
{
    [GeneratedRegex(@"\[(\d{1,4}):([0-5]?\d)(?:[.:](\d{1,3}))?\]", RegexOptions.CultureInvariant)]
    private static partial Regex TimestampRegex();
    [GeneratedRegex(@"\[offset\s*:\s*([+-]?\d+)\s*\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OffsetRegex();
    [GeneratedRegex(@"<\d{1,4}:[0-5]?\d(?:[.:]\d{1,3})?>", RegexOptions.CultureInvariant)]
    private static partial Regex InlineTimestampRegex();

    public static IReadOnlyList<LyricLine> Parse(string? lrc)
    {
        if (string.IsNullOrWhiteSpace(lrc)) return Array.Empty<LyricLine>();
        lrc = WebUtility.HtmlDecode(lrc);
        double offset = 0;
        var offsetMatch = OffsetRegex().Match(lrc);
        if (offsetMatch.Success && double.TryParse(offsetMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out double ms))
            offset = ms / 1000d;
        var result = new List<LyricLine>();
        foreach (string rawLine in lrc.Split('\n'))
        {
            string line = rawLine.Trim();
            MatchCollection stamps = TimestampRegex().Matches(line);
            if (stamps.Count == 0) continue;
            var last = stamps[^1];
            string text = InlineTimestampRegex().Replace(line[(last.Index + last.Length)..], "").Trim();
            foreach (Match stamp in stamps)
            {
                double minutes = double.Parse(stamp.Groups[1].Value, CultureInfo.InvariantCulture);
                double seconds = double.Parse(stamp.Groups[2].Value, CultureInfo.InvariantCulture);
                string fraction = stamp.Groups[3].Value;
                double subseconds = fraction.Length > 0 ? double.Parse(fraction, CultureInfo.InvariantCulture) / Math.Pow(10, fraction.Length) : 0;
                // LRC positive offset means the lyric is displayed earlier than its timestamp.
                result.Add(new LyricLine(Math.Max(0, minutes * 60 + seconds + subseconds - offset), text));
            }
        }
        // OrderBy is stable: equal timestamps retain file order; FindIndex selects the last one.
        return result.OrderBy(l => l.Seconds).ToArray();
    }

    public static int FindIndex(IReadOnlyList<LyricLine> lines, double seconds)
    {
        if (!double.IsFinite(seconds)) return -1;
        int low = 0, high = lines.Count;
        while (low < high)
        {
            int mid = low + ((high - low) >> 1);
            if (lines[mid].Seconds <= seconds) low = mid + 1;
            else high = mid;
        }
        return low - 1;
    }
}
