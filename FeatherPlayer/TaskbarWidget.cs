using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace FeatherPlayer;

/// <summary>A real child HWND of the primary taskbar, occupying only a measured free interval.</summary>
public sealed class TaskbarWidget : IDisposable
{
    private readonly Action _previous, _toggle, _next, _openMain;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _watch;
    private readonly AutoResetEvent _wake = new(false);
    private readonly NativeTaskbar.WinEventDelegate _eventCallback;
    private readonly Thread _layoutWorker;
    private volatile bool _disposed, _enabled;
    private volatile Snapshot? _snapshot;
    private IntPtr _host, _handle, _eventHook;
    private WidgetWindow? _window;
    private PlaybackState _state = new(null, false, "登录后播放你的音乐", 0, 0);
    private long _layoutVersion;
    private long _shellLayoutEvents, _ignoredOwnEvents, _shellReorderScans;
    private bool _forcePlacement;
    private bool _hasPlacement;
    private bool _showLayoutBounds;
    public void ShowLayoutBounds(bool show) { _showLayoutBounds = show; _window?.ShowLayoutBounds(show); }
    private string _status = "任务栏歌词已关闭";

    public bool IsEmbedded => _enabled && _handle != IntPtr.Zero && NativeTaskbar.IsWindow(_handle) && NativeTaskbar.GetParent(_handle) == _host;
    public string Status => _status;
    public int Alignment { get; set; } = 1;
    public WidgetLayout Layout { get; set; } = new();
    internal (double Text, double Buttons) YRanges => _window?.YRanges ?? (8, 12);
    public void ApplyLayout(WidgetLayout layout)
    {
        Layout = layout;
        _window?.ApplyInnerLayout(layout);
        _forcePlacement=true;
        Maintain();
    }
    public event Action<string>? StatusChanged;
    public IntPtr MenuOwner => _handle;

    /// <summary>Read-only native diagnostics; does not scan UIA or alter any window.</summary>
    public object GetDiagnostics()
    {
        var child = _handle;
        var shell = _host;
        var childExists = child != IntPtr.Zero && NativeTaskbar.IsWindow(child);
        var parent = childExists ? NativeTaskbar.GetParent(child) : IntPtr.Zero;
        var childBounds = childExists && NativeTaskbar.GetWindowRect(child, out var childRect) ? (NativeTaskbar.Rect?)childRect : null;
        var shellBounds = shell != IntPtr.Zero && NativeTaskbar.GetWindowRect(shell, out var shellRect) ? (NativeTaskbar.Rect?)shellRect : null;
        var client = shell != IntPtr.Zero && NativeTaskbar.GetClientRect(shell, out var clientRect) ? (NativeTaskbar.Rect?)clientRect : null;
        var snapshot = _snapshot;
        var version = Interlocked.Read(ref _layoutVersion);
        static object? Bounds(NativeTaskbar.Rect? rect) => rect is { } r ? new { left = r.Left, top = r.Top, right = r.Right, bottom = r.Bottom, width = r.Width, height = r.Height } : null;
        static string Handle(IntPtr handle) => "0x" + handle.ToInt64().ToString("X");
        return new
        {
            enabled = _enabled,
            embedded = _enabled && childExists && parent == shell,
            visible = childExists && NativeTaskbar.IsWindowVisible(child),
            status = _status,
            childHandle = Handle(child), actualParent = Handle(parent), shellHandle = Handle(shell),
            childStyle = childExists ? "0x" + NativeTaskbar.GetWindowLongPtr(child, NativeTaskbar.GwlStyle).ToInt64().ToString("X") : null,
            hasChildStyle = childExists && (NativeTaskbar.GetWindowLongPtr(child, NativeTaskbar.GwlStyle).ToInt64() & NativeTaskbar.WsChild) != 0,
            widgetScreenBounds = Bounds(childBounds), taskbarScreenBounds = Bounds(shellBounds),
            clientWidth = client?.Width ?? 0, clientHeight = client?.Height ?? 0,
            dpi = shell != IntPtr.Zero ? NativeTaskbar.GetDpiForWindow(shell) : 0,
            layoutVersion = version, snapshotVersion = snapshot?.Version,
            snapshotAgeMs = snapshot is null ? (long?)null : Environment.TickCount64 - snapshot.At,
            layoutPending = snapshot is null || snapshot.Host != shell || snapshot.Version != version,
            occupiedIntervals = snapshot?.Occupied.Select(x => new { left = x.Left, right = x.Right }).ToArray(),
            shellLayoutEvents = Interlocked.Read(ref _shellLayoutEvents),
            ignoredOwnEvents = Interlocked.Read(ref _ignoredOwnEvents),
            shellReorderScans = Interlocked.Read(ref _shellReorderScans)
        };
    }

