using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace FeatherPlayer;

internal static class NativeMenuSurface
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ApplySurface(IntPtr window, int dark, int blur, uint color, int layered);
    private static readonly Lazy<ApplySurface> Surface = new(Load);

    internal static void Apply(IntPtr window, bool dark, int blur, uint color, bool layered) =>
        Surface.Value(window, dark ? 1 : 0, blur, color, layered ? 1 : 0);

    private static ApplySurface Load()
    {
        using var source = typeof(NativeMenuSurface).Assembly.GetManifestResourceStream("LightSuite.Menu.dll")!;
        string hash = Convert.ToHexString(SHA256.HashData(source));
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LightSuite", "Native", hash);
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "LightSuiteMenu.dll");
        if (!File.Exists(path))
        {
            string temporary = path + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                source.Position = 0;
                using (var output = File.Create(temporary)) source.CopyTo(output);
                try { File.Move(temporary, path); }
                catch (IOException) when (File.Exists(path)) { }
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        IntPtr module = NativeLibrary.Load(path);
        return Marshal.GetDelegateForFunctionPointer<ApplySurface>(NativeLibrary.GetExport(module, "ApplyMenuSurface"));
    }
}
