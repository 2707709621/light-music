using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace FeatherPlayer;

public enum MenuBlurMode { None, Soft, Acrylic }
public sealed record MenuAppearance(double Opacity = .65, MenuBlurMode Blur = MenuBlurMode.Acrylic, double Glow = .25);

internal static class MenuAppearanceSettings
{
    internal static MenuAppearance Current { get; private set; } = new();
    internal static event Action? Changed;
    internal static (Color Surface, Color Glow) Colors(MenuAppearance appearance, bool dark)
    {
        var surface = SystemTheme.Tone(dark ? 13u : 98u,6 + appearance.Glow * 10);
        surface.A = (byte)Math.Round(appearance.Opacity * 255);
        var glow = SystemTheme.Accent;
        glow.A = (byte)Math.Round(appearance.Opacity * appearance.Glow * (dark ? 160 : 120));
        return (surface,glow);
    }
    internal static void Set(MenuAppearance value)
    {
        var next = new MenuAppearance(
            double.IsFinite(value.Opacity) ? Math.Clamp(value.Opacity,0,1) : .65,
            Enum.IsDefined(value.Blur) ? value.Blur : MenuBlurMode.Acrylic,
            double.IsFinite(value.Glow) ? Math.Clamp(value.Glow,0,1) : .25);
        if (Current == next) return;
        Current = next;
        Changed?.Invoke();
    }
}

internal sealed class MenuAppearancePanel : StackPanel
{
    private readonly Slider _opacity, _glow;
    private readonly RadioButton[] _blur = new RadioButton[3];
    private readonly Action _save;
    private bool _loading = true;
    internal MenuAppearancePanel(Action save)
    {
        _save = save;
        Width = 258; Margin = new Thickness(4,8,4,2);
        var options = MenuAppearanceSettings.Current;
        _opacity = AddSlider("菜单不透明度", options.Opacity);
        _glow = AddSlider("封面色光效", options.Glow);
        var label = new TextBlock { Text = "背景模糊", Margin = new Thickness(0,3,0,7) };
        label.SetResourceReference(TextBlock.ForegroundProperty,"MenuInk"); Children.Add(label);
        var row = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3 };
        string group = "MenuBlur" + Guid.NewGuid().ToString("N");
        var names = new[] { "关闭", "柔和", "磨砂" };
        for (int i = 0; i < _blur.Length; i++)
        {
            var button = _blur[i] = new RadioButton { Content = names[i], GroupName = group, IsChecked = (int)options.Blur == i, Margin = new Thickness(1,0,5,0) };
            button.SetResourceReference(StyleProperty,"LightChoice");
            button.Checked += (_, _) => { if (!_loading) { Apply(); _save(); } };
            row.Children.Add(button);
        }
        Children.Add(row);
        _opacity.ValueChanged += (_, _) => Apply();
        _glow.ValueChanged += (_, _) => Apply();
        // Commit once at the end of pointer edits, keeping live preview inexpensive.
        AddHandler(Thumb.DragCompletedEvent,new DragCompletedEventHandler((_, _) => _save()));
        AddHandler(System.Windows.Input.Mouse.PreviewMouseUpEvent,new System.Windows.Input.MouseButtonEventHandler((_, _) => _save()),true);
        KeyUp += (_, _) => _save();
        _loading = false;
    }

    private Slider AddSlider(string text, double value)
    {
        var header = new DockPanel();
        var number = new TextBlock { Text = (value * 100).ToString("0") + "%", HorizontalAlignment = HorizontalAlignment.Right };
        number.SetResourceReference(TextBlock.ForegroundProperty,"MenuMuted");
        DockPanel.SetDock(number,Dock.Right); header.Children.Add(number);
        var label = new TextBlock { Text = text };
        label.SetResourceReference(TextBlock.ForegroundProperty,"MenuInk"); header.Children.Add(label);
        Children.Add(header);
        var slider = new Slider { Minimum = 0, Maximum = 100, Value = value * 100, Height = 23, Margin = new Thickness(0,3,0,9), IsMoveToPointEnabled = true };
        slider.SetResourceReference(StyleProperty,"LightSlider");
        slider.ValueChanged += (_, _) => number.Text = slider.Value.ToString("0") + "%";
        Children.Add(slider);
        return slider;
    }

    private void Apply()
    {
        if (_loading) return;
        int mode = Array.FindIndex(_blur, button => button?.IsChecked == true);
        MenuAppearanceSettings.Set(new(_opacity.Value / 100,(MenuBlurMode)Math.Max(0,mode),_glow.Value / 100));
        NativePopupMenu.Refresh();
    }
}