    private sealed record Snapshot(IntPtr Host, long Version, long At, int Width, int Height, List<(int Left, int Right)> Occupied);

    public TaskbarWidget(Action previous, Action toggle, Action next, Action openMain)
    {
        _previous = previous; _toggle = toggle; _next = next; _openMain = openMain;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _eventCallback = OnShellLayoutEvent;
        _watch = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _watch.Tick += (_, _) => Maintain();
        _layoutWorker = new Thread(LayoutLoop) { IsBackground = true, Name = "任务栏空位检测" };
        _layoutWorker.SetApartmentState(ApartmentState.MTA);
        _layoutWorker.Start();
    }

    public void SetEnabled(bool enabled)
    {
        if (_disposed) return;
        if (!_dispatcher.CheckAccess()) { _dispatcher.BeginInvoke(() => SetEnabled(enabled)); return; }
        _enabled = enabled;
        if (enabled) { _watch.Start(); InvalidateLayout(); Maintain(); }
        else
        {
            _watch.Stop();
            HideIfShown();
            if (_eventHook != IntPtr.Zero) { NativeTaskbar.UnhookWinEvent(_eventHook); _eventHook = IntPtr.Zero; }
            SetStatus("任务栏歌词已关闭");
        }
        _wake.Set();
    }

    public void Update(PlaybackState state)
    {
        if (_disposed) return;
        if (!_dispatcher.CheckAccess()) { _dispatcher.BeginInvoke(() => Update(state)); return; }
        _state = state;
        _window?.Update(state);
    }

    private void SetStatus(string status)
    {
        if (_status == status) return;
        _status = status; StatusChanged?.Invoke(status);
    }

    private void InvalidateLayout()
    {
        Interlocked.Increment(ref _layoutVersion);
        _wake.Set();
    }

    private void HideIfShown()
    {
        _window?.SuspendLyrics(true);
        // Inspect the child's own style, rather than ancestor visibility: disabling
        // while Explorer is hidden must also clear our WS_VISIBLE for its return.
        if (_handle != IntPtr.Zero && (NativeTaskbar.GetWindowLongPtr(_handle, NativeTaskbar.GwlStyle).ToInt64() & NativeTaskbar.WsVisible) != 0)
            NativeTaskbar.ShowWindow(_handle, NativeTaskbar.SwHide);
    }

    private void OnShellLayoutEvent(IntPtr hook, uint eventType, IntPtr hwnd, int objectId, int childId, uint threadId, uint time)
    {
        if (_disposed || !_enabled || hwnd == IntPtr.Zero) return;
        if (eventType is not (0x8000 or 0x8001 or 0x8002 or 0x8003 or 0x8004 or 0x800B)) return;
        // OUTOFCONTEXT callbacks are delivered on the UI thread that installed this hook.
        // The PID-scoped hook already excludes our WPF process, but Explorer can report
        // accessibility events concerning a child HWND that belongs to another process.
        if (hwnd == _handle || (_handle != IntPtr.Zero && NativeTaskbar.IsChild(_handle, hwnd)))
        { Interlocked.Increment(ref _ignoredOwnEvents); return; }
        if (hwnd != _host && !NativeTaskbar.IsChild(_host, hwnd)) return;
        if(NativeTaskbar.MenuActive())return;
        Interlocked.Increment(ref _shellLayoutEvents);
        if (hwnd == _host && eventType == 0x8004)
        {
            // Showing/hiding/repositioning our child may produce a REORDER on its
            // Explorer parent. Do not hide or invalidate in response: that would
            // form show -> parent reorder -> hide -> parent reorder -> show feedback.
            // A background measurement will still detect genuine icon reordering.
            Interlocked.Increment(ref _shellReorderScans);
            _wake.Set(); return;
        }
        if (hwnd == _host && eventType == 0x800B && _snapshot is { } snapshot &&
            NativeTaskbar.GetClientRect(_host, out var client) && client.Width == snapshot.Width && client.Height == snapshot.Height)
        {
            // The embedded child follows its parent's screen position, including
            // taskbar auto-hide; unchanged client geometry does not invalidate gaps.
            _wake.Set(); return;
        }
        InvalidateLayout();
    }

