using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Threading;

namespace FeatherPlayer;

internal sealed class TaskbarAppearanceController : IDisposable
{
    private readonly DispatcherTimer _watch;
    private MemoryMappedFile? _mapping;
    private MemoryMappedViewAccessor? _view;
    private EventWaitHandle? _changed;
    private uint _explorer;
    private int _generation, _revision;
    private bool _disposed;
    private string _status = "使用 Windows 默认外观";

    internal bool Enabled { get; private set; }
    internal bool Motion { get; private set; } = true;
    internal string Status => _status;

    internal TaskbarAppearanceController(Dispatcher dispatcher)
    {
        _watch = new DispatcherTimer(DispatcherPriority.Background,dispatcher)
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _watch.Tick += (_, _) =>
        {
            string before = _status;
            Maintain();
            if (_status != before) NativePopupMenu.Refresh();
        };
        SystemTheme.Changed += Publish;
        MenuAppearanceSettings.Changed += Publish;
    }

    internal void Configure(bool enabled, bool motion)
    {
        if (_disposed) return;
        Enabled = enabled; Motion = motion;
        if (enabled)
        {
            _watch.Start();
            Maintain();
            Publish();
        }
        else
        {
            _watch.Stop();
            Publish();
            _status = "使用 Windows 默认外观";
        }
        NativePopupMenu.Refresh();
    }

    private void Maintain()
    {
        if (_disposed || !Enabled) return;
        if (Environment.OSVersion.Version.Build < 22621)
        {
            _status = "任务栏同步需要 Windows 11 22H2 或更新版本";
            return;
        }
        var taskbar = NativeTaskbar.FindWindow("Shell_TrayWnd",null);
        if (taskbar == IntPtr.Zero)
        {
            _status = "等待 Windows 任务栏";
            return;
        }
        NativeTaskbar.GetWindowThreadProcessId(taskbar,out uint process);
        if (process == 0) return;
        if (process != _explorer || _view == null)
        {
            ReleaseConnection();
            _explorer = process;
            _ = ConnectAsync(process,_generation);
            return;
        }
        int status = _view.ReadInt32(44);
        _status = status switch
        {
            2 => "任务栏已同步菜单外观",
            1 => "等待任务栏背景层",
            -1 => "任务栏外观连接失败 · 0x" + _view.ReadInt32(48).ToString("X8"),
            _ => _status
        };
    }

    private async Task ConnectAsync(uint explorer, int generation)
    {
        try
        {
            _mapping = MemoryMappedFile.CreateOrOpen("Local\\LightMusicTaskbar-" + explorer,64);
            _view = _mapping.CreateViewAccessor(0,64);
            _changed = new EventWaitHandle(false,EventResetMode.AutoReset,"Local\\LightMusicTaskbarChanged-" + explorer);
            _view.Write(44,0); _view.Write(48,0);
            Publish();
            _status = "正在连接 Windows 任务栏背景…";
            int result = await Task.Run(() => LoadNative()(explorer));
            if (_disposed || generation != _generation || !Enabled) return;
            if (result < 0) _status = "任务栏外观连接失败 · 0x" + result.ToString("X8");
            else Maintain();
            NativePopupMenu.Refresh();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            BadImageFormatException or DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            if (_disposed || generation != _generation || !Enabled) return;
            _status = "任务栏外观组件未能连接 · 0x" + error.HResult.ToString("X8");
            NativePopupMenu.Refresh();
        }
    }

    private void Publish()
    {
        if (_disposed || _view == null) return;
        var appearance = MenuAppearanceSettings.Current;
        bool dark = SystemTheme.MenuDark;
        var (surface,glow) = MenuAppearanceSettings.Colors(appearance,dark);
        int revision = _revision += 2;
        _view.Write(40,revision - 1);
        Thread.MemoryBarrier();
        _view.Write(0,0x4c4d5442);
        _view.Write(4,1);
        _view.Write(8,Environment.ProcessId);
        _view.Write(12,Enabled ? 1 : 0);
        _view.Write(16,(int)appearance.Blur);
        _view.Write(20,Motion ? 1 : 0);
        _view.Write(24,dark ? 1 : 0);
        _view.Write(28,Pack(surface));
        _view.Write(32,Pack(glow));
        Thread.MemoryBarrier();
        _view.Write(40,revision);
        _changed?.Set();
    }

    private static uint Pack(Color color) => (uint)color.A << 24 | (uint)color.R << 16 | (uint)color.G << 8 | color.B;

    private static InitializeTaskbar LoadNative() => NativeTaskbarComponent.Export<InitializeTaskbar>("InitializeLightTaskbar");

    private void ReleaseConnection()
    {
        _generation++;
        _view?.Dispose(); _mapping?.Dispose(); _changed?.Dispose();
        _view = null; _mapping = null; _changed = null; _explorer = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        Enabled = false;
        Publish();
        _disposed = true;
        _watch.Stop();
        SystemTheme.Changed -= Publish;
        MenuAppearanceSettings.Changed -= Publish;
        ReleaseConnection();
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int InitializeTaskbar(uint explorer);
}
