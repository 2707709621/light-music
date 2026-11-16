using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace FeatherPlayer;

internal enum HotkeyAction { PlayPause, Next, Previous, Favorite, VolumeUp, VolumeDown, Mute, ShowMenu, PlaybackMode }
internal sealed record HotkeyBinding(uint Modifiers, int Key)
{
    internal string Display => string.Join(" + ", new[]
    {
        (Modifiers & 2) != 0 ? "Ctrl" : null, (Modifiers & 1) != 0 ? "Alt" : null,
        (Modifiers & 4) != 0 ? "Shift" : null, (Modifiers & 8) != 0 ? "Win" : null, KeyName(Key)
    }.Where(value => value != null));
    private static string KeyName(int key) => KeyInterop.KeyFromVirtualKey(key) switch
    {
        System.Windows.Input.Key.Space => "空格",
        System.Windows.Input.Key.Left => "←",
        System.Windows.Input.Key.Right => "→",
        System.Windows.Input.Key.Up => "↑",
        System.Windows.Input.Key.Down => "↓",
        var value => value.ToString()
    };
}

internal sealed class GlobalHotkeys : IDisposable
{
    private readonly HwndSource _source;
    private readonly Action<HotkeyAction> _execute;
    private readonly HashSet<int> _registered = new();
    private Dictionary<HotkeyAction, HotkeyBinding?> _bindings = Defaults();
    private bool _suspended, _disposed;
    internal IReadOnlyDictionary<HotkeyAction, HotkeyBinding?> Bindings => _bindings;
    internal string RegistrationStatus { get; private set; } = "";
    internal static string Name(HotkeyAction action) => action switch
    {
        HotkeyAction.PlayPause => "播放 / 暂停",
        HotkeyAction.Next => "下一首",
        HotkeyAction.Previous => "上一首",
        HotkeyAction.Favorite => "收藏 / 取消收藏",
        HotkeyAction.VolumeUp => "增大音量",
        HotkeyAction.VolumeDown => "减小音量",
        HotkeyAction.Mute => "静音 / 恢复音量",
        HotkeyAction.ShowMenu => "打开程序菜单",
        _ => "切换播放模式"
    };
    internal static Dictionary<HotkeyAction, HotkeyBinding?> Defaults() => Enum.GetValues<HotkeyAction>().ToDictionary(action => action, action => action switch
    {
        HotkeyAction.PlayPause => new HotkeyBinding(3, 0x20),
        HotkeyAction.Next => new HotkeyBinding(3, 0x27),
        HotkeyAction.Previous => new HotkeyBinding(3, 0x25),
        _ => null
    });
    internal GlobalHotkeys(IntPtr owner, Action<HotkeyAction> execute)
    {
        _source = HwndSource.FromHwnd(owner) ?? throw new InvalidOperationException("快捷键窗口尚未创建。");
        _execute = execute;
        _source.AddHook(Message);
    }
    internal void Load(Dictionary<HotkeyAction, HotkeyBinding?>? saved)
    {
        _bindings = Defaults();
        if (saved != null)
            foreach (var pair in saved)
                if (Enum.IsDefined(pair.Key) && (pair.Value == null || Valid(pair.Value))) _bindings[pair.Key] = pair.Value;
        Resume();
    }
    private static bool Valid(HotkeyBinding value) => value.Modifiers is > 0 and <= 15 && value.Key is > 0 and < 255 &&
        value.Key is not (0x10 or 0x11 or 0x12 or 0x5b or 0x5c or >= 0xa0 and <= 0xa5);
    internal bool TryApply(Dictionary<HotkeyAction, HotkeyBinding?> values, out string error)
    {
        error = "";
        foreach (var pair in values)
        {
            if (!Enum.IsDefined(pair.Key) || pair.Value != null && !Valid(pair.Value)) { error = "快捷键需要至少一个 Ctrl、Alt、Shift 或 Win 修饰键。"; return false; }
        }
        var duplicate = values.Where(pair => pair.Value != null).GroupBy(pair => pair.Value).FirstOrDefault(group => group.Count() > 1);
        if (duplicate != null) { error = "以下操作使用了相同快捷键：" + string.Join("、", duplicate.Select(pair => Name(pair.Key))); return false; }
        UnregisterAll();
        foreach (var pair in values)
        {
            if (pair.Value == null) continue;
            if (!Register(pair.Key, pair.Value))
            {
                error = Name(pair.Key) + "的快捷键已被系统或其他程序占用，请换一个组合。";
                UnregisterAll();
                if (!_suspended) RegisterCurrent();
                return false;
            }
        }
        _bindings = new(values);
        RegistrationStatus = "";
        return true;
    }
    internal void Suspend() { _suspended = true; UnregisterAll(); }
    internal void Resume() { if (_disposed) return; _suspended = false; UnregisterAll(); RegisterCurrent(); }
    private void RegisterCurrent()
    {
        var failed = new List<string>();
        foreach (var pair in _bindings)
            if (pair.Value != null && !Register(pair.Key, pair.Value)) failed.Add(Name(pair.Key));
        RegistrationStatus = failed.Count == 0 ? "" : "快捷键被占用：" + string.Join("、", failed);
    }
    private bool Register(HotkeyAction action, HotkeyBinding binding)
    {
        int id = 0x4100 + (int)action;
        if (!RegisterHotKey(_source.Handle, id, binding.Modifiers | 0x4000, (uint)binding.Key)) return false;
        _registered.Add(id);
        return true;
    }
    private void UnregisterAll()
    {
        foreach (int id in _registered) UnregisterHotKey(_source.Handle, id);
        _registered.Clear();
    }
    private IntPtr Message(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0312 && _registered.Contains(wParam.ToInt32()))
        {
            handled = true;
            if (!_suspended && !_disposed) _execute((HotkeyAction)(wParam.ToInt32() - 0x4100));
        }
        return IntPtr.Zero;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        UnregisterAll();
        _source.RemoveHook(Message);
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr window, int id);
}
