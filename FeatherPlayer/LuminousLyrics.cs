using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MaterialColorUtilities.ColorAppearance;
using MaterialColorUtilities.Palettes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FeatherPlayer;

internal sealed record LyricSwayOptions(bool Enabled = true, double Amount = 1.8, double Speed = 1)
{
    internal LyricSwayOptions Normalize() => new(Enabled,
        double.IsFinite(Amount) ? Math.Clamp(Amount, 0, 8) : 1.8,
        double.IsFinite(Speed) ? Math.Clamp(Speed, .2, 3) : 1);
}

internal static class LyricStyleSettings
{
    private static bool _luminous;
    internal static LyricSwayOptions WholeSway { get; private set; } = new();
    internal static LyricSwayOptions LetterSway { get; private set; } = new(Amount: 1.2);
    internal static event Action? MotionChanged;
    internal static void SetSway(bool letters, LyricSwayOptions options)
    {
        options = options.Normalize();
        if (letters) { if (LetterSway == options) return; LetterSway = options; }
        else { if (WholeSway == options) return; WholeSway = options; }
        MotionChanged?.Invoke();
    }
    private static bool _randomLayout = true;
    private static double _randomness = .35;
    internal static bool RandomLayout
    {
        get => _randomLayout;
        set { if (_randomLayout == value) return; _randomLayout = value; Changed?.Invoke(); }
    }
    internal static double Randomness
    {
        get => _randomness;
        set
        {
            double next = double.IsFinite(value) ? Math.Clamp(value, 0, 1) : .35;
            if (_randomness == next) return;
            _randomness = next; Changed?.Invoke();
        }
    }
    internal static event Action? Changed;
    internal static bool Luminous
    {
        get => _luminous;
        set { if (_luminous == value) return; _luminous = value; Changed?.Invoke(); }
    }
}

// An independent WPF treatment inspired by Folia's Luminous visual style.
// Cached glyph sprites keep motion and light on the compositor drawing path.
internal sealed class LuminousLyrics
{
    private sealed record Glyph(ImageSource Body, ImageSource Blurred, ImageSource Light, ImageSource SimilarA, ImageSource SimilarB, ImageSource Halo, ImageSource SoftHalo, bool Blank, double Width, double Height);
    private readonly Dictionary<string, Glyph> _glyphs = new();
    private string _text = "", _outgoing = "", _song = "";
    private double _start, _end, _lastPosition, _motionTime;
    private const double Padding = 2;
    private int _seed, _outgoingSeed;
    private static readonly Typeface Face = new(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Medium, FontStretches.Normal);

    internal void ClearDrawings() => _glyphs.Clear();
    internal void Update(PlaybackState state)
    {
        bool reset = _song != (state.Song?.Mid ?? "") || state.LineStart < _start || Math.Abs(state.Position - _lastPosition) > 1.5;
        _lastPosition = state.Position;
        if (reset) _outgoing = "";
        if (_text != state.Lyric || _start != state.LineStart || reset)
        {
            _outgoing = reset ? "" : _text;
            _outgoingSeed = _seed;
            _seed = Random.Shared.Next();
            _text = state.Lyric;
            _start = state.LineStart;
            _end = state.LineEnd;
            _song = state.Song?.Mid ?? "";
        }
    }
    internal bool Moving(double position) => _end > _start && !string.IsNullOrWhiteSpace(_text) && _text is not ("暂无歌词" or "♪") &&
        (LyricStyleSettings.WholeSway is { Enabled: true, Amount: > 0 } || LyricStyleSettings.LetterSway is { Enabled: true, Amount: > 0 } || position < _end + 1.2);

    internal void Draw(DrawingContext dc, double position, double width, double height, double dpi)
    {
        double size = Math.Min(22, height * .72);
        if (size < 1 || width <= 0) return;
        // Both sentences share one playback-clock transform, so pause freezes
        // the entire line and sentence changes do not restart the sway phase.
        _motionTime = position;
        var whole = LyricStyleSettings.WholeSway;
        double strength = whole.Enabled ? whole.Amount : 0;
        double marginX = Math.Min(strength, width * .04);
        double marginY = Math.Min(strength * .67, height * .12);
        double swayX = Math.Sin(position * whole.Speed * Math.PI * 2 / 5.8) * marginX;
        double swayY = Math.Sin(position * whole.Speed * Math.PI * 2 / 4.7 + .65) * marginY;
        dc.PushTransform(new TranslateTransform(marginX + swayX, marginY + swayY));
        width = Math.Max(.01, width - marginX * 2);
        height = Math.Max(.01, height - marginY * 2);
        double elapsed = Math.Max(0, position - _start);
        bool timed = _end > _start && _text is not ("暂无歌词" or "♪" or "");
        double erase = timed && _outgoing.Length > 0 ? Math.Min(.28, (_end - _start) * .15) : 0;
        if (erase > 0 && elapsed < erase)
            DrawLine(dc, _outgoing, 1, 1 - Ease(elapsed / erase), size, height, width, dpi, exiting: true);
        double progress = timed ? Math.Max(0, (elapsed - erase) / Math.Max(.15, _end - _start - erase - .15)) : 1;
        DrawLine(dc, _text, progress, 1, size, height, width, dpi, exiting: false);
        dc.Pop();
    }

