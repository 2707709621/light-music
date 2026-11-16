using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace FeatherPlayer;

/// <summary>Offline diagnostics. Never reads account data or contacts QQ Music.</summary>
public static class SelfTests
{
    public static int TestCore()
    {
        var checks = CollectCore();
        int failures = 0;
        foreach (var check in checks) if (!check.Passed) failures++;
        return failures;
    }

    /// <summary>Call from the WPF dispatcher. A running dispatcher is required for MediaPlayer events.</summary>
    public static async Task<int> RunAsync(string outputDirectory)
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("音频自检必须在 WPF STA 线程中运行。");
        string directory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(directory);
        var checks = CollectCore();
        var stopwatch = Stopwatch.StartNew();
        string fixture = Path.Combine(directory, "audio-fixture.wav");
        WriteSilentWav(fixture, 3.2);
        File.WriteAllText(Path.Combine(directory, "audio-fixture.lrc"),
            "[00:00.00]本地音频诊断\n[00:01.00]暂停、拖动与切歌\n[00:02.00]任务栏歌词测试", Encoding.UTF8);

        using var audio = new AudioEngine { Volume = 0 };
        string? nativeError = null;
        audio.Error += message => nativeError = message;
        try
        {
            await audio.OpenAsync(fixture, CancellationToken.None);
            checks.Add(new("media_opened", !audio.IsOpening && !audio.IsPlaying,
                "MediaOpened 完成后为暂停状态。"));
            checks.Add(new("duration", Math.Abs(audio.Duration - 3.2) < 0.15,
                $"读取时长 {audio.Duration:F3} 秒，预期 3.2 秒。"));

            audio.Play();
            await Task.Delay(700);
            checks.Add(new("play_position", audio.IsPlaying && audio.Position > 0.25,
                $"播放 700 ms 后位置 {audio.Position:F3} 秒。"));

            audio.Pause();
            await Task.Delay(100);
            double pausedPosition = audio.Position;
            await Task.Delay(300);
            checks.Add(new("pause_position", !audio.IsPlaying && Math.Abs(audio.Position - pausedPosition) < 0.1,
                $"暂停后位置变化 {audio.Position - pausedPosition:F3} 秒。"));

            audio.Seek(1.5);
            await Task.Delay(100);
            checks.Add(new("seek", Math.Abs(audio.Position - 1.5) < 0.15,
                $"跳转目标 1.500 秒，实际 {audio.Position:F3} 秒。"));

            bool ended = false;
            audio.Ended += () => ended = true;
            audio.Seek(2.8);
            audio.Play();
            for (int i = 0; i < 25 && !ended; i++) await Task.Delay(100);
            checks.Add(new("media_ended", ended && !audio.IsPlaying,
                "末尾触发 MediaEnded，播放状态同步为暂停。"));

            audio.Play();
            await Task.Delay(250);
            checks.Add(new("replay_after_end", audio.IsPlaying && audio.Position < 1.0,
                $"结束后重新播放位置 {audio.Position:F3} 秒。"));
            audio.Close();
            checks.Add(new("close", !audio.IsPlaying && !audio.IsOpening && audio.Position == 0,
                "关闭音源后播放状态与位置复位。"));

            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel();
                bool sawCancellation = false;
                try { await audio.OpenAsync(fixture, canceled.Token); }
                catch (OperationCanceledException) { sawCancellation = true; }
                checks.Add(new("pre_canceled_open", sawCancellation && !audio.IsOpening,
                    "已取消的打开请求不触碰播放状态。"));
            }

            using (var inFlightCancellation = new CancellationTokenSource())
            {
                Task opening = audio.OpenAsync(fixture, inFlightCancellation.Token);
                inFlightCancellation.Cancel();
                bool inFlightCanceled = false;
                try { await opening; } catch (OperationCanceledException) { inFlightCanceled = true; }
                checks.Add(new("inflight_canceled_open", inFlightCanceled && !audio.IsOpening && audio.Duration == 0,
                    "打开过程中取消会关闭对应播放器并完成等待任务。"));
            }

            using (var oldCancellation = new CancellationTokenSource())
            {
                Task oldOpen = audio.OpenAsync(fixture, oldCancellation.Token);
                Task newOpen = audio.OpenAsync(fixture, CancellationToken.None);
                oldCancellation.Cancel();
                bool oldCanceled = false;
                try { await oldOpen; } catch (OperationCanceledException) { oldCanceled = true; }
                await newOpen;
                checks.Add(new("rapid_source_replacement", oldCanceled && !audio.IsOpening && audio.Duration > 3,
                    "快速切换音源取消旧请求，旧取消回调与媒体事件不影响新请求。"));
            }

            Task pendingOpen = audio.OpenAsync(fixture, CancellationToken.None);
            audio.Close();
            bool closeCanceled = false;
            try { await pendingOpen; } catch (OperationCanceledException) { closeCanceled = true; }
            checks.Add(new("close_cancels_open", closeCanceled && !audio.IsOpening,
                "关闭时完成挂起的打开任务，不遗留等待。"));

