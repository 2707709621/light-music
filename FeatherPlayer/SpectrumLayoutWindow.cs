using System;
using System.Windows;
using System.Windows.Controls;

namespace FeatherPlayer;

internal sealed class SpectrumLayoutWindow : Window
{
    internal SpectrumLayoutWindow(SpectrumController controller)
    {
        Title = "频谱位置与尺寸"; Width = 430; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;
        NativePopupMenu.InstallResources(this);
        SetResourceReference(BackgroundProperty,"MenuBackground");
        SetResourceReference(ForegroundProperty,"MenuInk");
        FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI"); FontSize = 12;
        Resources.Add(typeof(Button),Resources["LightButton"]);
        var panel = new StackPanel { Margin = new Thickness(22) }; Content = panel;
        panel.Children.Add(new TextBlock { Text = "频谱位置与尺寸",FontSize = 19,Margin = new Thickness(0,0,0,8) });
        panel.Children.Add(new TextBlock
        {
            Text = "拖动桌面上的频谱预览，或调整下方数值。保存后记住位置。",
            TextWrapping = TextWrapping.Wrap,Margin = new Thickness(0,0,0,14)
        });
        var centered = new CheckBox { Content = "沿任务栏居中",Margin = new Thickness(0,0,0,12) };
        panel.Children.Add(centered);
        bool updating = true;
        Slider Add(string text, double minimum, double maximum)
        {
            var row = new Grid { Margin = new Thickness(0,6,0,6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });
            var label = new TextBlock { Text = text,VerticalAlignment = VerticalAlignment.Center };
            var slider = new Slider
            {
                Minimum = minimum,Maximum = maximum,TickFrequency = 1,IsSnapToTickEnabled = true,
                IsMoveToPointEnabled = true,Height = 24,Margin = new Thickness(4,0,4,0)
            };
            slider.SetResourceReference(StyleProperty,"LightSlider");
            var value = new TextBox { VerticalAlignment = VerticalAlignment.Center,MaxLength = 12,Padding = new Thickness(4,3,4,3) };
            value.SetResourceReference(StyleProperty,"LightSearchTextBox");
            void Commit()
            {
                if (double.TryParse(value.Text,out double number) && double.IsFinite(number))
                    slider.Value = Math.Clamp(number,minimum,maximum);
                value.Text = slider.Value.ToString("0");
            }
            value.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Enter) { Commit(); e.Handled = true; }
            };
            value.LostKeyboardFocus += (_, _) => Commit();
            slider.ValueChanged += (_, _) => value.Text = slider.Value.ToString("0");
            value.Text = slider.Value.ToString("0");
            Grid.SetColumn(slider,1); Grid.SetColumn(value,2);
            row.Children.Add(label); row.Children.Add(slider); row.Children.Add(value); panel.Children.Add(row);
            return slider;
        }
        var offset = Add("沿任务栏偏移",-8000,8000);
        var gap = Add("距任务栏间隔",0,8000);
        var width = Add("频谱宽度",120,1400);
        var height = Add("频谱高度",20,240);
        void Refresh()
        {
            updating = true;
            var options = controller.Options;
            centered.IsChecked = options.Centered; offset.IsEnabled = !options.Centered;
            offset.Value = options.Offset; gap.Value = options.Gap;
            width.Value = options.Width; height.Value = options.Height;
            updating = false;
        }
        void Apply()
        {
            if (updating) return;
            controller.Configure(controller.Options with
            {
                Centered = centered.IsChecked == true,Offset = offset.Value,Gap = gap.Value,Width = width.Value,Height = height.Value
            });
        }
        centered.Checked += (_, _) => Apply(); centered.Unchecked += (_, _) => Apply();
        foreach (var slider in new[] { offset,gap,width,height }) slider.ValueChanged += (_, _) => Apply();
        controller.Changed += Refresh;
        Refresh();
        var reset = new Button { Content = "恢复默认位置",Margin = new Thickness(0,12,0,12),HorizontalAlignment = HorizontalAlignment.Left };
        reset.Click += (_, _) => controller.Configure(new SpectrumOptions(Enabled:controller.Options.Enabled));
        panel.Children.Add(reset);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal,HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消",IsCancel = true,MinWidth = 72,Margin = new Thickness(0,0,8,0) };
        var save = new Button { Content = "保存",IsDefault = true,MinWidth = 72 };
        cancel.Click += (_, _) => DialogResult = false; save.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(cancel); buttons.Children.Add(save); panel.Children.Add(buttons);
        void Theme() { NativePopupMenu.SetPalette(Resources); NativePopupMenu.ApplyBackdrop(this); }
        SystemTheme.Changed += Theme; MenuAppearanceSettings.Changed += Theme;
        Loaded += (_, _) => Theme();
        Closed += (_, _) =>
        {
            controller.Changed -= Refresh;
            SystemTheme.Changed -= Theme; MenuAppearanceSettings.Changed -= Theme;
        };
    }
}
