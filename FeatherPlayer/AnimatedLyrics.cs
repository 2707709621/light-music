using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace FeatherPlayer;

// Each physical row owns its lyric until a new sentence needs that SAME row.
// Playback time drives both phases, so pause and seeking cannot leave a wall-clock fade running.
internal sealed class LyricRows
{
    internal sealed class Row
    {
        internal string Text = "", Outgoing = "";
        internal string[] Glyphs = Array.Empty<string>(), OutgoingGlyphs = Array.Empty<string>();
    }
    internal Row[] Rows { get; } = { new(), new() };
    internal int ActiveRow { get; private set; } = -1;
    private double _start, _end;
    private bool _instant;
    internal void Clear()
    {
        ActiveRow = -1;
        foreach (var row in Rows) { row.Text = row.Outgoing = ""; row.Glyphs = row.OutgoingGlyphs = Array.Empty<string>(); }
    }
    internal void SetLine(string text, double start, double end, bool reset = false)
    {
        if (reset) Clear();
        if (ActiveRow >= 0 && Rows[ActiveRow].Text == text && _start == start) return;
        if (ActiveRow >= 0) { Rows[ActiveRow].Outgoing = ""; Rows[ActiveRow].OutgoingGlyphs = Array.Empty<string>(); }
        ActiveRow = (ActiveRow + 1) % 2;
        var row = Rows[ActiveRow];
        row.Outgoing = row.Text; row.OutgoingGlyphs = row.Glyphs;
        row.Text = text; row.Glyphs = Split(text);
        _start = start; _end = end;
        _instant = string.IsNullOrWhiteSpace(text) || text is "暂无歌词" or "♪" || end <= start;
        if (_instant) { row.Outgoing = ""; row.OutgoingGlyphs = Array.Empty<string>(); }
    }
    internal (double Incoming, double Outgoing) Progress(double position)
    {
        if (ActiveRow < 0 || _instant) return (1, 1);
        double duration = Math.Max(.15, _end - _start - .1);
        double erase = Rows[ActiveRow].Outgoing.Length == 0 ? 0 : Math.Min(.45, duration * .25);
        double elapsed = Math.Max(0, position - _start);
        return (EaseOut((elapsed - erase) / Math.Max(.1, duration - erase)), erase == 0 ? 1 : EaseOut(elapsed / erase));
    }
    internal static double EaseOut(double progress)
    {
        double t = Math.Clamp(progress, 0, 1);
        return 1 - (1 - t) * (1 - t) * (1 - t);
    }
    private static string[] Split(string text)
    {
        var glyphs = new List<string>();
        var iterator = StringInfo.GetTextElementEnumerator(text);
        while (iterator.MoveNext()) glyphs.Add(iterator.GetTextElement());
        return glyphs.ToArray();
    }
}