    private void Maintain()
    {
        if (!_enabled || _disposed) return;
        if(NativeTaskbar.MenuActive())return;
        var host = NativeTaskbar.FindWindow("Shell_TrayWnd", null);
        if (host == IntPtr.Zero || !NativeTaskbar.IsWindowVisible(host))
        {
            HideIfShown();
            SetStatus("等待 Windows 任务栏"); return;
        }
        if (host != _host || _handle == IntPtr.Zero || !NativeTaskbar.IsWindow(_handle))
        {
            if (_eventHook != IntPtr.Zero) { NativeTaskbar.UnhookWinEvent(_eventHook); _eventHook = IntPtr.Zero; }
            _host = host;
            _hasPlacement=false;
            if (_window is not null)
            {
                try { NativeTaskbar.Detach(_handle); _window.Close(); } catch (InvalidOperationException) { }
            }
            _window = new WidgetWindow(_previous, _toggle, _next, _openMain);
            _window.Update(_state);
            _window.ShowLayoutBounds(_showLayoutBounds);
            _handle = new WindowInteropHelper(_window).EnsureHandle();
            if (!NativeTaskbar.Attach(_handle, host))
            {
                SetStatus("当前系统不允许任务栏嵌入");
                _window.Close(); _window = null; _handle = IntPtr.Zero; return;
            }
            InvalidateLayout();
        }
        if (_eventHook == IntPtr.Zero)
        {
            NativeTaskbar.GetWindowThreadProcessId(host, out var processId);
            // Event hook is scoped to Explorer and does not inject a DLL (WINEVENT_OUTOFCONTEXT).
            _eventHook = NativeTaskbar.SetWinEventHook(0x8000, 0x800B, IntPtr.Zero, _eventCallback, processId, 0, 0);
        }
        var snapshot = _snapshot;
        if (snapshot is null || snapshot.Host != host || snapshot.Version != Interlocked.Read(ref _layoutVersion) || Environment.TickCount64 - snapshot.At > 70_000)
        {
            SetStatus("正在检测任务栏空位"); return;
        }
        if (!NativeTaskbar.GetClientRect(host, out var client) || client.Width != snapshot.Width || client.Height != snapshot.Height)
        { InvalidateLayout(); return; }
        var scale = Math.Max(1, NativeTaskbar.GetDpiForWindow(host) / 96.0);
        var minWidth = (int)Math.Ceiling(220 * scale);
        var preferredWidth = (int)Math.Ceiling(Math.Clamp(Layout.Width, 220, 800) * scale);
        var gap = LargestGap(snapshot.Occupied, snapshot.Width, (int)Math.Ceiling(10 * scale), Layout.Left < 0 ? -1 : (int)(Layout.Left * scale), minWidth);
        if (snapshot.Height < 28 * scale || snapshot.Height > 120 * scale || gap.Right - gap.Left < minWidth)
        {
            HideIfShown();
            SetStatus("任务栏空位不足，歌词条暂时隐藏"); return;
        }
        var width = Layout.FillAvailable ? gap.Right - gap.Left : Math.Min(preferredWidth, gap.Right - gap.Left);
        var height = Math.Min(snapshot.Height - (int)(4 * scale), (int)(44 * scale));
        var x = Alignment == 0 ? gap.Left : Alignment == 2 ? gap.Right - width : gap.Left + (gap.Right - gap.Left - width) / 2;
                if (Layout.Left >= 0)
        {
            x = Math.Clamp((int)(Layout.Left * scale), gap.Left, gap.Right - (Layout.FillAvailable ? minWidth : width));
            if (Layout.FillAvailable) width = gap.Right - x;
        }
        var y = (snapshot.Height - height) / 2;
        if(!Layout.FillAvailable && _hasPlacement && !_forcePlacement && Layout.Left<0 && NativeTaskbar.GetWindowRect(_handle,out var placed) && NativeTaskbar.GetWindowRect(host,out var hostRect))
        {
            int oldX=placed.Left-hostRect.Left;
            if(placed.Width>0 && oldX>=0 && oldX+placed.Width<=snapshot.Width && !snapshot.Occupied.Any(r=>oldX<r.Right && oldX+placed.Width>r.Left)) { x=oldX; width=Math.Min(width,placed.Width); }
        }
        _forcePlacement=false;
        // Set WPF size in DIP, then place HWND using the parent's physical client coordinates.
        var dipWidth = width / scale;
        var dipHeight = height / scale;
        if (_window!.Width != dipWidth) _window.Width = dipWidth;
        if (_window.Height != dipHeight) _window.Height = dipHeight;
        _window.ArrangeElements(Layout, dipWidth, dipHeight);
        if (!_window.IsVisible) _window.Show();
        var origin = new NativeTaskbar.Point();
        NativeTaskbar.ClientToScreen(host, ref origin);
        if (!NativeTaskbar.GetWindowRect(_handle, out var currentRect) || currentRect.Left != origin.X + x || currentRect.Top != origin.Y + y || currentRect.Width != width || currentRect.Height != height)
            NativeTaskbar.SetWindowPos(_handle, IntPtr.Zero, x, y, width, height, NativeTaskbar.SwpNoActivate);
        if (!NativeTaskbar.IsWindowVisible(_handle)) NativeTaskbar.ShowWindow(_handle, NativeTaskbar.SwShowNoActivate);
        _window.SuspendLyrics(false);
        _hasPlacement=true;
        SetStatus("已嵌入任务栏 · 右键展开音乐库");
    }

