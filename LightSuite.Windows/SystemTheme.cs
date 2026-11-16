using System;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Media;
using MaterialColorUtilities.Palettes;
using MaterialColorUtilities.ColorAppearance;
namespace FeatherPlayer;
internal static class SystemTheme
{
    internal static bool Dark { get; private set; } = Read("SystemUsesLightTheme");
    internal static bool MenuDark { get; private set; } = Read("AppsUseLightTheme");
    private static bool _tintUi = true;
    internal static bool TintUi
    {
        get => _tintUi;
        set { if (_tintUi == value) return; _tintUi = value; UpdateBrushes(); }
    }
    internal static Color Accent { get; private set; } = Color.FromRgb(100, 116, 135);
    internal static Brush Ink { get; private set; } = MakeInk(Dark, false);
    internal static Brush Secondary { get; private set; } = MakeInk(Dark, true);
    internal static Brush MenuInk { get; private set; } = MakeInk(MenuDark, false);
    internal static Brush MenuSecondary { get; private set; } = MakeInk(MenuDark, true);
    internal static event Action? Changed;
    private static bool? _menuOverride;
    internal static void SetMenuTheme(bool? dark)
    {
        if (_menuOverride == dark && MenuDark == (dark ?? Read("AppsUseLightTheme"))) return;
        _menuOverride = dark;
        MenuDark = dark ?? Read("AppsUseLightTheme");
        UpdateBrushes();
    }

    static SystemTheme()
    {
        SystemEvents.UserPreferenceChanged += (_, _) => Application.Current?.Dispatcher.BeginInvoke((Action)(() =>
        {
            bool dark = Read("SystemUsesLightTheme"), menuDark = _menuOverride ?? Read("AppsUseLightTheme");
            if (dark == Dark && menuDark == MenuDark) return;
            Dark = dark; MenuDark = menuDark;
            UpdateBrushes();
        }));
    }

    internal static void SetAccent(Color? color)
    {
        var next = color ?? Color.FromRgb(100, 116, 135);
        if (Accent == next) return;
        Accent = next;
        UpdateBrushes();
    }

    private static void UpdateBrushes()
    {
        Ink = MakeInk(Dark, false); Secondary = MakeInk(Dark, true);
        MenuInk = MakeInk(MenuDark, false); MenuSecondary = MakeInk(MenuDark, true);
        Changed?.Invoke();
    }

    internal static Color Blend(Color color, Color target, double amount) => Color.FromRgb(
        (byte)Math.Round(color.R + (target.R - color.R) * amount),
        (byte)Math.Round(color.G + (target.G - color.G) * amount),
        (byte)Math.Round(color.B + (target.B - color.B) * amount));

    private static Brush MakeInk(bool dark, bool secondary)
    {
        byte neutral = dark ? secondary ? (byte)185 : (byte)245 : secondary ? (byte)98 : (byte)32;
        var color = TintUi ? Tone(dark ? secondary ? 78u : 92u : secondary ? 38u : 20u, secondary ? 20 : 32)
            : Color.FromRgb(neutral, neutral, neutral);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    internal static Color Tone(uint tone, double chroma)
    {
        uint argb = 0xff000000u | (uint)Accent.R << 16 | (uint)Accent.G << 8 | Accent.B;
        var source = Hct.FromInt(argb);
        uint value = TonalPalette.FromHueAndChroma(source.Hue,Math.Min(source.Chroma,chroma))[tone];
        return Color.FromRgb((byte)(value >> 16),(byte)(value >> 8),(byte)value);
    }

    private static bool Read(string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue(name) is int value && value == 0;
        }
        catch { return false; }
    }
}
