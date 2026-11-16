using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FeatherPlayer;

internal sealed class SearchWindow : Window
{
    private readonly TextBox _input;
    internal string Query => _input.Text.Trim();
    internal SearchWindow()
    {
        Title = "搜索歌曲 · light music"; Width = 350; SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; AllowsTransparency = true;
        Background = Brushes.Transparent; ShowInTaskbar = false; Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 11.5;
        NativePopupMenu.InstallResources(this);
        SetResourceReference(ForegroundProperty, "MenuInk");
        var border = new Border { CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(0), Padding = new Thickness(16) };
        border.SetResourceReference(Border.BackgroundProperty, "MenuBackground");
        Content = border;
        var panel = new StackPanel(); border.Child = panel;
        var heading = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var icon = LineIcons.Create("search", 14, 1.8);
        if (icon is Viewbox view && view.Child is System.Windows.Shapes.Path path) path.SetResourceReference(System.Windows.Shapes.Path.StrokeProperty, "MenuInk");
        icon.Margin = new Thickness(0, 0, 7, 0); heading.Children.Add(icon);
        heading.Children.Add(new TextBlock { Text = "搜索歌曲或歌手", VerticalAlignment = VerticalAlignment.Center });
        heading.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        panel.Children.Add(heading);
        _input = new TextBox { MaxLength = 200, Style = (Style)Resources["LightTextBox"] };
        System.Windows.Automation.AutomationProperties.SetName(_input, "搜索歌曲或歌手");
        panel.Children.Add(_input);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var cancel = new Button { Content = "取消", IsCancel = true, Style = (Style)Resources["LightButton"], Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => DialogResult = false;
        var search = new Button { Content = "搜索", IsDefault = true, Style = (Style)Resources["LightButton"] };
        search.Click += (_, _) => { if (Query.Length > 0) DialogResult = true; };
        buttons.Children.Add(cancel); buttons.Children.Add(search); panel.Children.Add(buttons);
        SystemTheme.Changed += ApplyTheme;
        SourceInitialized += (_, _) => NativePopupMenu.ApplyBackdrop(this);
        Loaded += (_, _) => { NativePopupMenu.ApplyBackdrop(this); _input.Focus(); };
        Closed += (_, _) => SystemTheme.Changed -= ApplyTheme;
    }
    private void ApplyTheme() { NativePopupMenu.SetPalette(Resources); NativePopupMenu.ApplyBackdrop(this); }
}
