using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace FeatherPlayer;

// The patched WPF media pipeline can reject Internet-zone URLs before decoding.
// Fetch an authorized media URL with HttpClient, then give WPF a local file.
internal sealed class RemoteAudioFile : IDisposable
{
    private const long MaxBytes = 512L * 1024 * 1024;
    private static readonly string DirectoryPath = System.IO.Path.Combine(AppPaths.DataDirectory, "AudioTemp");
    internal string Path { get; }
    private RemoteAudioFile(string path) => Path = path;

    internal static async Task<RemoteAudioFile> DownloadAsync(Uri address, CancellationToken ct)
    {
        Directory.CreateDirectory(DirectoryPath);
        // Only our own stale temporary audio files are eligible for cleanup.
        foreach (string stale in Directory.EnumerateFiles(DirectoryPath, "playback-*"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(stale) < DateTime.UtcNow.AddDays(-1)) File.Delete(stale);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        string extension = System.IO.Path.GetExtension(address.AbsolutePath).ToLowerInvariant();
        if (extension is not (".mp3" or ".m4a" or ".aac" or ".flac" or ".wav" or ".ogg" or ".mp4")) extension = ".mp3";
        var temporary = new RemoteAudioFile(System.IO.Path.Combine(DirectoryPath, "playback-" + Guid.NewGuid().ToString("N") + extension));
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(90));
            using var handler = new SocketsHttpHandler
            {
                UseCookies = false,
                Credentials = null,
                AutomaticDecompression = DecompressionMethods.All,
                ConnectTimeout = TimeSpan.FromSeconds(12),
                MaxAutomaticRedirections = 5
            };
            using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            using var request = new HttpRequestMessage(HttpMethod.Get, address);
            request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/130.0.0.0 Safari/537.36");
            if (address.Host.EndsWith(".qq.com", StringComparison.OrdinalIgnoreCase))
                request.Headers.Referrer = new Uri("https://y.qq.com/");
            else if (address.Host.EndsWith(".126.net", StringComparison.OrdinalIgnoreCase) || address.Host.EndsWith(".163.com", StringComparison.OrdinalIgnoreCase))
                request.Headers.Referrer = new Uri("https://music.163.com/");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException("音频服务器返回 HTTP " + (int)response.StatusCode + "，请重新获取播放地址。", null, response.StatusCode);
            if (response.Content.Headers.ContentLength > MaxBytes)
                throw new IOException("该音频超过兼容播放缓存的 512 MB 上限。");
            string type = response.Content.Headers.ContentType?.MediaType ?? "";
            if (type.Contains("html", StringComparison.OrdinalIgnoreCase) || type.Contains("json", StringComparison.OrdinalIgnoreCase) || type.Contains("xml", StringComparison.OrdinalIgnoreCase))
                throw new IOException("音频服务器返回了网页或错误信息，请重新获取播放地址。");
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            await using var output = new FileStream(temporary.Path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[65536];
            long total = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > MaxBytes) throw new IOException("该音频超过兼容播放缓存的 512 MB 上限。");
                await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
            }
            if (total == 0) throw new IOException("音频服务器返回了空文件，请重新获取播放地址。");
            await output.FlushAsync(timeout.Token).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return temporary;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            temporary.Dispose();
            throw new TimeoutException("音频缓存超时，请检查网络后重试。");
        }
        catch
        {
            temporary.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        try { File.Delete(Path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
