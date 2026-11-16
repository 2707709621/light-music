using System;
using System.Windows;

namespace FeatherPlayer;

// The controller has no library UI. Keep its state as plain data, without hidden WPF controls/templates.
internal sealed class TextState
{
    internal string Text { get; set; }="";
    internal object? Content { get; set; }
    internal object? ToolTip { get; set; }
    internal Visibility Visibility { get; set; }
}
internal sealed class ListState
{
    internal object? ItemsSource { get; set; }
    internal object? SelectedItem { get; set; }
}
internal sealed class ValueState
{
    private double _value;
    internal Action<double>? Changed;
    internal double Maximum { get; set; }=1;
    internal double Value { get=>_value;set{if(_value==value)return;_value=value;Changed?.Invoke(value);}}
}
internal sealed class CheckState
{
    private bool? _checked=true;
    internal Action? Changed;
    internal bool? IsChecked { get=>_checked;set{if(_checked==value)return;_checked=value;Changed?.Invoke();}}
}
