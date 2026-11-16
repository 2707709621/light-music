using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
namespace FeatherPlayer;
internal static class LineIcons
{
    internal const string HeadphonesGeometry = "M3,14 L3,11 A9,9 0 0 1 21,11 L21,18.5 Q21,21 18.5,21 L16,21 L16,13 L21,13 M3,13 L8,13 L8,21 L5.5,21 Q3,21 3,18.5 Z";
    internal static System.Drawing.Icon TrayIcon()
    {
        var taskbar = NativeTaskbar.FindWindow("Shell_TrayWnd", null);
        uint dpi = taskbar == System.IntPtr.Zero ? 96u : NativeTaskbar.GetDpiForWindow(taskbar);
        int size = System.Math.Clamp(GetSystemMetricsForDpi(49, dpi == 0 ? 96u : dpi), 16, 128);
        // Fit the visible outline to the tray cell, including the rounded stroke.
        // A sparse headphone outline needs almost the full cell for optical balance.
        double stroke = size / 16d * 1.9;
        double edgePadding = size / 16d * 0.25;
        double extent = size - edgePadding * 2 - stroke;
        var geometry = Geometry.Parse(HeadphonesGeometry).Clone();
        Rect bounds = geometry.Bounds;
        double scale = extent / System.Math.Max(bounds.Width, bounds.Height);
        geometry.Transform = new MatrixTransform(scale, 0, 0, scale,
            (size - bounds.Width * scale) / 2 - bounds.X * scale,
            (size - bounds.Height * scale) / 2 - bounds.Y * scale);
        var pen = new Pen(SystemTheme.Dark ? Brushes.White : new SolidColorBrush(Color.FromRgb(48, 48, 48)), stroke)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen()) context.DrawGeometry(null, pen, geometry);
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var png=new System.Windows.Media.Imaging.PngBitmapEncoder();png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream=new System.IO.MemoryStream();png.Save(stream);stream.Position=0;using var drawing=new System.Drawing.Bitmap(stream);
        var handle=drawing.GetHicon();try{return (System.Drawing.Icon)System.Drawing.Icon.FromHandle(handle).Clone();}finally{DestroyIcon(handle);}
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")]private static extern bool DestroyIcon(System.IntPtr icon);
    [System.Runtime.InteropServices.DllImport("user32.dll")]private static extern int GetSystemMetricsForDpi(int index, uint dpi);
    internal static FrameworkElement Create(string kind, double size=18, double weight=2)
    {
        string data=kind switch {
            "star"=>"M12,3 L14.8,8.7 L21,9.6 L16.5,14 L17.6,20.2 L12,17.3 L6.4,20.2 L7.5,14 L3,9.6 L9.2,8.7 Z",
            "addplaylist"=>"M3,6 L17,6 M3,11 L13,11 M3,16 L9,16 M17,12 L17,21 M12.5,16.5 L21.5,16.5",
            "playlist"=>"M3,5 L15,5 M3,10 L15,10 M3,15 L9,15 M17,19 L17,4 L22,3 M17,19 C17,23 11,23 11,19 C11,15 17,15 17,19",
            "calendar"=>"M5,5 L19,5 Q21,5 21,7 L21,20 L3,20 L3,7 Q3,5 5,5 Z M7,2 L7,8 M17,2 L17,8 M3,10 L21,10 M7,14 L7,15 M12,14 L12,15 M17,14 L17,15",
            "sparkles"=>"M10,3 L12,9 L18,11 L12,13 L10,19 L8,13 L2,11 L8,9 Z M19,2 L20,5 L23,6 L20,7 L19,10 L18,7 L15,6 L18,5 Z M19,16 L20,19 L23,20 L20,21 L19,24 L18,21 L15,20 L18,19 Z",
            "repeat"=>"M4,10 L4,9 Q4,6 7,6 L20,6 M17,3 L20,6 L17,9 M20,14 L20,15 Q20,18 17,18 L4,18 M7,15 L4,18 L7,21",
            "quality"=>"M4,10 L4,14 M8,6 L8,18 M12,3 L12,21 M16,6 L16,18 M20,10 L20,14",
            "settings"=>"M19.206,9.015 Q19.206,9.015 19.578,9.179 L20.900,9.763 Q21.271,9.928 21.271,10.674 L21.271,13.326 Q21.271,14.072 20.900,14.237 L19.578,14.821 Q19.206,14.985 19.206,14.985 L19.206,14.985 Q19.206,14.985 19.353,15.364 L19.874,16.711 Q20.021,17.090 19.494,17.618 L17.618,19.494 Q17.090,20.021 16.711,19.874 L15.364,19.353 Q14.985,19.206 14.985,19.206 L14.985,19.206 Q14.985,19.206 14.821,19.578 L14.237,20.900 Q14.072,21.271 13.326,21.271 L10.674,21.271 Q9.928,21.271 9.763,20.900 L9.179,19.578 Q9.015,19.206 9.015,19.206 L9.015,19.206 Q9.015,19.206 8.636,19.353 L7.289,19.874 Q6.910,20.021 6.382,19.494 L4.506,17.618 Q3.979,17.090 4.126,16.711 L4.647,15.364 Q4.794,14.985 4.794,14.985 L4.794,14.985 Q4.794,14.985 4.422,14.821 L3.100,14.237 Q2.729,14.072 2.729,13.326 L2.729,10.674 Q2.729,9.928 3.100,9.763 L4.422,9.179 Q4.794,9.015 4.794,9.015 L4.794,9.015 Q4.794,9.015 4.647,8.636 L4.126,7.289 Q3.979,6.910 4.506,6.382 L6.382,4.506 Q6.910,3.979 7.289,4.126 L8.636,4.647 Q9.015,4.794 9.015,4.794 L9.015,4.794 Q9.015,4.794 9.179,4.422 L9.763,3.100 Q9.928,2.729 10.674,2.729 L13.326,2.729 Q14.072,2.729 14.237,3.100 L14.821,4.422 Q14.985,4.794 14.985,4.794 L14.985,4.794 Q14.985,4.794 15.364,4.647 L16.711,4.126 Q17.090,3.979 17.618,4.506 L19.494,6.382 Q20.021,6.910 19.874,7.289 L19.353,8.636 Q19.206,9.015 19.206,9.015 Z M15.2,12 A3.2,3.2 0 1 1 8.8,12 A3.2,3.2 0 1 1 15.2,12 Z",
            "remove"=>"M4,6 L20,6 M9,6 L9,3 L15,3 L15,6 M6,6 L7,21 L17,21 L18,6 M10,10 L10,17 M14,10 L14,17",
            "headphones"=>HeadphonesGeometry,
            "play"=>"M8,5 Q7,4 7,6 L7,18 Q7,20 9,19 L19,13 Q21,12 19,11 Z",
            "pause"=>"M5,4 L5,20 M19,4 L19,20",
            "previous"=>"M5,5 L5,19 M18,5 Q19,4 19,6 L19,18 Q19,20 17,19 L8,13 Q6,12 8,11 Z",
            "next"=>"M19,5 L19,19 M6,5 Q5,4 5,6 L5,18 Q5,20 7,19 L16,13 Q18,12 16,11 Z",
            "heart"=>"M12,20 C10,18 3,14 3,8 C3,2 10,2 12,7 C14,2 21,2 21,8 C21,14 14,18 12,20 Z",
            "folder"=>"M3,8 L3,19 Q3,21 5,21 L19,21 Q21,21 21,19 L21,8 Q21,6 19,6 L11,6 L9,3 L5,3 Q3,3 3,5 Z",
            "search"=>"M16,10 A6,6 0 1 1 4,10 A6,6 0 1 1 16,10 M15,15 L21,21",
            "volume"=>"M3,9 L7,9 L12,5 L12,19 L7,15 L3,15 Z M16,8 Q21,12 16,16",
            "layout"=>"M4,3 L20,3 Q22,3 22,5 L22,19 Q22,21 20,21 L4,21 Q2,21 2,19 L2,5 Q2,3 4,3 Z M2,9 L22,9 M14,9 L14,21",
            "user"=>"M16,7 A4,4 0 1 1 8,7 A4,4 0 1 1 16,7 M4,21 C4,12 20,12 20,21",
            "exit"=>"M10,3 L5,3 Q3,3 3,5 L3,19 Q3,21 5,21 L10,21 M9,12 L22,12 M18,8 L22,12 L18,16",
            "refresh"=>"M20,8 A8,8 0 1 0 20,16 M20,3 L20,8 L15,8",
            "music"=>"M9,17 L9,5 Q9,3 11,3 L20,2 L20,15 M9,17 C9,22 2,22 2,18 C2,14 9,14 9,17 M20,15 C20,20 13,20 13,16 C13,12 20,12 20,15 M9,8 L20,6",
            _=>"M5,6 L19,6 M5,12 L19,12 M5,18 L19,18" };
        return new System.Windows.Controls.Viewbox { Width=size,Height=size,Child=new Path{Data=Geometry.Parse(data),Width=24,Height=24,Stretch=Stretch.Uniform,Stroke=SystemTheme.Ink,StrokeThickness=weight,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round,StrokeLineJoin=PenLineJoin.Round,Fill=Brushes.Transparent} };
    }
}