    private static (int Left, int Right) LargestGap(List<(int Left, int Right)> occupied, int width, int margin, int desired = -1, int minimum = 0)
    {
        var cursor = margin;
        var best = (Left: 0, Right: 0);
        double distance = double.MaxValue;
        void Consider((int Left, int Right) candidate)
        {
            if (desired < 0) { if (candidate.Right - candidate.Left > best.Right - best.Left) best = candidate; return; }
            if (candidate.Right - candidate.Left < minimum) return;
            double current = Math.Abs(Math.Clamp(desired, candidate.Left, candidate.Right - minimum) - desired);
            if (current < distance) { best = candidate; distance = current; }
        }
        foreach (var interval in occupied)
        {
            var candidate = (Left: cursor, Right: Math.Max(cursor, interval.Left - margin));
            Consider(candidate);
            cursor = Math.Max(cursor, interval.Right + margin);
        }
        Consider((cursor, width - margin));
        return best;
    }

    private void LayoutLoop()
    {
        var delay = 1000;
        Snapshot? previous = null;
        while (!_disposed)
        {
            if (!_enabled) { _wake.WaitOne(); continue; }
            if(NativeTaskbar.MenuActive()){_wake.WaitOne(500);continue;}
            var host = _host;
            var handle = _handle;
            var version = Interlocked.Read(ref _layoutVersion);
            try
            {
                if (host != IntPtr.Zero && NativeTaskbar.IsWindow(host) && NativeTaskbar.GetClientRect(host, out var client) && NativeTaskbar.GetWindowRect(host, out var bounds))
                {
                    var intervals = new List<(int Left, int Right)>();
                    // UI Automation calls must never run on the WPF thread: they synchronously cross into Explorer.
                    var request=new CacheRequest{TreeScope=TreeScope.Element};
                    request.Add(AutomationElement.ControlTypeProperty);request.Add(AutomationElement.NativeWindowHandleProperty);
                    request.Add(AutomationElement.BoundingRectangleProperty);request.Add(AutomationElement.ClassNameProperty);request.Add(AutomationElement.IsOffscreenProperty);
                    using(request.Activate())Collect(AutomationElement.FromHandle(host), bounds, unchecked((int)handle.ToInt64()), intervals, 0);
                    if (intervals.Count > 0)
                    {
                        var ordered = intervals.OrderBy(x => x.Left).ToList();
                        var merged = new List<(int Left, int Right)>();
                        foreach (var interval in ordered)
                        {
                            if (merged.Count > 0 && interval.Left <= merged[^1].Right + 2)
                                merged[^1] = (merged[^1].Left, Math.Max(merged[^1].Right, interval.Right));
                            else merged.Add(interval);
                        }
                        var snapshot = new Snapshot(host, version, Environment.TickCount64, client.Width, client.Height, merged);
                        var same = previous is not null && previous.Host == host && previous.Version == version && previous.Width == client.Width && previous.Height == client.Height && previous.Occupied.SequenceEqual(merged);
                        delay = same ? Math.Min(delay * 2, 60_000) : 1000;
                        previous = snapshot;
                        _snapshot = snapshot;
                        if (!_disposed && !_dispatcher.HasShutdownStarted) _dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)Maintain);
                    }
                }
            }
            // Providers can throw several exception types while Explorer replaces its tree.
            // A broken accessibility provider must not terminate the music player.
            catch (Exception) { delay = 3000; }
            // Events interrupt the wait; a changing shell is debounced rather than scanned per message.
            if (_wake.WaitOne(delay))
            {
                if (_disposed) break;
                Thread.Sleep(150);
                while (_wake.WaitOne(0)) { }
                delay = 1000;
            }
        }
    }

    private void Collect(AutomationElement element, NativeTaskbar.Rect bounds, int exclude, List<(int Left, int Right)> result, int depth)
    {
        if (_disposed || depth > 10) return;
        var children = element.FindAll(TreeScope.Children, System.Windows.Automation.Condition.TrueCondition);
        foreach (AutomationElement child in children)
        {
            if (_disposed) return;
            try
            {
                var current = child.Cached;
                if(current.ControlType==ControlType.Menu || current.ControlType==ControlType.MenuBar || current.ControlType==ControlType.Window)continue;
                if (exclude != 0 && current.NativeWindowHandle == exclude) continue;
                var rect = current.BoundingRectangle;
                if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0 || rect.Bottom <= bounds.Top || rect.Top >= bounds.Bottom) continue;
                // Windows 11 retains obsolete Win10 container HWNDs with incorrect rectangles.
                if (Environment.OSVersion.Version.Build >= 22000 && current.ClassName is "ReBarWindow32" or "WorkerW" or "MSTaskListWClass") continue;
                if (rect.Width >= bounds.Width * 0.9) { Collect(child, bounds, exclude, result, depth + 1); continue; }
                if (current.ControlType == ControlType.Pane || current.ControlType == ControlType.ToolBar || current.ControlType == ControlType.Group || current.ControlType == ControlType.List)
                { int count = result.Count; Collect(child, bounds, exclude, result, depth + 1); if (result.Count > count) continue; }
                if (current.IsOffscreen) continue;
                var left = Math.Clamp((int)Math.Floor(rect.Left) - bounds.Left, 0, bounds.Width);
                var right = Math.Clamp((int)Math.Ceiling(rect.Right) - bounds.Left, 0, bounds.Width);
                if (right > left) result.Add((left, right));
            }
            catch (ElementNotAvailableException) { }
            catch (System.Runtime.InteropServices.COMException) { }
            catch (InvalidOperationException) { }
            catch (ArgumentException) { }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _enabled = false;
        _watch.Stop(); _wake.Set();
        if (_eventHook != IntPtr.Zero) NativeTaskbar.UnhookWinEvent(_eventHook);
        if (_window is not null)
        {
            NativeTaskbar.Detach(_handle);
            try { _window.Close(); } catch (InvalidOperationException) { }
        }
        // Do not join a thread inside an uninterruptible cross-process UIA call.
        // Its background flag guarantees it cannot keep the player process alive.
    }

    private sealed class WidgetWindow : Window
    {
        private readonly TextBlock _song = new();
        private readonly AnimatedLyrics _lyric = new();
        private readonly Button _play;
        private readonly List<Button> _controls = new();
        private readonly Canvas _surface = new();
        private readonly Image _coverImage = new() { Stretch = Stretch.UniformToFill };
        private readonly Border _cover = new() { CornerRadius = new CornerRadius(4), Margin = new Thickness(0,0,6,0) };
        private readonly ContentControl _coverFallback = new() { HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        private StackPanel _text = null!, _buttons = null!;
        private readonly Action _openMenu;
        private WidgetLayout _layout=new();
        private double _layoutWidth=300, _layoutHeight=40, _iconSize=14;
        private bool? _playingIcon;
        private readonly Border _componentBounds = Guide();
        private readonly Border _textBounds = Guide();
        private readonly Border _buttonBounds = Guide();
        private static Border Guide() => new() { IsHitTestVisible = false, BorderThickness = new Thickness(1), Visibility = Visibility.Collapsed, CornerRadius = new CornerRadius(2) };
        internal void ShowLayoutBounds(bool show)
        {
            foreach (var guide in new[] { _componentBounds, _textBounds, _buttonBounds }) guide.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            _arranged = false;
            ArrangeElements(_layout, _layoutWidth, _layoutHeight);
        }
        private bool _arranged;
        private Visibility _arrangedMetadata;

        internal WidgetWindow(Action previous, Action toggle, Action next, Action openMain)
        {
            _openMenu = openMain;
            Title = "light music任务栏歌词";
            Width = 400; Height = 44;
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false; ShowActivated = false;
            AllowsTransparency = true; Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
            Focusable = false;
            var root = new Grid { Margin = new Thickness(8, 2, 4, 2), Background = Brushes.Transparent };
            root.ColumnDefinitions.Add(new ColumnDefinition());
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent, Cursor = Cursors.Hand };
            text.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) openMain(); };
            _song.FontFamily = new FontFamily("Microsoft YaHei UI"); _song.FontSize = 10;
            _song.Foreground = new SolidColorBrush(Color.FromRgb(168, 178, 190));
            _song.TextTrimming = TextTrimming.CharacterEllipsis;
            
            text.Children.Add(_song); text.Children.Add(_lyric);
            root.Children.Add(text);
            var controls = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7, 0, 0, 0) };
            var coverContent = new Grid();
            coverContent.Children.Add(_coverFallback); coverContent.Children.Add(_coverImage);
            _cover.Child = coverContent;
            controls.Children.Add(_cover);
            controls.Children.Add(AddButton("previous", "上一首", previous));
            _play = AddButton("play", "播放 / 暂停", toggle);
            controls.Children.Add(_play);
            controls.Children.Add(AddButton("next", "下一首", next));
            Grid.SetColumn(controls, 1); root.Children.Add(controls);
            Content = root;
            root.Children.Remove(text); root.Children.Remove(controls);
            text.Margin = new Thickness(0); controls.Margin = new Thickness(0);
            _text = text; _buttons = controls;
            _surface.Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
            _surface.ClipToBounds = true; _surface.Children.Add(text); _surface.Children.Add(controls);
            _surface.Children.Add(_componentBounds); _surface.Children.Add(_textBounds); _surface.Children.Add(_buttonBounds);
            Content = _surface;
            SystemTheme.Changed += ApplyTheme;
            ApplyTheme();
            Closed += (_,_)=>{SystemTheme.Changed-=ApplyTheme;_lyric.Release();};
            SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WindowMessage);
        }

        private Button AddButton(string glyph, string label, Action action)
        {
            var button = new Button
            {
                Content = LineIcons.Create(glyph, 14, 2), Width = 24, Height = 24,
                FontSize = 18,
                Foreground = Brushes.White, Background = Brushes.Transparent,
                BorderThickness = new Thickness(0), Padding = new Thickness(0),
                Focusable = false, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center
            };
            AutomationProperties.SetName(button, label);
            button.Click += (_, _) => action();
            _controls.Add(button);
            return button;
        }

        internal (double Text, double Buttons) YRanges =>
            (Math.Max(0, _layoutHeight - (_song.Visibility == Visibility.Visible ? 14 : 0) - _lyric.Height),
             Math.Max(0, _layoutHeight - (_layout.ShowCover ? Math.Min(32, _layoutHeight - 4) : Math.Min(24, _layoutHeight))));
        internal void ApplyInnerLayout(WidgetLayout layout) => ArrangeElements(layout,_layoutWidth,_layoutHeight);

        internal void ArrangeElements(WidgetLayout layout, double width, double height)
        {
            if(_arranged && _layout==layout && _layoutWidth==width && _layoutHeight==height && _arrangedMetadata==_song.Visibility)return;
            _arranged=true;_arrangedMetadata=_song.Visibility;
            _layout=layout;_layoutWidth=width;_layoutHeight=height;
            double buttonSize=Math.Min(24,Math.Max(1,height));
            _iconSize=buttonSize*14/24;
            foreach(var button in _controls)
            {
                button.Width=button.Height=buttonSize;
                if(button.Content is FrameworkElement icon)icon.Width=icon.Height=_iconSize;
            }
            double coverSize = Math.Min(32, Math.Max(1, height - 4));
            _cover.Visibility = layout.ShowCover ? Visibility.Visible : Visibility.Collapsed;
            _cover.Width = _cover.Height = coverSize;
            _coverImage.Clip = new RectangleGeometry(new Rect(0,0,coverSize,coverSize),4,4);
            double controlsWidth=buttonSize*3 + (layout.ShowCover ? coverSize + 6 : 0);
            double bx = layout.ButtonsX < 0 ? Math.Max(0,width-controlsWidth-2) : System.Math.Clamp(layout.ButtonsX, 0, System.Math.Max(0, width-controlsWidth));
            double tx = System.Math.Clamp(layout.TextX, 0, System.Math.Max(0, width - 80));
            if (tx >= bx && tx < bx + controlsWidth + 6)
                tx = Math.Min(width - 4, bx + controlsWidth + 6);
            double metadataHeight=_song.Visibility==Visibility.Visible ? 14 : 0;
            _song.Height=metadataHeight;
            _lyric.FitHeight(Math.Max(1,height-metadataHeight));
            double textHeight=metadataHeight+_lyric.Height;
            double AlignedY(double custom, double itemHeight)
            {
                double space = Math.Max(0, height - itemHeight);
                return layout.VerticalAlignment switch { 0 => 0, 1 => space / 2, 2 => space, _ => Math.Clamp(custom, 0, space) };
            }
            Canvas.SetLeft(_text, tx); Canvas.SetTop(_text, AlignedY(layout.TextY, textHeight));
            double availableTextWidth = Math.Max(0, bx > tx ? bx - tx - 6 : width - tx - 4);
            _text.Width = !layout.FillAvailable && layout.TextWidth >= 0
                ? Math.Min(layout.TextWidth, availableTextWidth)
                : availableTextWidth;
            Canvas.SetLeft(_buttons, bx); Canvas.SetTop(_buttons, AlignedY(layout.ButtonsY, Math.Max(buttonSize,layout.ShowCover ? coverSize : 0)));
            void Bounds(Border guide, double left, double top, double w, double h)
            {
                Canvas.SetLeft(guide, left); Canvas.SetTop(guide, top);
                guide.Width = Math.Max(0, w); guide.Height = Math.Max(0, h);
            }
            Bounds(_componentBounds, .5, .5, width - 1, height - 1);
            Bounds(_textBounds, tx, Canvas.GetTop(_text), _text.Width, textHeight);
            Bounds(_buttonBounds, bx, Canvas.GetTop(_buttons), controlsWidth, Math.Max(buttonSize, layout.ShowCover ? coverSize : 0));
            _componentBounds.BorderBrush = SystemTheme.Secondary;
            _textBounds.BorderBrush = new SolidColorBrush(Color.FromRgb(69, 153, 236));
            _buttonBounds.BorderBrush = new SolidColorBrush(Color.FromRgb(224, 158, 56));
        }

        private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wparam, IntPtr lparam, ref bool handled)
        {
            if (message == 0x205) { handled = true; Dispatcher.BeginInvoke(_openMenu); return IntPtr.Zero; }
            if (message == 0x7B) { handled = true; return IntPtr.Zero; }
            if (message == 0x21) { handled = true; return new IntPtr(3); } // MA_NOACTIVATE: controls do not steal keyboard focus.
            return IntPtr.Zero;
        }

        internal void SuspendLyrics(bool value)=>_lyric.Suspend(value);
        private void ApplyTheme()
        {
            _song.Foreground=SystemTheme.Secondary;
            _cover.Background = new SolidColorBrush(Color.FromArgb(16, SystemTheme.Accent.R, SystemTheme.Accent.G, SystemTheme.Accent.B));
            _coverFallback.Content = LineIcons.Create("headphones",18,1.6);
            foreach(var button in _controls)
            {
                button.Foreground=SystemTheme.Ink;
                if(button.Content is System.Windows.Controls.Viewbox view && view.Child is System.Windows.Shapes.Path path)path.Stroke=SystemTheme.Ink;
            }
        }
        internal void Update(PlaybackState state)
        {
            var song = state.Song is null ? "light music · 随时听一首" : state.Song.Title;
            _coverImage.Source = state.Cover;
            _coverFallback.Visibility = state.Cover == null ? Visibility.Visible : Visibility.Hidden;
            if (_song.Text != song) _song.Text = song;
            _song.Visibility = state.Song == null ? Visibility.Visible : Visibility.Collapsed;
            _lyric.Update(state);
            if(_playingIcon!=state.IsPlaying){_playingIcon=state.IsPlaying;_play.Content=LineIcons.Create(state.IsPlaying?"pause":"play",_iconSize,2);}
            foreach (var button in _controls) button.IsEnabled = state.Song is not null;
            ArrangeElements(_layout,_layoutWidth,_layoutHeight);
        }
    }
}
