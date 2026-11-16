using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FeatherPlayer;

internal sealed class MenuNowPlaying : Grid
{
    private readonly Image _image = new() { Width = 80, Height = 80, Stretch = Stretch.UniformToFill };
    private readonly ContentControl _fallback = new() { HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly Func<ImageSource?> _readCover;
    private readonly Func<Song?> _readSong;
    private readonly TextBlock _title;
    private readonly TextBlock _artist;

    internal MenuNowPlaying(Func<Song?> readSong, Func<ImageSource?> readCover)
    {
        _readCover = readCover;
        _readSong = readSong;
        Margin = new Thickness(8, 8, 8, 10);
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
        ColumnDefinitions.Add(new ColumnDefinition());
        var icon = LineIcons.Create("headphones", 30, 1.6);
        if (icon is Viewbox box && box.Child is System.Windows.Shapes.Path path)
            path.SetResourceReference(System.Windows.Shapes.Path.StrokeProperty, "MenuInk");
        _fallback.Content = icon;
        _image.Clip = new RectangleGeometry(new Rect(0, 0, 80, 80), 7, 7);
        var cover = new Grid { Width = 80, Height = 80, HorizontalAlignment = HorizontalAlignment.Left };
        cover.Children.Add(_fallback); cover.Children.Add(_image);
        Children.Add(cover);
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = _title = new TextBlock
        {
            Text = readSong()?.Title ?? "light music",
            FontSize = 14, FontWeight = FontWeights.Medium,
            TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis,
            LineHeight = 20, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            MaxHeight = 40, ClipToBounds = true
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "MenuInk");
        labels.Children.Add(title);
        _artist = new TextBlock
        {
            FontSize = 11.5, Margin = new Thickness(0, 6, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        _artist.SetResourceReference(TextBlock.ForegroundProperty,"MenuMuted");
        labels.Children.Add(_artist);
        SetColumn(labels, 1); Children.Add(labels);
        Loaded += (_, _) => { Refresh(); SystemTheme.Changed += Refresh; NativePopupMenu.Updated += Refresh; };
        Unloaded += (_, _) => { SystemTheme.Changed -= Refresh; NativePopupMenu.Updated -= Refresh; };
    }

    private void Refresh()
    {
        _image.Source = _readCover();
        var song = _readSong();
        _title.Text = song?.Title ?? "light music";
        _artist.Text = song?.Artist ?? "选择一首歌曲开始播放";
        _fallback.Visibility = _image.Source == null ? Visibility.Visible : Visibility.Hidden;
    }
}