    private void DrawLine(DrawingContext dc, string text, double progress, double opacity, double font, double height, double width, double dpi, bool exiting)
    {
        if (text.Length == 0 || opacity <= 0) return;
        var elements = StringInfo.GetTextElementEnumerator(text);
        var line = new List<Glyph>();
        double total = 0;
        while (elements.MoveNext())
        {
            var glyph = Prepare(elements.GetTextElement(), font, height, dpi);
            line.Add(glyph); total += glyph.Width;
        }
        double randomness = LyricStyleSettings.RandomLayout ? LyricStyleSettings.Randomness : 0;
        int seed = exiting ? _outgoingSeed : _seed;
        var layout = new (double Rotation, double Scale, double Cell)[line.Count];
        total = 0;
        for (int i = 0; i < line.Count; i++)
        {
            var glyph = line[i];
            double rotation = Noise(seed, i, 0) * 24 * randomness;
            double baseScale = 1 + Noise(seed, i, 1) * .3 * randomness;
            double radians = Math.Abs(rotation) * Math.PI / 180;
            double boxWidth = (glyph.Width + Padding * 2) * Math.Cos(radians) + glyph.Height * Math.Sin(radians);
            double boxHeight = glyph.Height * Math.Cos(radians) + (glyph.Width + Padding * 2) * Math.Sin(radians);
            baseScale = Math.Min(baseScale, Math.Max(.01, height - 2) / boxHeight);
            double inkWidth = glyph.Width * Math.Cos(radians) + Math.Max(0, glyph.Height - Padding * 2) * Math.Sin(radians);
            double cell = inkWidth * baseScale * 1.08 + 1;
            layout[i] = (rotation, baseScale, cell);
            total += cell;
        }
        // Fit the readable text, allowing neighboring halos to blend. Clamp
        // each animated glyph independently at the component edges.
        double fit = Math.Min(1, Math.Max(.01, width - 2) / Math.Max(1, total));
        double left = Math.Max(1, (width - total * fit) / 2);
        double amount = progress * line.Count;
        double step = Math.Max(.06, (_end - _start) / Math.Max(1, line.Count));
        int current = Math.Clamp((int)Math.Floor(amount), 0, Math.Max(0, line.Count - 1));
        while (current > 0 && line[current].Blank) current--;
        var accents = Enumerable.Range(0, line.Count).Where(index => index != current && !line[index].Blank)
            .OrderBy(index => Noise(seed, index, 6)).Take(2).ToArray();
        double x = left;
        for (int i = 0; i < line.Count; i++)
        {
            var glyph = line[i];
            var placement = layout[i];
            double age = (amount - i) * step;
            bool staticLine = _end <= _start;
            double reveal = staticLine || exiting ? 1 : Smooth(age / .28);
            if (reveal > 0)
            {
                double passed = Math.Max(0, age - step);
                double arrival = Smooth(age / .42);
                double light = exiting || staticLine || i != current ? 0 : Smooth(age / .22) * Math.Exp(-passed / .48);
                double growth = staticLine ? 1 : .02 + .98 * arrival + .4 * arrival * Math.Exp(-passed / .36);
                double scale = placement.Scale * fit * (exiting ? 1 + (1 - opacity) * .12 : growth);
                double rotation = placement.Rotation + (staticLine || exiting ? 0 : (1 - Smooth(age / .5)) * 24 + Noise(seed, i, 4) * randomness * 3 * Math.Clamp(passed / 5, 0, 1));
                var letters = LyricStyleSettings.LetterSway;
                double letterAmount = letters.Enabled ? letters.Amount : 0;
                double phase = Noise(seed, i, 7) * Math.PI;
                double clock = _motionTime * letters.Speed * (1 + Noise(seed, i, 8) * .2);
                rotation += Math.Sin(clock * 1.1 + phase) * letterAmount * 1.8;
                double radians = Math.Abs(rotation) * Math.PI / 180;
                double boxWidth = (glyph.Width + Padding * 2) * Math.Cos(radians) + glyph.Height * Math.Sin(radians);
                double boxHeight = glyph.Height * Math.Cos(radians) + (glyph.Width + Padding * 2) * Math.Sin(radians);
                scale = Math.Min(scale, Math.Min(Math.Max(.01, height - 1) / boxHeight, Math.Max(.01, width - 1) / boxWidth));
                double halfWidth = ((glyph.Width + Padding * 2) * Math.Cos(radians) + glyph.Height * Math.Sin(radians)) * scale / 2;
                double halfHeight = (glyph.Height * Math.Cos(radians) + (glyph.Width + Padding * 2) * Math.Sin(radians)) * scale / 2;
                double centerX = x + placement.Cell * fit / 2 + Noise(seed, i, 2) * randomness * fit + (1 - reveal) * Noise(seed, i, 5) * 6;
                double slack = Math.Max(0, height / 2 - halfHeight - 1);
                double centerY = height / 2 + Noise(seed, i, 3) * randomness * slack - light + (1 - reveal) * 3;
                centerX += Math.Sin(clock * 1.55 + phase) * letterAmount;
                centerY += Math.Sin(clock * 1.9 + phase * .7) * Math.Min(letterAmount, slack);
                centerX = Math.Clamp(centerX, halfWidth, Math.Max(halfWidth, width - halfWidth));
                centerY = Math.Clamp(centerY, halfHeight, Math.Max(halfHeight, height - halfHeight));
                dc.PushTransform(new TranslateTransform(centerX, centerY));
                dc.PushTransform(new RotateTransform(rotation));
                dc.PushTransform(new ScaleTransform(scale, scale));
                dc.PushOpacity(opacity * reveal);
                var rect = new Rect(-glyph.Width / 2 - Padding, -glyph.Height / 2, glyph.Width + Padding * 2, glyph.Height);
                var glowRect = rect;
                glowRect.Inflate(9, 9);
                double colorWeight = !staticLine && !exiting && i <= current
                    ? i == current ? 1 : 1 - Smooth(passed / .95)
                    : 0;
                double ambient = SystemTheme.Dark ? .16 : .09;
                dc.PushOpacity(ambient * (1 - colorWeight)); dc.DrawImage(glyph.SoftHalo, glowRect); dc.Pop();
                if (colorWeight > .001)
                {
                    dc.PushOpacity(colorWeight * (.3 + light * .7)); dc.DrawImage(glyph.Halo, glowRect); dc.Pop();
                }
                double clarity = staticLine || exiting ? 1 : Ease(age / .2);
                if (clarity < 1) { dc.PushOpacity(1 - clarity); dc.DrawImage(glyph.Blurred, rect); dc.Pop(); }
                ImageSource settled = accents.Length > 0 && i == accents[0] ? glyph.SimilarA
                    : accents.Length > 1 && i == accents[1] ? glyph.SimilarB : glyph.Body;
                dc.PushOpacity(clarity);
                dc.DrawImage(settled, rect);
                if (colorWeight > .001)
                {
                    dc.PushOpacity(colorWeight); dc.DrawImage(glyph.Light, rect); dc.Pop();
                }
                dc.Pop();
                dc.Pop(); dc.Pop(); dc.Pop(); dc.Pop();
            }
            x += placement.Cell * fit;
        }
    }

