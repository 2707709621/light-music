using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace FeatherPlayer;

internal sealed record MenuSeekSource(Func<PlaybackState> Read,Action<double> Seek);

internal sealed class MenuSeekBar : Grid
{
    private readonly MenuSeekSource _source;
    private readonly Slider _slider;
    private readonly TextBlock _position=new(), _duration=new();
    private readonly DispatcherTimer _timer=new(DispatcherPriority.Background){Interval=TimeSpan.FromMilliseconds(250)};
    private bool _editing;
    private string? _editingSong;
    internal MenuSeekBar(MenuSeekSource source)
    {
        _source=source;
        MinWidth=190; Height=28;
        ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
        ColumnDefinitions.Add(new ColumnDefinition());
        ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
        _slider=new Slider{Minimum=0,Maximum=1,Height=22,Margin=new Thickness(5,0,5,0),VerticalAlignment=VerticalAlignment.Center,IsMoveToPointEnabled=true,SmallChange=5,LargeChange=15};
        System.Windows.Automation.AutomationProperties.SetName(_slider, "播放进度");
        _slider.SetResourceReference(StyleProperty,"LightSlider");
        SetColumn(_slider,1);Children.Add(_slider);
        _position.FontSize=_duration.FontSize=10.5;
        _position.VerticalAlignment=_duration.VerticalAlignment=VerticalAlignment.Center;
        _position.SetResourceReference(TextBlock.ForegroundProperty,"MenuInk");_duration.SetResourceReference(TextBlock.ForegroundProperty,"MenuInk");
        Children.Add(_position);SetColumn(_duration,2);Children.Add(_duration);
        // Slider handles track clicks before instance handlers run. Observe handled
        // events too so the moved value is committed to audio instead of refreshed away.
        _slider.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler((_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left) return;
            BeginEdit();
            if (e.Handled) Commit();
        }), true);
        _slider.PreviewMouseLeftButtonUp+=(_,_)=>Commit();
        _slider.AddHandler(Thumb.DragCompletedEvent,new DragCompletedEventHandler((_,_)=>Commit()));
        _slider.LostMouseCapture+=(_,_)=>Commit();
        _slider.PreviewKeyDown+=(_,e)=>{if(e.Key is Key.Left or Key.Right or Key.Home or Key.End or Key.PageUp or Key.PageDown)BeginEdit();};
        _slider.KeyUp+=(_,e)=>{if(e.Key is Key.Left or Key.Right or Key.Home or Key.End or Key.PageUp or Key.PageDown)Commit();};
        _slider.LostKeyboardFocus+=(_,_)=>{if(!_slider.IsMouseCaptureWithin)Commit();};
        _slider.ValueChanged+=(_,_)=>{if(_editing)_position.Text=Format(_slider.Value);};
        _timer.Tick+=(_,_)=>Refresh();
        Loaded+=(_,_)=>{Refresh();_timer.Start();};Unloaded+=(_,_)=>Stop();
    }
    private void BeginEdit()
    {
        if(!_slider.IsEnabled || _editing)return;
        _editing=true;_editingSong=_source.Read().Song?.Mid;
    }
    private void Commit()
    {
        if(!_editing)return;
        _editing=false;
        var state=_source.Read();
        if(state.Song?.Mid==_editingSong && state.Duration>0)_source.Seek(Math.Clamp(_slider.Value,0,state.Duration));
        Refresh();
    }
    internal void Refresh()
    {
        if(_editing)return;
        var state=_source.Read();
        double duration=double.IsFinite(state.Duration)?Math.Max(0,state.Duration):0;
        _slider.IsEnabled=state.Song!=null && duration>0;
        _slider.Maximum=Math.Max(1,duration);
        _slider.Value=double.IsFinite(state.Position)?Math.Clamp(state.Position,0,_slider.Maximum):0;
        _position.Text=Format(_slider.Value);_duration.Text=duration>0?Format(duration):"—:—";
    }
    internal void Stop(){_timer.Stop();_editing=false;}
    private static string Format(double seconds)=>TimeSpan.FromSeconds(Math.Max(0,seconds)).ToString(seconds>=3600?@"h\:mm\:ss":@"mm\:ss");
}
