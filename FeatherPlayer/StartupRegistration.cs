using System;
using System.IO;
using Microsoft.Win32;

namespace FeatherPlayer;

internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "LightMusic";
    internal static string Command(string executable)
    {
        if(!Path.IsPathFullyQualified(executable) || executable.Contains('"') || executable.Contains('\r') || executable.Contains('\n'))
            throw new ArgumentException("自启动需要有效的程序绝对路径。");
        return "\"" + Path.GetFullPath(executable) + "\" --startup";
    }
    internal static bool IsEnabled
    {
        get
        {
            try { using var key=Registry.CurrentUser.OpenSubKey(RunKey); return key?.GetValue(ValueName) is string value && string.Equals(value,Command(Environment.ProcessPath!),StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }
    }
    internal static void SetEnabled(bool enabled)
    {
        using var key=Registry.CurrentUser.CreateSubKey(RunKey,true);
        if(enabled)key.SetValue(ValueName,Command(Environment.ProcessPath!),RegistryValueKind.String);
        else key.DeleteValue(ValueName,false);
    }
    internal static void RefreshEnabledRegistration()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        if (key?.GetValue(ValueName) is string command && command.EndsWith(" --startup", StringComparison.OrdinalIgnoreCase))
            SetEnabled(true);
    }
}
