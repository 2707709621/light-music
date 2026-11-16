using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace FeatherPlayer;

public sealed record SpectrumOptions(bool Enabled = false, bool Centered = true,
    double Offset = 0, double Gap = 8, double Width = 320, double Height = 64)
{
    internal SpectrumOptions Normalize() => this with
    {
        Offset = double.IsFinite(Offset) ? Math.Clamp(Offset,-8000,8000) : 0,
        Gap = double.IsFinite(Gap) ? Math.Clamp(Gap,0,8000) : 8,
        Width = double.IsFinite(Width) ? Math.Clamp(Width,120,1400) : 320,
        Height = double.IsFinite(Height) ? Math.Clamp(Height,20,240) : 64
    };
}

internal sealed class SpectrumController : IDisposable
{
    private readonly DispatcherTimer _frames, _watch;
    private readonly float[] _values = new float[32];
    private SpectrumWindow? _window;
    private NativeApi? _api;
    private IntPtr _capture;
    private bool _playing, _editing, _disposed;
    private long _retryAt, _lastFrame;
    private NativeTaskbar.Rect _bar;
    private double _scale = 1;
    internal SpectrumOptions Options { get; private set; } = new();
    internal string Status { get; private set; } = "频谱条已关闭";
    internal event Action? Changed;

