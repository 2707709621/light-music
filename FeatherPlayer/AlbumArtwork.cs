using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MaterialColorUtilities.Utils;

namespace FeatherPlayer;

internal sealed record AlbumImage(BitmapSource Image, Color Dominant);

internal sealed class AlbumArtwork : IDisposable
{
    private readonly HttpClient _http = new(new SocketsHttpHandler { UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(6) })
    { Timeout = TimeSpan.FromSeconds(12), MaxResponseContentBufferSize = 2 * 1024 * 1024 };
    private readonly Dictionary<string, AlbumImage> _cache = new();
    private readonly Queue<string> _order = new();

    internal async Task<AlbumImage?> ReadAsync(Song song, CancellationToken ct)
    {
        string url = song.CoverUrl;
        if (url.Length == 0 && song.Provider == MusicProvider.Qq && Regex.IsMatch(song.AlbumMid, "^[A-Za-z0-9]{10,30}$"))
            url = "https://y.gtimg.cn/music/photo_new/T002R300x300M000" + song.AlbumMid + ".jpg";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var address) || address.Scheme is not ("https" or "http")) return null;
        if (address.Scheme == "http") address = new UriBuilder(address) { Scheme = "https", Port = -1 }.Uri;
        string key = address.AbsoluteUri;
        if (_cache.TryGetValue(key, out var cached)) return cached;
        byte[] bytes = await _http.GetByteArrayAsync(address, ct);
        var artwork = await Task.Run(() => Decode(bytes), ct);
        ct.ThrowIfCancellationRequested();
        if (_cache.TryGetValue(key, out cached)) return cached;
        while (_cache.Count >= 8) _cache.Remove(_order.Dequeue());
        _cache[key] = artwork;
        _order.Enqueue(key);
        return artwork;
    }

    private static AlbumImage Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable:false);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.DecodePixelWidth = 128;
        bitmap.DecodePixelHeight = 128;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        var pixels = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        int stride = pixels.PixelWidth * 4;
        var data = new byte[stride * pixels.PixelHeight];
        pixels.CopyPixels(data, stride, 0);
        var samples = new List<uint>();
        for (int y = 0; y < pixels.PixelHeight; y += 2)
        for (int x = 0; x < pixels.PixelWidth; x += 2)
        {
            int index = y * stride + x * 4;
            if (data[index + 3] < 128) continue;
            byte r = data[index + 2], g = data[index + 1], b = data[index];
            samples.Add(0xff000000u | (uint)r << 16 | (uint)g << 8 | b);
        }
        uint seed = samples.Count == 0 ? 0xff647487u : ImageUtils.ColorsFromImage(samples.ToArray())[0];
        var color = Color.FromRgb((byte)(seed >> 16),(byte)(seed >> 8),(byte)seed);
        return new(bitmap, color);
    }

    public void Dispose() { _http.Dispose(); _cache.Clear(); _order.Clear(); }
}