internal sealed class AnimatedLyrics : FrameworkElement
{
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(40) };
    private PlaybackState _state = new(null, false, "", 0, 0);
    private readonly LyricRows _rows = new();
    private readonly LuminousLyrics _luminous = new();
    private string _mid = "";
    private long _anchor;
    private bool _suspended;
    private double _rowHeight = 16, _fontSize = 12;
    private sealed record LineDrawing(string Text,ImageSource Image,double Width,double[] Edges);
    private readonly Dictionary<string,LineDrawing> _lines=new();
    private static readonly Typeface LyricTypeface=new(new FontFamily("Microsoft YaHei UI"),FontStyles.Normal,FontWeights.Medium,FontStretches.Normal);
    private (int Incoming,int Outgoing) _lastFrame=(-1,-1);
    internal AnimatedLyrics()
    {
        Height = 32;
        _timer.Tick += (_, _) => { InvalidateFrame(); if (LyricStyleSettings.Luminous ? !_luminous.Moving(Position()) : _rows.Progress(Position()).Incoming >= 1) _timer.Stop(); };
        SystemTheme.Changed += Refresh;
        LyricStyleSettings.Changed += OnStyleChanged;
        LyricStyleSettings.MotionChanged += OnMotionChanged;
        Unloaded += (_, _) => _timer.Stop();
    }
    internal void FitHeight(double available)
    {
        double height = Math.Clamp(available, 1, 32), font = Math.Min(12, height / 2 * .75);
        if (Height == height && _fontSize == font) return;
        Height = height; _rowHeight = height / 2; _fontSize = font; Refresh();
    }
    internal void Release() { _timer.Stop(); SystemTheme.Changed -= Refresh; LyricStyleSettings.Changed -= OnStyleChanged; LyricStyleSettings.MotionChanged -= OnMotionChanged; }
    private void OnMotionChanged() { InvalidateVisual(); SyncTimer(); }
    private void OnStyleChanged() { _luminous.Update(_state); Refresh(); SyncTimer(); }
    private void Refresh() { _luminous.ClearDrawings(); _lines.Clear();_lastFrame=(-1,-1); InvalidateVisual(); }
    private void InvalidateFrame()
    {
        if (LyricStyleSettings.Luminous) { InvalidateVisual(); return; }
        if(_rows.ActiveRow<0)return;
        var row=_rows.Rows[_rows.ActiveRow];var progress=_rows.Progress(Position());
        var frame=((int)Math.Round(progress.Incoming*row.Glyphs.Length*255),(int)Math.Round(progress.Outgoing*row.OutgoingGlyphs.Length*255));
        if(frame==_lastFrame)return;_lastFrame=frame;InvalidateVisual();
    }
    internal void Suspend(bool value) { _suspended = value; SyncTimer(); }
    internal void Update(PlaybackState state)
    {
        string mid = state.Song?.Mid ?? "";
        bool reset = mid != _mid || state.LineStart < _state.LineStart ||
            (_rows.ActiveRow >= 0 && state.LineStart > _state.LineEnd + .75);
        _mid = mid;
        _rows.SetLine(state.Lyric, state.LineStart, state.LineEnd, reset);
        // At most the two retained sentences and their replacement drawings are useful.
        foreach(var key in new List<string>(_lines.Keys))
            if(!_rows.Rows.Any(row=>row.Text==key || row.Outgoing==key))_lines.Remove(key);
        _luminous.Update(state);
        _state = state; _anchor = Environment.TickCount64;
        _lastFrame=(-1,-1);InvalidateFrame(); SyncTimer();
    }
    private double Position() => _state.Position + (_state.IsPlaying ? (Environment.TickCount64 - _anchor) / 1000d : 0);
    private void SyncTimer()
    {
        if (!_suspended && _state.IsPlaying && (LyricStyleSettings.Luminous ? _luminous.Moving(Position()) : _rows.Progress(Position()).Incoming < 1)) _timer.Start();
        else _timer.Stop();
    }
    internal static double OpacityFor(int index, int count, double progress, bool exiting) =>
        Math.Clamp(exiting ? index + 1 - progress * count : progress * count - index, 0, 1);
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi) { base.OnDpiChanged(oldDpi, newDpi); Refresh(); }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, Math.Max(0, ActualWidth), Height)));
        if (LyricStyleSettings.Luminous)
        {
            _luminous.Draw(dc, Position(), ActualWidth, Height, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.Pop();
            return;
        }
        var progress = _rows.Progress(Position());
        for (int i = 0; i < 2; i++)
        {
            var row = _rows.Rows[i];
            if (i == _rows.ActiveRow)
            {
                Draw(dc, row.Outgoing, row.OutgoingGlyphs, i, progress.Outgoing, true);
                Draw(dc, row.Text, row.Glyphs, i, progress.Incoming, false);
            }
            else Draw(dc, row.Text, row.Glyphs, i, 1, false);
        }
        dc.Pop();
    }
    private void Draw(DrawingContext dc,string text,string[] elements,int row,double progress,bool exiting)
    {
        if(elements.Length==0 || (!exiting && progress<=0) || (exiting && progress>=1))return;
        if(!_lines.TryGetValue(text,out var line))
        {
            var drawing=new DrawingGroup();var edges=new double[elements.Length+1];
            using(var context=drawing.Open())
            {
                for(int i=0;i<elements.Length;i++)
                {
                    var glyph=new FormattedText(elements[i],CultureInfo.CurrentUICulture,FlowDirection.LeftToRight,LyricTypeface,_fontSize,SystemTheme.Ink,VisualTreeHelper.GetDpi(this).PixelsPerDip);
                    context.DrawText(glyph,new Point(edges[i],Math.Max(0,(_rowHeight-glyph.Height)/2)));
                    edges[i+1]=edges[i]+glyph.WidthIncludingTrailingWhitespace;
                }
            }
            drawing.Freeze();
            double scale=VisualTreeHelper.GetDpi(this).PixelsPerDip;
            int pixels=Math.Max(1,(int)Math.Ceiling(Math.Min(2048,edges[^1]+2)*scale));
            var visual=new DrawingVisual();using(var context=visual.RenderOpen())context.DrawDrawing(drawing);
            var bitmap=new RenderTargetBitmap(pixels,Math.Max(1,(int)Math.Ceiling(_rowHeight*scale)),96*scale,96*scale,PixelFormats.Pbgra32);
            bitmap.Render(visual);bitmap.Freeze();
            _lines[text]=line=new(text,bitmap,pixels/scale,edges);
        }
        double amount=Math.Clamp(progress,0,1)*elements.Length;int index=Math.Min(elements.Length,(int)amount);double fraction=amount-index;
        dc.PushTransform(new TranslateTransform(0,row*_rowHeight));
        void Part(double left,double right,double opacity)
        {
            right=Math.Min(right,ActualWidth);if(right<=left || opacity<=0)return;
            dc.PushClip(new RectangleGeometry(new Rect(left,0,right-left,_rowHeight)));
            if(opacity<1)dc.PushOpacity(opacity);
            dc.DrawImage(line.Image,new Rect(0,0,line.Width,_rowHeight));
            if(opacity<1)dc.Pop();dc.Pop();
        }
        if(!exiting)Part(0,line.Edges[index],1);
        else if(index<elements.Length)Part(line.Edges[index+1],line.Edges[^1],1);
        if(index<elements.Length)Part(line.Edges[index],line.Edges[index+1],exiting?1-fraction:fraction);
        dc.Pop();
    }
}