            foreach (string rejected in new[] { "file:///C:/Windows/win.ini", "javascript:alert(1)", "data:audio/wav;base64,AAAA" })
            {
                bool rejectedSource = false;
                try { await audio.OpenAsync(rejected, CancellationToken.None); }
                catch (ArgumentException) { rejectedSource = true; }
                checks.Add(new("source_scheme_" + rejected.Split(':')[0], rejectedSource,
                    "网络来源仅允许 HTTP(S)，本地音频必须使用明确文件路径。"));
            }
        }
        catch (Exception error)
        {
            checks.Add(new("audio_pipeline", false, nativeError ?? error.GetType().Name + ": " + error.Message));
        }
        stopwatch.Stop();
        int failed = 0;
        foreach (var check in checks) if (!check.Passed) failed++;
        var report = new
        {
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Platform = Environment.OSVersion.VersionString,
            Framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            Offline = true,
            AudioMuted = true,
            ElapsedMs = stopwatch.ElapsedMilliseconds,
            Passed = checks.Count - failed,
            Failed = failed,
            Checks = checks
        };
        await File.WriteAllTextAsync(Path.Combine(directory, "self-test.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
        return failed == 0 ? 0 : 1;
    }

    private static List<Check> CollectCore()
    {
        var result = new List<Check>();
        void CheckThat(string name, bool condition, string description) => result.Add(new Check(name, condition, description));
        var lines = LyricTimeline.Parse("[ar:测试歌手]\n[00:03.500]第三行\n[00:01.20][00:02.200]前两行 &amp; 歌词\n[00:03.500]同时间的末行\n[00:04.00]\n[00:05.00]<00:05.00>逐<00:05.20>字");
        CheckThat("lrc_count", lines.Count == 6, "多时间戳展开，元信息不加入时间轴。" );
        CheckThat("lrc_sorted", lines.Count > 0 && Math.Abs(lines[0].Seconds - 1.2) < 0.0001, "时间戳排序并解析小数秒。" );
        CheckThat("lrc_html", lines.Count > 0 && lines[0].Text == "前两行 & 歌词", "解码 HTML 实体。" );
        CheckThat("lrc_before_start", LyricTimeline.FindIndex(lines, 0) == -1, "歌词开始前返回 -1。" );
        CheckThat("lrc_exact_boundary", LyricTimeline.FindIndex(lines, 1.2) == 0, "精确边界显示该行。" );
        CheckThat("lrc_between_lines", LyricTimeline.FindIndex(lines, 2.8) == 1, "二分查找获取最近已到达的歌词。" );
        int duplicateIndex = LyricTimeline.FindIndex(lines, 3.5);
        CheckThat("lrc_equal_timestamps", duplicateIndex >= 0 && lines[duplicateIndex].Text == "同时间的末行", "相同时间保留文件顺序并显示最后一行。" );
        int blankIndex = LyricTimeline.FindIndex(lines, 4.2);
        CheckThat("lrc_blank_line", blankIndex >= 0 && lines[blankIndex].Text == "", "保留有时间的空行。" );
        CheckThat("lrc_inline_timestamps", lines.Count > 0 && lines[^1].Text == "逐字", "去除逐字标签，保持可读歌词。" );
        var offset = LyricTimeline.Parse("[offset:500]\n[00:01.00]提前半秒");
        CheckThat("lrc_positive_offset", offset.Count == 1 && Math.Abs(offset[0].Seconds - 0.5) < 0.0001, "正 offset 提前显示。" );
        var negative = LyricTimeline.Parse("[offset:-250]\n[00:01.000]延迟四分之一秒");
        CheckThat("lrc_negative_offset", negative.Count == 1 && Math.Abs(negative[0].Seconds - 1.25) < 0.0001, "负 offset 延迟显示。" );
        CheckThat("lrc_empty", LyricTimeline.Parse(null).Count == 0 && LyricTimeline.Parse("无时间戳的歌词").Count == 0, "空歌词和无时间戳文本安全返回空列表。" );
        CheckThat("lrc_nonfinite", LyricTimeline.FindIndex(lines, double.NaN) == -1, "无效位置不进入查找。" );
        CheckThat("lrc_long_minutes", Math.Abs(LyricTimeline.Parse("[125:02.125]长节目")[0].Seconds - 7502.125) < 0.0001, "支持超过 99 分钟的时间戳。" );
        return result;
    }

    private static void WriteSilentWav(string path, double seconds)
    {
        const int rate = 44100;
        const short channels = 1, bits = 16;
        int samples = (int)(rate * seconds);
        int bytes = samples * channels * (bits / 8);
        using var writer = new BinaryWriter(File.Create(path), Encoding.ASCII, false);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + bytes);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(rate);
        writer.Write(rate * channels * (bits / 8));
        writer.Write((short)(channels * (bits / 8)));
        writer.Write(bits);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(bytes);
        writer.Write(new byte[bytes]);
    }

    private sealed record Check(string Name, bool Passed, string Detail);
}
