using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace FeatherPlayer;
internal sealed class WidgetLayoutWindow : Window
{
    internal WidgetLayoutWindow(TaskbarWidget widget, Action save)
    {
        Title = "任务栏歌词与播放组件"; Width = 520; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterScreen; ShowInTaskbar = false;
        NativePopupMenu.InstallResources(this);
        SetResourceReference(BackgroundProperty, "MenuBackground"); SetResourceReference(ForegroundProperty, "MenuInk");
        FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 13;
        Resources.Add(typeof(Button), Resources["LightButton"]);
        var original = widget.Layout;
        bool accepted = false, updating = true;
        int alignment = original.VerticalAlignment;
        var sliders = new Dictionary<string, Slider>();
        var rows = new Dictionary<string, FrameworkElement>();
        var panel = new StackPanel { Margin = new Thickness(22) }; Content = panel;
        panel.Children.Add(new TextBlock { Text = "歌词与播放组件", FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
        panel.Children.Add(new TextBlock { Text = "蓝框为歌词，橙框为播放组件。调整即时预览。", Margin = new Thickness(0, 0, 0, 12) });
        var fill = new CheckBox { Content = "歌词填满任务栏可用空间", IsChecked = original.FillAvailable, Margin = new Thickness(0, 0, 0, 12) };
        fill.SetResourceReference(StyleProperty, "LightCheck"); panel.Children.Add(fill);
        void Apply()
        {
            if (updating) return;
            widget.ApplyLayout(new WidgetLayout(sliders["width"].Value, sliders["left"].Value, sliders["tx"].Value, sliders["ty"].Value,
                sliders["bx"].Value, sliders["by"].Value, sliders["textwidth"].Value, original.ShowCover, fill.IsChecked == true, alignment));
        }
        void Add(string key, string title, double value, double min, double max)
        {
            var row = new Grid { Margin = new Thickness(0, 5, 0, 5) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(125) }); row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
            row.Children.Add(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center });
            var slider = new Slider { Minimum = min, Maximum = Math.Max(min, max), Value = value, TickFrequency = 1, IsSnapToTickEnabled = true, IsMoveToPointEnabled = true, Margin = new Thickness(8, 0, 8, 0) };
            slider.SetResourceReference(StyleProperty, "LightSlider");
            var number = new TextBlock { Text = value < 0 ? "自动" : slider.Value.ToString("0"), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(slider, 1); Grid.SetColumn(number, 2); row.Children.Add(slider); row.Children.Add(number);
            panel.Children.Add(row); sliders[key] = slider; rows[key] = row;
            slider.ValueChanged += (_, _) => { number.Text = slider.Value < 0 ? "自动" : slider.Value.ToString("0"); Apply(); };
        }
        Add("width", "组件宽度", original.Width, 220, 1600);
        Add("left", "组件 X", original.Left, -1, 6000);
        Add("tx", "歌词 X", original.TextX, 0, 1600);
        Add("ty", "歌词 Y", original.TextY, 0, widget.YRanges.Text);
        Add("bx", "播放组件 X", original.ButtonsX, -1, 1600);
        Add("by", "播放组件 Y", original.ButtonsY, 0, widget.YRanges.Buttons);
        Add("textwidth", "歌词宽度", original.TextWidth, -1, 1600);
        void Visibility()
        {
            rows["width"].Visibility = rows["textwidth"].Visibility = fill.IsChecked == true ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
            rows["ty"].Visibility = alignment < 0 && widget.YRanges.Text > 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            rows["by"].Visibility = alignment < 0 && widget.YRanges.Buttons > 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        }
        fill.Checked += (_, _) => { Visibility(); Apply(); }; fill.Unchecked += (_, _) => { Visibility(); Apply(); };
        panel.Children.Add(new TextBlock { Text = "Y 坐标对齐", Margin = new Thickness(0, 12, 0, 8) });
        var modes = new UniformGrid { Columns = 4 };
        var choices = new List<RadioButton>();
        string group = "Align" + Guid.NewGuid().ToString("N");
        string[] names = { "自由调整", "顶部对齐", "垂直居中", "底部对齐" };
        for (int i = 0; i < names.Length; i++)
        {
            int mode = i - 1;
            var choice = new RadioButton { Content = names[i], GroupName = group, IsChecked = alignment == mode, Margin = new Thickness(0, 0, 5, 0) };
            choice.SetResourceReference(StyleProperty, "LightChoice");
            choice.Checked += (_, _) => { alignment = mode; Visibility(); Apply(); };
            choices.Add(choice); modes.Children.Add(choice);
        }
        panel.Children.Add(modes);
        var sides = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 12) };
        void SideButton(string name, bool left)
        {
            var button = new Button { Content = name, Margin = new Thickness(0, 0, 8, 0) };
            button.Click += (_, _) => { updating = true; sliders["tx"].Value = left ? 8 : original.ShowCover ? 124 : 86; sliders["bx"].Value = left ? -1 : 8; updating = false; Apply(); };
            sides.Children.Add(button);
        }
        SideButton("歌词在左", true); SideButton("歌词在右", false); panel.Children.Add(sides);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var reset = new Button { Content = "恢复默认", Margin = new Thickness(0, 0, 8, 0) };
        reset.Click += (_, _) =>
        {
            updating = true; fill.IsChecked = true;
            var defaults = new Dictionary<string, double> { ["width"] = 300, ["left"] = -1, ["tx"] = 8, ["ty"] = 2, ["bx"] = -1, ["by"] = 6, ["textwidth"] = -1 };
            foreach (var pair in defaults) sliders[pair.Key].Value = pair.Value;
            alignment = 1; choices[2].IsChecked = true; updating = false; Visibility(); Apply();
        };
        var cancel = new Button { Content = "取消", Margin = new Thickness(0, 0, 8, 0) }; cancel.Click += (_, _) => Close();
        var ok = new Button { Content = "保存" }; ok.Click += (_, _) => { Apply(); accepted = true; save(); Close(); };
        actions.Children.Add(reset); actions.Children.Add(cancel); actions.Children.Add(ok); panel.Children.Add(actions);
        updating = false; Visibility();
        Loaded += (_, _) => { widget.ShowLayoutBounds(true); ApplyTheme(); };
        Closed += (_, _) => { widget.ShowLayoutBounds(false); if (!accepted) widget.ApplyLayout(original); SystemTheme.Changed -= ApplyTheme; MenuAppearanceSettings.Changed -= ApplyTheme; };
        SystemTheme.Changed += ApplyTheme; MenuAppearanceSettings.Changed += ApplyTheme;
    }
    private void ApplyTheme() { NativePopupMenu.SetPalette(Resources); NativePopupMenu.ApplyBackdrop(this); }
}
