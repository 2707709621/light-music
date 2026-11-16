using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace FeatherPlayer;

// Keep one background and motion clock for the owner's lifetime. Palette edits
// retarget color animations from their current values without resetting motion.
internal sealed class MenuLighting : IDisposable
{
    private static double _energy;
    private static event Action? EnergyChanged;
    internal static void SetEnergy(double energy)
    {
        if (Math.Abs(_energy - energy) < .02) return;
        _energy = energy;
        EnergyChanged?.Invoke();
    }
    private void UpdateEnergy()
    {
        if (_released || !_owner.IsLoaded || !_owner.IsVisible) return;
        var pulse = new DoubleAnimation(.5 + .5 * _energy, TimeSpan.FromMilliseconds(180))
        { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
        _glow.BeginAnimation(Brush.OpacityProperty, pulse);
    }
    private readonly FrameworkElement _owner;
    private readonly SolidColorBrush _surface = new(Colors.Transparent);
    private readonly RadialGradientBrush _glow;
    private readonly AnimationClock _motionClock;
    private bool _enabled, _released, _dark;

    internal DrawingBrush Background { get; }

    internal MenuLighting(FrameworkElement owner)
    {
        _owner = owner;
        EnergyChanged += UpdateEnergy;
        _glow = new RadialGradientBrush
        {
            Center = new Point(.86,.47), GradientOrigin = new Point(.86,.47),
            RadiusX = 1.1, RadiusY = .9, Opacity = 0,
            ColorInterpolationMode = ColorInterpolationMode.ScRgbLinearInterpolation
        };
        foreach (double offset in new[] { 0, .45, .75, 1 })
            _glow.GradientStops.Add(new GradientStop(Colors.Transparent,offset));

        // A closed ellipse has matching position and tangent at the loop seam.
        // Path animation follows arc length, so the light never stops at corners.
        var path = new PathGeometry();
        path.AddGeometry(new EllipseGeometry(new Rect(.14,.15,.72,.64)));
        path.Freeze();
        var motion = new PointAnimationUsingPath
        {
            PathGeometry = path,
            Duration = TimeSpan.FromSeconds(24),
            RepeatBehavior = RepeatBehavior.Forever
        };
        Timeline.SetDesiredFrameRate(motion,24);
        _motionClock = (AnimationClock)motion.CreateClock(true);
        _motionClock.Controller!.Pause();
        // Bind before publishing the resource to prevent WPF from freezing it.
        _glow.ApplyAnimationClock(RadialGradientBrush.CenterProperty,_motionClock);
        _glow.ApplyAnimationClock(RadialGradientBrush.GradientOriginProperty,_motionClock);

        var bounds = new RectangleGeometry(new Rect(0,0,1,1));
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(_surface,null,bounds));
        drawing.Children.Add(new GeometryDrawing(_glow,null,bounds));
        Background = new DrawingBrush(drawing);

        owner.Loaded += (_, _) =>
        {
            if (_released) return;
            var fade = new DoubleAnimation(0,1,TimeSpan.FromMilliseconds(700))
            {
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            Timeline.SetDesiredFrameRate(fade,24);
            _glow.BeginAnimation(Brush.OpacityProperty,fade,HandoffBehavior.SnapshotAndReplace);
            UpdateMotion();
        };
        owner.Unloaded += (_, _) =>
        {
            if (!_released) _motionClock.Controller?.Pause();
        };
        owner.IsVisibleChanged += (_, _) => UpdateMotion();
        if (owner is ContextMenu menu) menu.Closed += (_, _) => Release();
        if (owner is Window window) window.Closed += (_, _) => Release();
    }

    internal void Update(Color surface, Color glow, bool dark)
    {
        if (_released) return;
        bool visible = _owner.IsLoaded && _owner.IsVisible;
        // Theme text changes immediately, so match its surface contrast at once.
        // Cover tint and opacity changes within the same theme blend gradually.
        AnimateColor(_surface,SolidColorBrush.ColorProperty,_surface.Color,surface,visible && dark == _dark);
        _dark = dark;
        double[] weights = { 1, .52, .16, 0 };
        for (int i = 0; i < _glow.GradientStops.Count; i++)
        {
            var stop = _glow.GradientStops[i];
            var color = glow;
            color.A = (byte)Math.Round(glow.A * weights[i]);
            AnimateColor(stop,GradientStop.ColorProperty,stop.Color,color,visible);
        }
        _enabled = glow.A > 0;
        UpdateMotion();
    }

    private static void AnimateColor(Animatable target, DependencyProperty property,
        Color current, Color next, bool visible)
    {
        var transition = new ColorAnimation(current,next,
            visible ? TimeSpan.FromMilliseconds(900) : TimeSpan.Zero)
        {
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            FillBehavior = FillBehavior.HoldEnd
        };
        Timeline.SetDesiredFrameRate(transition,24);
        // Holding the final color also keeps mutable brush components unfrozen
        // until the next palette update. Replace clocks instead of accumulating.
        target.BeginAnimation(property,transition,HandoffBehavior.SnapshotAndReplace);
    }

    private void UpdateMotion()
    {
        if (_released) return;
        if (_enabled && _owner.IsLoaded && _owner.IsVisible) _motionClock.Controller?.Resume();
        else _motionClock.Controller?.Pause();
    }

    private void Release()
    {
        if (_released) return;
        _released = true;
        EnergyChanged -= UpdateEnergy;
        _motionClock.Controller?.Remove();
        if (!_glow.IsFrozen)
        {
            _glow.ApplyAnimationClock(RadialGradientBrush.CenterProperty,null);
            _glow.ApplyAnimationClock(RadialGradientBrush.GradientOriginProperty,null);
            _glow.BeginAnimation(Brush.OpacityProperty,null);
        }
        if (!_surface.IsFrozen) _surface.BeginAnimation(SolidColorBrush.ColorProperty,null);
        foreach (var stop in _glow.GradientStops)
            if (!stop.IsFrozen) stop.BeginAnimation(GradientStop.ColorProperty,null);
    }

    public void Dispose() => Release();
}