    internal SpectrumController(Dispatcher dispatcher)
    {
        _frames = new DispatcherTimer(DispatcherPriority.Background,dispatcher) { Interval = TimeSpan.FromSeconds(1.0 / 30) };
        _watch = new DispatcherTimer(DispatcherPriority.Background,dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _frames.Tick += (_, _) => DrawFrame();
        _watch.Tick += (_, _) => UpdateActivity();
    }

    internal void Configure(SpectrumOptions options)
    {
        if (_disposed) return;
        if (options.Enabled != Options.Enabled) _retryAt = 0;
        Options = options.Normalize();
        Changed?.Invoke();
        UpdateActivity();
        NativePopupMenu.Refresh();
    }

    internal void SetPlaying(bool playing)
    {
        if (_playing == playing || _disposed) return;
        _playing = playing;
        UpdateActivity();
    }

    internal void EditPosition(Action save)
    {
        var original = Options;
        _editing = true;
        UpdateActivity();
        try
        {
            var editor = new SpectrumLayoutWindow(this);
            if (editor.ShowDialog() == true) save();
            else Configure(original);
        }
        finally { _editing = false; UpdateActivity(); }
    }

    private void UpdateActivity()
    {
        if (_disposed) return;
        bool active = Options.Enabled && _playing;
        if (!active && !_editing)
        {
            StopCapture(); _frames.Stop(); _watch.Stop();
            _window?.Hide();
            Status = Options.Enabled ? "播放时显示频谱，暂停时隐藏" : "频谱条已关闭";
            if (!Options.Enabled) { _window?.Close(); _window = null; }
            return;
        }
        _watch.Start();
        _window ??= new SpectrumWindow(DragTo);
        _window.SetEditing(_editing);
        if (!PlaceWindow())
        {
            StopCapture(); _frames.Stop(); _window.Hide();
            Status = "等待可见的 Windows 任务栏";
            return;
        }
        if (!_window.IsVisible)
        {
            _window.Show();
            NativePopupMenu.RaiseOpenMenus();
        }
        if (!active)
        {
            StopCapture(); _frames.Stop();
            _window.Surface.Frame(_values,1);
            return;
        }
        if (_capture == IntPtr.Zero && Environment.TickCount64 >= _retryAt) StartCapture();
        if (_capture != IntPtr.Zero && !_frames.IsEnabled)
        {
            _lastFrame = Stopwatch.GetTimestamp(); _frames.Start();
        }
    }

    private bool PlaceWindow()
    {
        var bar = NativeTaskbar.FindWindow("Shell_TrayWnd",null);
        if (bar == IntPtr.Zero || !NativeTaskbar.IsWindowVisible(bar) || !NativeTaskbar.GetWindowRect(bar,out _bar)) return false;
        _scale = Math.Max(1,NativeTaskbar.GetDpiForWindow(bar) / 96.0);
        var screen = System.Windows.Forms.Screen.FromHandle(bar);
        var monitor = screen.Bounds;
        var work = screen.WorkingArea;
        if (!_editing)
        {
            var visible = System.Drawing.Rectangle.Intersect(monitor,
                System.Drawing.Rectangle.FromLTRB(_bar.Left,_bar.Top,_bar.Right,_bar.Bottom));
            if (visible.Width < 3 || visible.Height < 3) return false;
            var hit = WindowFromPoint(new NativeTaskbar.Point { X = (_bar.Left + _bar.Right) / 2, Y = (_bar.Top + _bar.Bottom) / 2 });
            if (hit != bar && !NativeTaskbar.IsChild(bar,hit)) return false;
        }
        int width = Math.Min(work.Width,(int)Math.Round(Options.Width * _scale));
        int height = Math.Min(work.Height,(int)Math.Round(Options.Height * _scale));
        bool horizontal = _bar.Width >= _bar.Height;
        int x, y;
        if (horizontal)
        {
            x = Options.Centered ? _bar.Left + (_bar.Width - width) / 2 : _bar.Left + (int)Math.Round(Options.Offset * _scale);
            bool bottom = _bar.Top >= monitor.Top + monitor.Height / 2;
            y = bottom ? _bar.Top - height - (int)Math.Round(Options.Gap * _scale) : _bar.Bottom + (int)Math.Round(Options.Gap * _scale);
        }
        else
        {
            y = Options.Centered ? _bar.Top + (_bar.Height - height) / 2 : _bar.Top + (int)Math.Round(Options.Offset * _scale);
            bool right = _bar.Left >= monitor.Left + monitor.Width / 2;
            x = right ? _bar.Left - width - (int)Math.Round(Options.Gap * _scale) : _bar.Right + (int)Math.Round(Options.Gap * _scale);
        }
        x = Math.Clamp(x,work.Left,Math.Max(work.Left,work.Right - width));
        y = Math.Clamp(y,work.Top,Math.Max(work.Top,work.Bottom - height));
        _window!.Width = width / _scale; _window.Height = height / _scale;
        var handle = new WindowInteropHelper(_window).EnsureHandle();
        if (!NativeTaskbar.GetWindowRect(handle,out var current) || current.Left != x || current.Top != y || current.Width != width || current.Height != height)
            NativeTaskbar.SetWindowPos(handle,IntPtr.Zero,x,y,width,height,NativeTaskbar.SwpNoActivate);
        return true;
    }

    private void DragTo(int x, int y)
    {
        bool horizontal = _bar.Width >= _bar.Height;
        var screen = System.Windows.Forms.Screen.FromHandle(NativeTaskbar.FindWindow("Shell_TrayWnd",null));
        var work = screen.WorkingArea;
        int width = Math.Min(work.Width,(int)Math.Round(Options.Width * _scale));
        int height = Math.Min(work.Height,(int)Math.Round(Options.Height * _scale));
        x = Math.Clamp(x,work.Left,Math.Max(work.Left,work.Right - width));
        y = Math.Clamp(y,work.Top,Math.Max(work.Top,work.Bottom - height));
        bool trailing = horizontal ? _bar.Top >= screen.Bounds.Top + screen.Bounds.Height / 2 : _bar.Left >= screen.Bounds.Left + screen.Bounds.Width / 2;
        double offset = horizontal ? (x - _bar.Left) / _scale : (y - _bar.Top) / _scale;
        double gap = horizontal ? (trailing ? _bar.Top - y - height : y - _bar.Bottom) / _scale
            : (trailing ? _bar.Left - x - width : x - _bar.Right) / _scale;
        Configure(Options with { Centered = false, Offset = offset, Gap = gap });
    }

    private void StartCapture()
    {
        try
        {
            if (Environment.OSVersion.Version.Build < 20348)
            {
                Status = "进程音频采集需要 Windows 10 build 20348 或更新版本";
                _retryAt = long.MaxValue; return;
            }
            _api ??= new NativeApi();
            _capture = _api.Start();
            if (_capture == IntPtr.Zero) throw new InvalidOperationException("无法启动音频采集");
            Status = "正在连接 light music 音频…";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or DllNotFoundException or
            BadImageFormatException or EntryPointNotFoundException or InvalidOperationException)
        {
            Status = "频谱采集启动失败 · 0x" + error.HResult.ToString("X8");
            _retryAt = long.MaxValue;
        }
    }

    private void DrawFrame()
    {
        if (_disposed || _window == null) return;
        if (_capture != IntPtr.Zero)
        {
            int result = _api!.Read(_capture,_values,_values.Length);
            if (result < 0)
            {
                StopCapture();
                _retryAt = result == unchecked((int)0x800705b4) ? long.MaxValue : Environment.TickCount64 + 3000;
                Status = "频谱采集失败 · 0x" + result.ToString("X8");
                NativePopupMenu.Refresh();
            }
            else if (result == 0 && Status != "频谱正在跟随 light music")
            {
                Status = "频谱正在跟随 light music"; NativePopupMenu.Refresh();
            }
        }
        long now = Stopwatch.GetTimestamp();
        double seconds = (double)(now - _lastFrame) / Stopwatch.Frequency; _lastFrame = now;
        if (!_window.Surface.Frame(_values,seconds) && _capture == IntPtr.Zero) _frames.Stop();
    }

    private void StopCapture()
    {
        if (_capture != IntPtr.Zero) { _api!.Stop(_capture); _capture = IntPtr.Zero; }
        Array.Clear(_values);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _frames.Stop(); _watch.Stop(); StopCapture();
        _window?.Close(); _window = null;
    }

    private sealed class NativeApi
    {
        internal readonly StartSpectrum Start = NativeTaskbarComponent.Export<StartSpectrum>("StartLightSpectrum");
        internal readonly ReadSpectrum Read = NativeTaskbarComponent.Export<ReadSpectrum>("ReadLightSpectrum");
        internal readonly StopSpectrum Stop = NativeTaskbarComponent.Export<StopSpectrum>("StopLightSpectrum");
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate IntPtr StartSpectrum();
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ReadSpectrum(IntPtr capture,[Out,MarshalAs(UnmanagedType.LPArray,SizeParamIndex = 2)] float[] values,int count);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void StopSpectrum(IntPtr capture);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativeTaskbar.Point point);
}

internal sealed class SpectrumWindow : Window
{
    private readonly Action<int,int> _drag;
    private bool _editing, _dragging;
    private NativeTaskbar.Point _anchor;
    private NativeTaskbar.Rect _initial;
    internal SpectrumSurface Surface { get; } = new();

