using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace FeatherPlayer;

/// <summary>Account secrets are encrypted for the current Windows user before reaching disk.</summary>
public static class SessionStore
{
    private static string SessionPath(MusicProvider provider) => provider switch
    {
        MusicProvider.Netease => Path.Combine(AppPaths.DataDirectory, "netease-session.dpapi"),
        MusicProvider.Kugou => Path.Combine(AppPaths.DataDirectory, "kugou-session.dpapi"),
        _ => AppPaths.SessionFile
    };
    public static void Save(AccountSession session) => SaveTo(session, SessionPath(session.Provider));

    // A separate path makes offline DPAPI diagnostics possible without touching a real account.
    internal static void SaveTo(AccountSession session, string filePath)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (string.IsNullOrWhiteSpace(session.Uin) || string.IsNullOrWhiteSpace(session.MusicKey))
            throw new ArgumentException("登录凭证不完整，未保存。", nameof(session));
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(session);
        byte[] encrypted;
        try { encrypted = Transform(plain, protect: true); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        filePath = Path.GetFullPath(filePath);
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        string temporary = filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(encrypted);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static AccountSession? Load(MusicProvider provider = MusicProvider.Qq)
    {
        var session = LoadFrom(SessionPath(provider));
        return session?.Provider == provider ? session : null;
    }

    internal static AccountSession? LoadFrom(string filePath)
    {
        if (!File.Exists(filePath)) return null;
        try
        {
            var info = new FileInfo(filePath);
            if (info.Length is <= 0 or > 1_048_576) return null;
            byte[] plain = Transform(File.ReadAllBytes(filePath), protect: false);
            try
            {
                AccountSession? session = JsonSerializer.Deserialize<AccountSession>(plain);
                return session is not null && !string.IsNullOrWhiteSpace(session.Uin) && !string.IsNullOrWhiteSpace(session.MusicKey)
                    ? session : null;
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or Win32Exception or CryptographicException)
        {
            // A moved/corrupted file or a different Windows account cannot restore this session.
            return null;
        }
    }

    public static void Clear(MusicProvider provider = MusicProvider.Qq)
    {
        string path = SessionPath(provider);
        if (File.Exists(path)) File.Delete(path);
    }

    private static byte[] Transform(byte[] input, bool protect)
    {
        var source = new DataBlob { Length = input.Length, Data = Marshal.AllocHGlobal(input.Length) };
        DataBlob result = default;
        try
        {
            Marshal.Copy(input, 0, source.Data, input.Length);
            // CRYPTPROTECT_UI_FORBIDDEN; deliberately no LOCAL_MACHINE flag.
            bool succeeded = protect
                ? CryptProtectData(ref source, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out result)
                : CryptUnprotectData(ref source, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out result);
            if (!succeeded) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法使用当前 Windows 用户保护登录凭证。");
            if (result.Length < 0 || result.Length > 1_048_576) throw new CryptographicException("登录凭证大小无效。");
            byte[] output = new byte[result.Length];
            Marshal.Copy(result.Data, output, 0, output.Length);
            return output;
        }
        finally
        {
            if (source.Data != IntPtr.Zero)
            {
                Marshal.Copy(new byte[source.Length], 0, source.Data, source.Length);
                Marshal.FreeHGlobal(source.Data);
            }
            if (result.Data != IntPtr.Zero)
            {
                if (!protect && result.Length is > 0 and <= 1_048_576)
                    Marshal.Copy(new byte[result.Length], 0, result.Data, result.Length);
                LocalFree(result.Data);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { public int Length; public IntPtr Data; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob source, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob result);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob source, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob result);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
