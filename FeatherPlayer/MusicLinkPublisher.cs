using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using LightSuite;

namespace FeatherPlayer;

/// <summary>有 light drive 订阅时按需采集本进程音频，其余时间仅发送低频状态心跳。</summary>
internal sealed class MusicLinkPublisher : IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly Func<bool> _playing;
    private readonly float[] _bands = new float[32];
    private MusicLinkChannel? _channel;
    private IntPtr _capture;
    private StartSpectrum? _start;
    private ReadSpectrum? _read;
    private StopSpectrum? _stop;
    private long _retryAt;
    private int _status;
    private bool _disposed;

    public MusicLinkPublisher(Dispatcher dispatcher, Func<bool> playing)
    {
        _playing = playing;
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => Publish();
        SystemTheme.Changed += Publish;
        MenuAppearanceSettings.Changed += Publish;
        _timer.Start();
        Publish();
    }

    public void Publish()
    {
        if (_disposed) return;
        try
        {
            _channel ??= MusicLinkChannel.CreatePublisher();
            bool capture = _playing() && _channel.HasActiveReader;
            _timer.Interval = TimeSpan.FromMilliseconds(capture ? 50 : 500);
            if (!capture) StopCapture();
            else ReadAudio();
            float energy = 0, bass = 0;
            for (int i = 0; i < _bands.Length; i++)
            {
                energy += _bands[i] / _bands.Length;
                if (i < 8) bass += _bands[i] / 8;
            }
            var color = SystemTheme.Accent;
            var appearance = MenuAppearanceSettings.Current;
            _channel.Publish(new(Environment.ProcessId, _playing(), (uint)(color.R << 16 | color.G << 8 | color.B),
                appearance.Opacity, appearance.Glow, (int)appearance.Blur, _status, energy, bass));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // 联动不可用时继续播放，下个低频心跳重试连接。
            StopCapture();
            _channel?.Dispose(); _channel = null;
            _timer.Interval = TimeSpan.FromMilliseconds(500);
        }
    }

    private void ReadAudio()
    {
        if (Environment.OSVersion.Version.Build < 20348) { _status = -2; return; }
        if (_capture == IntPtr.Zero && Environment.TickCount64 < _retryAt) return;
        try
        {
            if (_capture == IntPtr.Zero)
            {
                _start ??= NativeTaskbarComponent.Export<StartSpectrum>("StartLightSpectrum");
                _read ??= NativeTaskbarComponent.Export<ReadSpectrum>("ReadLightSpectrum");
                _stop ??= NativeTaskbarComponent.Export<StopSpectrum>("StopLightSpectrum");
                _capture = _start();
                if (_capture == IntPtr.Zero) throw new InvalidOperationException("音频采集未启动");
            }
            int result = _read!(_capture, _bands, _bands.Length);
            if (result < 0) Marshal.ThrowExceptionForHR(result);
            _status = result == 0 ? 2 : 1;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or DllNotFoundException or
            EntryPointNotFoundException or BadImageFormatException or InvalidOperationException or COMException)
        {
            StopCapture();
            _status = -1;
            _retryAt = Environment.TickCount64 + 5000;
        }
    }

    private void StopCapture()
    {
        if (_capture != IntPtr.Zero) { _stop!(_capture); _capture = IntPtr.Zero; }
        Array.Clear(_bands);
        _status = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        SystemTheme.Changed -= Publish;
        MenuAppearanceSettings.Changed -= Publish;
        StopCapture();
        _channel?.Publish(new(Environment.ProcessId, false, 0x647487, .65, .25, 2, 0, 0, 0));
        _channel?.Dispose();
        _channel = null;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr StartSpectrum();
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ReadSpectrum(IntPtr handle, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] float[] bands, int count);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void StopSpectrum(IntPtr handle);
}