    // Stable for the lifetime of a sentence, including pause and settings changes.
    private static double Noise(int seed, int index, int channel)
    {
        uint value = unchecked((uint)seed ^ (uint)(index + 1) * 2654435761u ^ (uint)(channel + 1) * 2246822519u);
        value ^= value >> 16; value *= 2246822519u; value ^= value >> 13;
        return (value & 0xffff) / 32767.5 - 1;
    }

    private Glyph Prepare(string text, double font, double height, double dpi)
    {
        if (_glyphs.TryGetValue(text, out var cached)) return cached;
        var body = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, font, Brushes.White, dpi);
        var color = SystemTheme.Tone(SystemTheme.Dark ? 78u : 38u, 48);
        var ink = new SolidColorBrush(color); ink.Freeze();
        var active = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, font, ink, dpi);
        double width = body.WidthIncludingTrailingWhitespace;
        Rect inkBounds = body.BuildGeometry(new Point()).Bounds;
        double inkTop = inkBounds.IsEmpty ? 0 : inkBounds.Top;
        double inkHeight = inkBounds.IsEmpty ? body.Height : inkBounds.Height;
        double spriteHeight = Math.Max(1, inkHeight) + Padding * 2;
        ImageSource Sprite(FormattedText textRun)
        {
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                var origin = new Point(Padding, Padding - inkTop);
                context.DrawText(textRun, origin);
            }
            var image = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling((width + Padding * 2) * dpi)), Math.Max(1, (int)Math.Ceiling(spriteHeight * dpi)), 96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
            image.Render(visual); image.Freeze(); return image;
        }
        if (_glyphs.Count >= 256) _glyphs.Clear();
        var bodyImage = Sprite(body);
        var activeImage = Sprite(active);
        ImageSource Similar(double hueOffset, uint tone)
        {
            var source = Hct.FromInt(0xff000000u | (uint)SystemTheme.Accent.R << 16 | (uint)SystemTheme.Accent.G << 8 | SystemTheme.Accent.B);
            uint argb = TonalPalette.FromHueAndChroma((source.Hue + hueOffset + 360) % 360, Math.Min(source.Chroma, 40))[tone];
            var brush = new SolidColorBrush(Color.FromRgb((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb)); brush.Freeze();
            return Sprite(new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, font, brush, dpi));
        }
        ImageSource Blur(ImageSource source, double radius)
        {
            var element = new System.Windows.Controls.Image
            {
                Source = source, Width = width + Padding * 2, Height = spriteHeight,
                Effect = new System.Windows.Media.Effects.BlurEffect { Radius = radius, KernelType = System.Windows.Media.Effects.KernelType.Gaussian }
            };
            element.Measure(new Size(width + Padding * 2, spriteHeight));
            element.Arrange(new Rect(0, 0, width + Padding * 2, spriteHeight));
            var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling((width + Padding * 2) * dpi)), Math.Max(1, (int)Math.Ceiling(spriteHeight * dpi)), 96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
            bitmap.Render(element); bitmap.Freeze(); return bitmap;
        }
        // Cache bloom in a padded surface so wide blur is not cut off by the
        // glyph bitmap. The taskbar component supplies the final outer clip.
        ImageSource Glow(ImageSource source, double radius)
        {
            var image = new System.Windows.Controls.Image
            {
                Source = source, Width = width + Padding * 2, Height = spriteHeight,
                Effect = new System.Windows.Media.Effects.BlurEffect { Radius = radius, KernelType = System.Windows.Media.Effects.KernelType.Gaussian }
            };
            var padded = new System.Windows.Controls.Border { Padding = new Thickness(9), Child = image };
            var size = new Size(width + Padding * 2 + 18, spriteHeight + 18);
            padded.Measure(size); padded.Arrange(new Rect(size));
            var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(size.Width * dpi)), Math.Max(1, (int)Math.Ceiling(size.Height * dpi)), 96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
            bitmap.Render(padded); bitmap.Freeze(); return bitmap;
        }
        var inner = Glow(activeImage, 1.4);
        var middle = Glow(activeImage, 4);
        var outer = Glow(activeImage, 8.5);
        var haloVisual = new DrawingVisual();
        var bounds = new Rect(0, 0, width + Padding * 2 + 18, spriteHeight + 18);
        using (var context = haloVisual.RenderOpen())
        {
            double strength = SystemTheme.Dark ? 1 : .65;
            context.PushOpacity(strength * .5); context.DrawImage(outer, bounds); context.Pop();
            context.PushOpacity(strength * .45); context.DrawImage(middle, bounds); context.Pop();
            context.PushOpacity(strength * .32); context.DrawImage(inner, bounds); context.Pop();
        }
        var halo = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(bounds.Width * dpi)), Math.Max(1, (int)Math.Ceiling(bounds.Height * dpi)), 96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
        halo.Render(haloVisual); halo.Freeze();
        var result = new Glyph(bodyImage, Blur(bodyImage, 2), activeImage, Similar(12, SystemTheme.Dark ? 82u : 46u), Similar(-12, SystemTheme.Dark ? 74u : 40u), halo, Glow(bodyImage, 1.5), string.IsNullOrWhiteSpace(text), width, spriteHeight);
        _glyphs[text] = result;
        return result;
    }
    private static double Smooth(double t) { t = Math.Clamp(t, 0, 1); return t * t * t * (t * (t * 6 - 15) + 10); }
    private static double Ease(double t) { t = Math.Clamp(t, 0, 1); return 1 - Math.Pow(1 - t, 3); }
}
