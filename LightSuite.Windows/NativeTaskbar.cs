using System;
using System.Runtime.InteropServices;

namespace FeatherPlayer;

// Embedding helpers for the player's own taskbar child HWND.
internal static class NativeTaskbar
{
    internal const int GwlStyle = -16, GwlExStyle = -20;
    internal const long WsChild = 0x40000000L, WsPopup = 0x80000000L, WsVisible = 0x10000000L;
    internal const long WsExNoActivate = 0x08000000L, WsExToolWindow = 0x80L;
    internal const uint SwpNoActivate = 0x10, SwpFrameChanged = 0x20, SwpNoMove = 2, SwpNoSize = 1;
    internal const int SwHide = 0, SwShowNoActivate = 8;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect { public int Left, Top, Right, Bottom; public int Width => Right - Left; public int Height => Bottom - Top; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct GuiInfo { public int Size; public uint Flags; public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret; public Rect CaretRect; }
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GuiInfo info);
    internal static bool MenuActive()
    {
        if(NativePopupMenu.IsOpen)return true;
        var info=new GuiInfo{Size=Marshal.SizeOf<GuiInfo>()};
        if(GetGUIThreadInfo(0,ref info)&&(info.Flags&0x1C)!=0)return true;
        var menu=FindWindow("#32768",null);return menu!=IntPtr.Zero&&IsWindowVisible(menu);
    }

    internal delegate void WinEventDelegate(IntPtr hook, uint eventType, IntPtr hwnd, int objectId, int childId, uint threadId, uint time);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr FindWindow(string? className, string? title);
    [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool IsChild(IntPtr parent, IntPtr child);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] internal static extern bool GetClientRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] internal static extern bool ClientToScreen(IntPtr hwnd, ref Point point);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern IntPtr GetParent(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] internal static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] internal static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] internal static extern IntPtr SetWinEventHook(uint firstEvent, uint lastEvent, IntPtr module, WinEventDelegate callback, uint processId, uint threadId, uint flags);
    [DllImport("user32.dll")] internal static extern bool UnhookWinEvent(IntPtr hook);

    internal static bool Attach(IntPtr child, IntPtr parent)
    {
        var style = GetWindowLongPtr(child, GwlStyle).ToInt64();
        SetWindowLongPtr(child, GwlStyle, new IntPtr((style & ~WsPopup) | WsChild));
        var extended = GetWindowLongPtr(child, GwlExStyle).ToInt64();
        SetWindowLongPtr(child, GwlExStyle, new IntPtr(extended | WsExNoActivate | WsExToolWindow));
        SetParent(child, parent);
        SetWindowPos(child, IntPtr.Zero, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpFrameChanged);
        return GetParent(child) == parent;
    }

    internal static void Detach(IntPtr child)
    {
        if (!IsWindow(child)) return;
        ShowWindow(child, SwHide);
        SetParent(child, IntPtr.Zero);
        var style = GetWindowLongPtr(child, GwlStyle).ToInt64();
        SetWindowLongPtr(child, GwlStyle, new IntPtr((style & ~WsChild) | WsPopup));
    }
}
