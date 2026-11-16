using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace FeatherPlayer;

internal static class NativeTaskbarComponent
{
    private static readonly object Sync = new();
    private static IntPtr _module;

    internal static T Export<T>(string name) where T : Delegate
    {
        lock (Sync)
        {
            if (_module == IntPtr.Zero)
            {
                using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("FeatherPlayer.Native.LightMusicTaskbar.dll")
                    ?? throw new DllNotFoundException("原生组件缺失");
                using var buffer = new MemoryStream();
                resource.CopyTo(buffer);
                byte[] bytes = buffer.ToArray();
                string hash = Convert.ToHexString(SHA256.HashData(bytes));
                string folder = Path.Combine(AppPaths.DataDirectory,"Native",hash);
                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder,"LightMusicTaskbar.dll");
                bool valid = File.Exists(path) && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) == hash;
                if (!valid)
                {
                    string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    File.WriteAllBytes(temporary,bytes); File.Move(temporary,path,true);
                }
                // Native callbacks can outlive their caller; keep this module for the process lifetime.
                _module = NativeLibrary.Load(path);
            }
            return Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_module,name));
        }
    }
}
