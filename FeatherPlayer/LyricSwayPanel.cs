using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace FeatherPlayer;

internal sealed class LyricSwayPanel : StackPanel
{
    internal LyricSwayPanel(bool letters, Action save)
    {
        Width = 258; Margin = new Thickness(8);
        var options = letters ? LyricStyleSettings.LetterSway : LyricStyleSettings.WholeSway;
        var enabled = new CheckBox { Content = letters ? "逐字晃动" : "整句晃动", IsChecked = options.Enabled, Margin = new Thickness(0, 0, 0, 10) };
        enabled.SetResourceReference(StyleProperty, "LightCheck"); Children.Add(enabled);
        Slider Add(string title, double value, double min, double max, string format)
        {
            var label = new TextBlock { Margin = new Thickness(0, 6, 0, 4) };
            label.SetResourceReference(TextBlock.ForegroundProperty, "MenuInk"); Children.Add(label);
            var slider = new Slider { Minimum = min, Maximum = max, Value = value, Height = 24, IsMoveToPointEnabled = true };
            slider.SetResourceReference(StyleProperty, "LightSlider"); Children.Add(slider);
            void Text() => label.Text = title + "  " + slider.Value.ToString(format);
            slider.ValueChanged += (_, _) => Text(); Text();
            slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => save()));
            slider.AddHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler((_, _) => save()), true);
            slider.KeyUp += (_, _) => save();
            return slider;
        }
        var amount = Add("幅度", options.Amount, 0, 8, "0.0 'px'");
        var speed = Add("速度", options.Speed, .2, 3, "0.0 '×'");
        void Apply()
        {
            bool active = enabled.IsChecked == true;
            amount.IsEnabled = speed.IsEnabled = active;
            LyricStyleSettings.SetSway(letters, new LyricSwayOptions(active, amount.Value, speed.Value));
        }
        enabled.Checked += (_, _) => { Apply(); save(); };
        enabled.Unchecked += (_, _) => { Apply(); save(); };
        amount.ValueChanged += (_, _) => Apply(); speed.ValueChanged += (_, _) => Apply();
        amount.IsEnabled = speed.IsEnabled = options.Enabled;
    }
}
