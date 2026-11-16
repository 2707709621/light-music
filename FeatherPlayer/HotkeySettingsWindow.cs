using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FeatherPlayer;

internal sealed class HotkeySettingsWindow : Window
{
    private readonly Dictionary<HotkeyAction, HotkeyBinding?> _values;
    private readonly Dictionary<HotkeyAction, TextBox> _inputs = new();
    internal HotkeySettingsWindow(GlobalHotkeys hotkeys, Action save)
    {
        Title = "快捷键设置 · light music"; Width = 520; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterScreen; ShowInTaskbar = false;
        NativePopupMenu.InstallResources(this);
        SetResourceReference(BackgroundProperty, "MenuBackground"); SetResourceReference(ForegroundProperty, "MenuInk");
        FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 13;
        Resources.Add(typeof(Button), Resources["LightButton"]);
        _values = hotkeys.Bindings.ToDictionary(pair => pair.Key, pair => pair.Value);
        hotkeys.Suspend();
        Closed += (_, _) => hotkeys.Resume();
        var panel = new StackPanel { Margin = new Thickness(22) }; Content = panel;
        panel.Children.Add(new TextBlock { Text = "全局快捷键", FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
        panel.Children.Add(new TextBlock { Text = "点击输入框后按下组合键。清除后该操作不绑定快捷键。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) });
        var status = new TextBlock { Text = hotkeys.RegistrationStatus, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 10) };
        foreach (var action in Enum.GetValues<HotkeyAction>())
        {
            var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(155) });
            row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = GlobalHotkeys.Name(action), VerticalAlignment = VerticalAlignment.Center });
            var input = new TextBox { IsReadOnly = true, MinHeight = 32, Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 0, 8, 0) };
            input.SetResourceReference(ForegroundProperty, "MenuInk");
            input.Background = Brushes.Transparent;
            input.Text = _values[action]?.Display ?? "未设置";
            _inputs[action] = input;
            input.PreviewKeyDown += (_, e) =>
            {
                Key key = e.Key == Key.System ? e.SystemKey : e.Key;
                if (key is Key.Tab or Key.Escape) return;
                e.Handled = true;
                if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.ImeProcessed or Key.DeadCharProcessed) return;
                var modifiers = Keyboard.Modifiers;
                if (modifiers == ModifierKeys.None && key is Key.Back or Key.Delete) { Set(action, null); return; }
                if (modifiers == ModifierKeys.None) { status.Text = "请同时按住 Ctrl、Alt、Shift 或 Win。"; return; }
                uint flags = ((modifiers & ModifierKeys.Alt) != 0 ? 1u : 0) | ((modifiers & ModifierKeys.Control) != 0 ? 2u : 0) |
                    ((modifiers & ModifierKeys.Shift) != 0 ? 4u : 0) | ((modifiers & ModifierKeys.Windows) != 0 ? 8u : 0);
                Set(action, new HotkeyBinding(flags, KeyInterop.VirtualKeyFromKey(key)));
                status.Text = "";
            };
            Grid.SetColumn(input, 1); row.Children.Add(input);
            var clear = new Button { Content = "清除", Padding = new Thickness(8, 5, 8, 5) };
            clear.Click += (_, _) => Set(action, null); Grid.SetColumn(clear, 2); row.Children.Add(clear); panel.Children.Add(row);
        }
        panel.Children.Add(status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var reset = new Button { Content = "恢复默认", Margin = new Thickness(0, 0, 8, 0) };
        reset.Click += (_, _) => { foreach (var pair in GlobalHotkeys.Defaults()) Set(pair.Key, pair.Value); status.Text = "点击保存后生效。"; };
        var cancel = new Button { Content = "取消", Margin = new Thickness(0, 0, 8, 0) }; cancel.Click += (_, _) => Close();
        var apply = new Button { Content = "保存" };
        apply.Click += (_, _) =>
        {
            if (!hotkeys.TryApply(_values, out string error)) { status.Text = error; hotkeys.Suspend(); return; }
            save(); Close();
        };
        buttons.Children.Add(reset); buttons.Children.Add(cancel); buttons.Children.Add(apply); panel.Children.Add(buttons);
    }
    private void Set(HotkeyAction action, HotkeyBinding? binding) { _values[action] = binding; _inputs[action].Text = binding?.Display ?? "未设置"; }
}