    internal SpectrumWindow(Action<int,int> drag)
    {
        _drag = drag;
        Title = "light music · 音频频谱";
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = Brushes.Transparent;
        ShowInTaskbar = false; ShowActivated = false; Focusable = false; Topmost = true;
        Content = Surface;
        SourceInitialized += (_, _) =>
        {
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WindowMessage);
            SetEditing(_editing);
        };
        MouseLeftButtonDown += (_, e) =>
        {
            if (!_editing) return;
            GetCursorPos(out _anchor);
            NativeTaskbar.GetWindowRect(new WindowInteropHelper(this).Handle,out _initial);
            _dragging = CaptureMouse(); e.Handled = true;
        };
        MouseMove += (_, _) =>
        {
            if (!_dragging || Mouse.LeftButton != MouseButtonState.Pressed) return;
            GetCursorPos(out var point);
            _drag(_initial.Left + point.X - _anchor.X,_initial.Top + point.Y - _anchor.Y);
        };
        MouseLeftButtonUp += (_, _) => { _dragging = false; ReleaseMouseCapture(); };
        LostMouseCapture += (_, _) => _dragging = false;
        Closed += (_, _) => Surface.Dispose();
    }

    internal void SetEditing(bool editing)
    {
        _editing = editing; Surface.Preview(editing);
        Cursor = editing ? Cursors.SizeAll : Cursors.Arrow;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        long style = NativeTaskbar.GetWindowLongPtr(handle,NativeTaskbar.GwlExStyle).ToInt64();
        style |= NativeTaskbar.WsExNoActivate | NativeTaskbar.WsExToolWindow;
        style = editing ? style & ~0x20L : style | 0x20L;
        NativeTaskbar.SetWindowLongPtr(handle,NativeTaskbar.GwlExStyle,new IntPtr(style));
    }

    private IntPtr WindowMessage(IntPtr hwnd,int message,IntPtr wparam,IntPtr lparam,ref bool handled)
    {
        if (message == 0x21) { handled = true; return new IntPtr(3); }
        if (message == 0x84 && !_editing) { handled = true; return new IntPtr(-1); }
        return IntPtr.Zero;
    }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativeTaskbar.Point point);
}
