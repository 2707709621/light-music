using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace FeatherPlayer;

// Watch only while a menu is visible. Callbacks observe clicks and foreground
// changes without suppressing input or polling the desktop.
internal sealed class MenuDismissal : IDisposable
{
    private readonly Func<IEnumerable<Visual>> _surfaces;
    private readonly Dispatcher _dispatcher;
    private readonly Action _close;
    private readonly MouseHook _mouseCallback;
    private readonly NativeTaskbar.WinEventDelegate _foregroundCallback;
    private readonly DispatcherTimer _foregroundCheck;
    private IntPtr _mouse, _foreground;
    private IntPtr _initialForeground;
    private bool _disposed, _pending, _armed, _activated;

    internal MenuDismissal(Dispatcher dispatcher, Action close, Func<IEnumerable<Visual>> surfaces)
    {
        _dispatcher = dispatcher; _close = close; _surfaces = surfaces;
        _foregroundCheck = new DispatcherTimer(DispatcherPriority.Input,dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(150)
        };
        _foregroundCheck.Tick += (_, _) =>
        {
            _foregroundCheck.Stop();
            if (!_disposed && IsOtherApplication(GetForegroundWindow())) _close();
        };
        _mouseCallback = OnMouse;
        _foregroundCallback = (_, _, hwnd, _, _, _, _) =>
        {
            // Out-of-context WinEvents arrive asynchronously. Ignore obsolete
            // notifications and allow transient shell/IME activation to settle.
            if (_disposed || !_armed || hwnd != GetForegroundWindow()) return;
            _foregroundCheck.Stop();
            if (!IsOtherApplication(hwnd)) _activated = true;
            else if (_activated || hwnd != _initialForeground) _foregroundCheck.Start();
        };
    }

    internal bool Start()
    {
        _mouse = SetWindowsHookEx(14,_mouseCallback,GetModuleHandle(null),0);
        _foreground = NativeTaskbar.SetWinEventHook(3,3,IntPtr.Zero,_foregroundCallback,0,0,0);
        if (_mouse != IntPtr.Zero && _foreground != IntPtr.Zero) return true;
        Dispose(); return false;
    }

    internal void Arm()
    {
        if (_disposed) return;
        _initialForeground = GetForegroundWindow();
        _activated = !IsOtherApplication(_initialForeground);
        _armed = true;
    }

    private bool IsInside(Point point)
    {
        // Resolve HWNDs from the live popup tree, including newly opened menus
        // whose deferred backdrop/animation setup has not run yet.
        foreach (var visual in _surfaces())
            if (PresentationSource.FromVisual(visual) is HwndSource source &&
                NativeTaskbar.IsWindowVisible(source.Handle) &&
                NativeTaskbar.GetWindowRect(source.Handle,out var rect) &&
                point.X >= rect.Left && point.X < rect.Right && point.Y >= rect.Top && point.Y < rect.Bottom)
                return true;
        return false;
    }

    private IntPtr OnMouse(int code, IntPtr message, IntPtr data)
    {
        if (!_disposed && _armed && code >= 0 && message.ToInt32() is 0x201 or 0x204 or 0x207 or 0x20B)
        {
            var point = Marshal.PtrToStructure<MouseData>(data).Point;
            if (!IsInside(point) && !IsInputMethodWindow(WindowFromPoint(point))) QueueOutsideClick(point);
        }
        return CallNextHookEx(_mouse,code,message,data);
    }

    private static bool IsInputMethodWindow(IntPtr hwnd)
    {
        var name = new StringBuilder(256);
        GetClassName(hwnd,name,name.Capacity);
        if (name.ToString().Contains("IME",StringComparison.OrdinalIgnoreCase) || name.ToString().Contains("Candidate",StringComparison.OrdinalIgnoreCase)) return true;
        NativeTaskbar.GetWindowThreadProcessId(hwnd,out uint id);
        try
        {
            using var process = Process.GetProcessById((int)id);
            return process.ProcessName is "TextInputHost" or "ChsIME" or "ChtIME" or "ctfmon";
        }
        catch { return false; }
    }

    private static bool IsOtherApplication(IntPtr hwnd)
    {
        NativeTaskbar.GetWindowThreadProcessId(hwnd,out uint process);
        return hwnd != IntPtr.Zero && process != 0 && process != (uint)Environment.ProcessId && !IsInputMethodWindow(hwnd);
    }

    private void QueueOutsideClick(Point point)
    {
        if (_disposed || _pending || _dispatcher.HasShutdownStarted) return;
        _pending = true;
        _dispatcher.BeginInvoke(DispatcherPriority.Input,(Action)(() =>
        {
            _pending = false;
            if (!_disposed && !IsInside(point) && !IsInputMethodWindow(WindowFromPoint(point))) _close();
        }));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _foregroundCheck.Stop();
        if (_mouse != IntPtr.Zero) UnhookWindowsHookEx(_mouse);
        if (_foreground != IntPtr.Zero) NativeTaskbar.UnhookWinEvent(_foreground);
        _mouse = _foreground = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X,Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseData { public Point Point; public uint Data,Flags,Time; public UIntPtr Extra; }
    private delegate IntPtr MouseHook(int code,IntPtr message,IntPtr data);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int type,MouseHook callback,IntPtr module,uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook,int code,IntPtr message,IntPtr data);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd,StringBuilder name,int count);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
