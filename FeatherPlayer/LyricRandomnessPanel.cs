using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace FeatherPlayer;

internal sealed class LyricRandomnessPanel : StackPanel
{
    internal LyricRandomnessPanel(Action save)
    {
        Width = 258; Margin = new Thickness(8);
        var toggle = new CheckBox { Content = "随机排布", IsChecked = LyricStyleSettings.RandomLayout, Margin = new Thickness(0, 0, 0, 12) };
        toggle.SetResourceReference(StyleProperty, "LightCheck");
        Children.Add(toggle);
        var label = new TextBlock { Margin = new Thickness(0, 0, 0, 6) };
        label.SetResourceReference(TextBlock.ForegroundProperty, "MenuInk");
        Children.Add(label);
        var slider = new Slider { Minimum = 0, Maximum = 100, Value = LyricStyleSettings.Randomness * 100, IsMoveToPointEnabled = true, Height = 24 };
        slider.SetResourceReference(StyleProperty, "LightSlider");
        Children.Add(slider);
        var hint = new TextBlock { Text = "同时调整位置、旋转与缩放的随机幅度", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), FontSize = 11 };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "MenuMuted"); Children.Add(hint);
        void Refresh()
        {
            slider.IsEnabled = toggle.IsChecked == true;
            label.Text = "随机强度  " + slider.Value.ToString("0") + "%";
        }
        toggle.Checked += (_, _) => { LyricStyleSettings.RandomLayout = true; Refresh(); save(); };
        toggle.Unchecked += (_, _) => { LyricStyleSettings.RandomLayout = false; Refresh(); save(); };
        slider.ValueChanged += (_, _) => { LyricStyleSettings.Randomness = slider.Value / 100; Refresh(); };
        slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => save()));
        slider.AddHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler((_, _) => save()), true);
        slider.KeyUp += (_, _) => save();
        Refresh();
    }
}
