using System;
using System.Windows;
using System.Windows.Media;

namespace FeatherPlayer;

// The logarithmic bands, dB scale and separate rise/fall smoothing are adapted
// from the designs in wpfsvl and FreqFreak. Rendering uses one reusable geometry.
internal sealed class SpectrumSurface : FrameworkElement, IDisposable
{
    private readonly GeometryGroup _bars = new() { FillRule = FillRule.Nonzero };
    private readonly RectangleGeometry[] _rectangles = new RectangleGeometry[32];
    private readonly float[] _levels = new float[32];
    private readonly SolidColorBrush _aura = new();
    private readonly Pen _halo;
    private readonly MenuLighting _lighting;
    private Color _glow;
    private bool _preview;
    private double _energy;

    internal SpectrumSurface()
    {
        _halo = new Pen(_aura,6) { LineJoin = PenLineJoin.Round };
        for (int i = 0; i < _rectangles.Length; i++)
        {
            _rectangles[i] = new RectangleGeometry { RadiusX = 2, RadiusY = 2 };
            _bars.Children.Add(_rectangles[i]);
        }
        _lighting = new MenuLighting(this);
        SystemTheme.Changed += ApplyAppearance;
        MenuAppearanceSettings.Changed += ApplyAppearance;
        ApplyAppearance();
    }

    private void ApplyAppearance()
    {
        var colors = MenuAppearanceSettings.Colors(MenuAppearanceSettings.Current,SystemTheme.MenuDark);
        _glow = colors.Glow;
        _lighting.Update(Colors.Transparent,_glow,SystemTheme.MenuDark);
        InvalidateVisual();
    }

    internal bool Frame(float[] values, double seconds)
    {
        double total = 0;
        bool active = false;
        seconds = Math.Clamp(seconds,.001,.1);
        for (int i = 0; i < _levels.Length; i++)
        {
            float next = float.IsFinite(values[i]) ? Math.Clamp(values[i],0,1) : 0;
            double speed = next > _levels[i] ? .055 : .22;
            _levels[i] += (float)((next - _levels[i]) * (1 - Math.Exp(-seconds / speed)));
            total += _levels[i]; active |= _levels[i] > .002;
        }
        _energy = total / _levels.Length;
        InvalidateVisual();
        return active;
    }

    internal void Preview(bool preview)
    {
        _preview = preview;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        if (_preview)
        {
            var tint = SystemTheme.Tone(SystemTheme.MenuDark ? 12u : 96u,6);
            tint.A = 70;
            dc.DrawRoundedRectangle(new SolidColorBrush(tint),null,new Rect(RenderSize),6,6);
        }
        double step = Math.Max(1,(ActualWidth - 8) / _levels.Length);
        double width = Math.Max(1,step * .64), height = Math.Max(1,ActualHeight - 8);
        for (int i = 0; i < _rectangles.Length; i++)
        {
            double level = _preview && _levels[i] < .08 ? .15 : _levels[i];
            double barHeight = Math.Max(2,level * height);
            _rectangles[i].Rect = new Rect(4 + i * step,ActualHeight - 4 - barHeight,width,barHeight);
        }
        var aura = _glow;
        aura.A = (byte)Math.Round(_glow.A * (.12 + _energy * .3));
        _aura.Color = aura;
        dc.DrawGeometry(null,_halo,_bars);
        dc.PushOpacity(.68 + _energy * .3);
        dc.DrawGeometry(SystemTheme.MenuInk,null,_bars);
        dc.Pop();
        dc.PushOpacity(.35 + _energy * .65);
        dc.DrawGeometry(_lighting.Background,null,_bars);
        dc.Pop();
    }

    public void Dispose()
    {
        SystemTheme.Changed -= ApplyAppearance;
        MenuAppearanceSettings.Changed -= ApplyAppearance;
        _lighting.Dispose();
    }
}
