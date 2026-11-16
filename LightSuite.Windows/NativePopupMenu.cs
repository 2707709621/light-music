using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace FeatherPlayer;

internal sealed record NativeMenuEntry(string Text, Action? Action = null,
    IReadOnlyList<NativeMenuEntry>? Children = null, bool Enabled = true, bool Checked = false,
    Func<CancellationToken, Task<IReadOnlyList<NativeMenuEntry>>>? LoadChildren = null, 
    Func<CancellationToken,Task<NativeMenuEntry>>? Refresh = null,
    Func<string, CancellationToken, Task<IReadOnlyList<NativeMenuEntry>>>? Search = null,
    string? Glyph = null, Func<FrameworkElement>? Content = null,
    Func<bool>? ReadChecked = null, Func<string>? ReadText = null, Func<bool>? ReadEnabled = null, string? DataKey = null, int ContentHeight = 98);

internal static class NativePopupMenu
{
    private static TrackedContextMenu? _open;
    internal static event Action? Updated;
    private static ResourceDictionary? _styles;
    // The MTA taskbar worker must never touch WPF objects.
    private static volatile bool _isOpen;
    private static IntPtr _openHandle;
    internal static bool IsOpen => _isOpen && NativeTaskbar.IsWindowVisible(Volatile.Read(ref _openHandle));

    private sealed class TrackedContextMenu : ContextMenu
    {
        internal MenuBuilder Builder = null!;
        internal MenuDismissal? Dismissal;
        internal bool KeepOpen;
        static TrackedContextMenu()
        {
            // Suppress capture-driven dismissal at its source. Coercing IsOpen
            // alone leaves WPF's internal menu mode out of sync with the popup.
            EventManager.RegisterClassHandler(typeof(TrackedContextMenu),Mouse.LostMouseCaptureEvent,
                new MouseEventHandler(KeepMenuCapture));
            EventManager.RegisterClassHandler(typeof(TrackedContextMenu),Mouse.PreviewMouseDownOutsideCapturedElementEvent,
                new MouseButtonEventHandler(KeepMenuClick));
            EventManager.RegisterClassHandler(typeof(TrackedContextMenu),Mouse.PreviewMouseUpOutsideCapturedElementEvent,
                new MouseButtonEventHandler(KeepMenuClick));
        }
        private static void KeepMenuCapture(object sender, MouseEventArgs e)
        {
            if (((TrackedContextMenu)sender).KeepOpen) e.Handled = true;
        }
        private static void KeepMenuClick(object sender, MouseButtonEventArgs e)
        {
            if (((TrackedContextMenu)sender).KeepOpen) e.Handled = true;
        }
        protected override void OnIsKeyboardFocusWithinChanged(DependencyPropertyChangedEventArgs e)
        {
            if (!KeepOpen) base.OnIsKeyboardFocusWithinChanged(e);
        }
        internal void Dismiss()
        {
            Dismissal?.Dispose();
            KeepOpen = false;
            if (IsMouseCaptureWithin) Mouse.Capture(null);
            SetCurrentValue(IsOpenProperty,false);
            FocusManager.SetFocusedElement(this,null);
        }
        protected override void OnInitialized(EventArgs e)
        {
            // Configure the popup before its HWND is built. DWM backdrop sampling
            // needs a redirected surface rather than a per-pixel layered window.
            if (LogicalTreeHelper.GetParent(this) is Popup host)
            {
                host.AllowsTransparency = false;
                // Our surface animates on entry. Native popup close animation
                // would otherwise keep the previous menu HWND alive on reopen.
                host.PopupAnimation = PopupAnimation.None;
            }
            base.OnInitialized(e);
        }
        protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);
            if (e.Property == IsOpenProperty && !(bool)e.NewValue && ReferenceEquals(_open, this))
            {
                _isOpen = false;
                Volatile.Write(ref _openHandle,IntPtr.Zero);
            }
        }
        protected override DependencyObject GetContainerForItemOverride() => new EntryMenuItem(Builder);
        protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
        {
            base.PrepareContainerForItemOverride(element, item);
            if (element is EntryMenuItem row && item is NativeMenuEntry entry)
            {
                row.Bind(entry);
                if (entry.Content == null && entry.Text != "-")
                    row.SetResourceReference(Control.PaddingProperty, "RootRowPadding");
            }
        }
    }

    private sealed class EntryMenuItem : MenuItem
    {
        private readonly MenuBuilder _builder;
        private NativeMenuEntry? _entry;
        private CancellationTokenSource? _request;
        private TextBox? _searchInput;
        internal EntryMenuItem(MenuBuilder builder)
        {
            _builder = builder;
            Loaded += (_, _) => _builder.Rows.Add(this);
            Unloaded += (_, _) =>
            {
                _builder.Rows.Remove(this);
            };
            // Song headers still play on click while their submenu opens on hover.
            PreviewMouseLeftButtonUp+=(_,e)=>
            {
                if(NearestMenuItem(e.OriginalSource)!=this || _entry is not {Action:{ } action,LoadChildren:not null})return;
                e.Handled=true;action();Refresh();
            };
            PreviewKeyDown+=(_,e)=>
            {
                if(e.Key!=System.Windows.Input.Key.Enter || NearestMenuItem(e.OriginalSource)!=this || _entry is not {Action:{ } action,LoadChildren:not null})return;
                e.Handled=true;action();Refresh();
            };
            SubmenuOpened += async (_, e) =>
            {
                if (!ReferenceEquals(e.OriginalSource, this)) return;
                if (_searchInput is { } input)
                    _ = Dispatcher.BeginInvoke((Action)(() =>
                    {
                        if (IsSubmenuOpen) FocusSearchInput(input);
                    }), System.Windows.Threading.DispatcherPriority.Input);
                _ = Dispatcher.BeginInvoke((Action)(() =>
                {
                    if (Template.FindName("PART_Popup", this) is Popup popup && popup.Child is FrameworkElement surface)
                    {
                        ApplyBackdrop(surface);
                        var parentCenter=PointToScreen(new Point(ActualWidth/2,ActualHeight/2));
                        var childCenter=surface.PointToScreen(new Point(surface.ActualWidth/2,surface.ActualHeight/2));
                        AnimateMenu(surface,childCenter.X>=parentCenter.X?-12:12,0);
                    }
                }));
                await ReloadChildrenAsync();
            };
            SubmenuClosed += (_, e) =>
            {
                if (!ReferenceEquals(e.OriginalSource,this)) return;
                _request?.Cancel();
            };
            Click += (_, e) =>
            {
                if (!ReferenceEquals(e.OriginalSource, this) || _entry?.Action is not { } action) return;
                e.Handled = true; action(); Refresh();
            };
        }
        internal string? DataKey => _entry?.DataKey;
        internal async Task ReloadChildrenAsync()
        {
            if (_entry?.LoadChildren is not { } load || !IsSubmenuOpen) return;
            _request?.Cancel(); _request?.Dispose();
            var request = _request = CancellationTokenSource.CreateLinkedTokenSource(_builder.Lifetime.Token);
            if (IsKeyboardFocusWithin) Focus();
            ItemsSource = LoadingEntries;
            try
            {
                var items = await load(request.Token);
                if (!request.IsCancellationRequested && ReferenceEquals(_request,request) && IsSubmenuOpen)
                    ItemsSource = items.Count == 0 ? EmptyEntries : items;
            }
            catch (OperationCanceledException) { }
            catch (Exception)
            {
                if (!request.IsCancellationRequested && IsSubmenuOpen)
                    ItemsSource = new[] { new NativeMenuEntry("读取失败，重新展开可重试",Enabled:false) };
            }
        }
        internal void Bind(NativeMenuEntry entry)
        {
            _entry = entry;
            var title = new TextBlock { Text = entry.Text, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            title.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding("Foreground") { Source = this });
            Header = title;
            IsEnabled = entry.Enabled; IsChecked = entry.Checked;
            StaysOpenOnClick = true;
            Tag = null;
            _searchInput = null;
            if (entry.Content != null)
            {
                Header = entry.Content();
                ItemsSource = entry.LoadChildren != null ? LoadingEntries : entry.Children;
                Style = (Style)_builder.Menu.Resources[ItemsSource == null ? "LightSeekItem" : "LightMenuItem"];
                Tag = "full";
                StaysOpenOnClick = true;
                Focusable = false;
                return;
            }
            Style = (Style)_builder.Menu.Resources[entry.Text == "-" ? "LightSeparatorItem" : "LightMenuItem"];
            if (entry.Text == "-") return;
            Icon = null;
            if (entry.Glyph != "none")
            {
                var glyph = LineIcons.Create(entry.Glyph ?? Kind(entry.Text), 16, 1.7);
                if (glyph is Viewbox box && box.Child is System.Windows.Shapes.Path path)
                    path.SetBinding(System.Windows.Shapes.Path.StrokeProperty, new System.Windows.Data.Binding("Foreground") { Source = this });
                Icon = glyph;
            }
            ItemsSource = entry.LoadChildren != null ? LoadingEntries : entry.Children;
            if (entry.Search is { } search) BindSearch(search);
            if(entry.Refresh!=null)_=RefreshEntryAsync(entry);
            RefreshLive();
        }
        internal void RefreshLive(bool reloadData = false)
        {
            if (_entry == null) return;
            if (_entry.ReadChecked != null) IsChecked = _entry.ReadChecked();
            if (_entry.ReadEnabled != null) IsEnabled = _entry.ReadEnabled();
            if (_entry.ReadText != null)
            {
                string value = _entry.ReadText();
                if (Header is TextBlock text) text.Text = value;
            }
            if (reloadData && _entry.Refresh != null) _ = RefreshEntryAsync(_entry);
        }
        private void BindSearch(Func<string, CancellationToken, Task<IReadOnlyList<NativeMenuEntry>>> search)
        {
            var input = _searchInput = new TextBox
            {
                Width = 230,
                MaxLength = 200,
                Style = (Style)_builder.Menu.Resources["LightSearchTextBox"]
            };
            System.Windows.Automation.AutomationProperties.SetName(input, "搜索歌曲或歌手");
            System.Windows.Input.InputMethod.SetIsInputMethodSuspended(input, false);
            System.Windows.Input.InputMethod.SetIsInputMethodEnabled(input, true);
            var panel = new StackPanel();
            var heading = new DockPanel { Margin = new Thickness(0, 3, 0, 6) };
            var icon = LineIcons.Create("search", 14, 1.8);
            if (icon is Viewbox box && box.Child is System.Windows.Shapes.Path path)
                path.SetResourceReference(System.Windows.Shapes.Path.StrokeProperty, "MenuMuted");
            icon.Margin = new Thickness(0, 0, 7, 0);
            heading.Children.Add(icon);
            var hint = new TextBlock
            {
                Text = "歌曲名或歌手 · 回车搜索",
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center
            };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "MenuMuted");
            heading.Children.Add(hint);
            panel.Children.Add(heading);
            panel.Children.Add(input);
            var inputRow = new SearchInputMenuItem
            {
                Header = panel,
                Style = (Style)_builder.Menu.Resources["LightSeekItem"],
                StaysOpenOnClick = true,
                Focusable = false
            };
            inputRow.SetResourceReference(Control.ForegroundProperty, "MenuInk");
            System.Windows.Input.InputMethod.SetIsInputMethodSuspended(inputRow, false);
            System.Windows.Input.InputMethod.SetIsInputMethodSuspended(this, false);
            var results = new EntryMenuItem(_builder);
            results.Bind(new NativeMenuEntry("搜索结果", Enabled: false));
            ItemsSource = new MenuItem[] { inputRow, results };
            input.PreviewMouseLeftButtonDown += (_, _) => FocusSearchInput(input);
            bool composing = false;
            System.Windows.Input.TextCompositionManager.AddPreviewTextInputStartHandler(input, (_, _) => composing = true);
            input.AddHandler(System.Windows.Input.TextCompositionManager.TextInputEvent,
                new System.Windows.Input.TextCompositionEventHandler((_, _) => composing = false), true);
            input.PreviewKeyDown += async (_, e) =>
            {
                if (composing || e.Key != System.Windows.Input.Key.Enter) return;
                e.Handled = true;
                string query = input.Text.Trim();
                if (query.Length == 0) return;
                _request?.Cancel();
                _request?.Dispose();
                var request = _request = CancellationTokenSource.CreateLinkedTokenSource(_builder.Lifetime.Token);
                results.Bind(new NativeMenuEntry("搜索结果 · " + query, Children: LoadingEntries));
                results.IsSubmenuOpen = true;
                try
                {
                    var songs = await search(query, request.Token);
                    if (!request.IsCancellationRequested && ReferenceEquals(_request, request) && IsSubmenuOpen)
                        results.ItemsSource = songs.Count == 0 ? EmptyEntries : songs;
                }
                catch (OperationCanceledException) { }
                catch (Exception)
                {
                    if (!request.IsCancellationRequested && ReferenceEquals(_request, request) && IsSubmenuOpen)
                        results.ItemsSource = new[] { new NativeMenuEntry("搜索失败，按回车重试", Enabled: false) };
                }
            };
        }
        private static void FocusSearchInput(TextBox input)
        {
            if (PresentationSource.FromVisual(input) is not HwndSource source) return;
            // Taskbar controls and ordinary menu popups do not activate. Text input
            // needs its own popup HWND to receive keyboard and IME messages.
            const int extendedStyleIndex = -20;
            const long noActivate = 0x08000000;
            long style = GetWindowLongPtr(source.Handle, extendedStyleIndex).ToInt64();
            if ((style & noActivate) != 0)
                SetWindowLongPtr(source.Handle, extendedStyleIndex, new IntPtr(style & ~noActivate));
            SetForegroundWindow(source.Handle);
            SetFocus(source.Handle);
            System.Windows.Input.Keyboard.Focus(input);
        }
        private int _refreshVersion;
        private async Task RefreshEntryAsync(NativeMenuEntry entry)
        {
            int version = ++_refreshVersion;
            IsEnabled = false;
            try
            {
                var updated=await entry.Refresh!(_builder.Lifetime.Token);
                if(!_builder.Lifetime.IsCancellationRequested && ReferenceEquals(_entry,entry) && version == _refreshVersion)
                {
                    Bind(updated with{Refresh=null});
                    _entry = updated with { Refresh = entry.Refresh };
                }
            }
            catch(OperationCanceledException) { }
            catch(Exception)
            {
                if(!_builder.Lifetime.IsCancellationRequested && ReferenceEquals(_entry,entry) && version == _refreshVersion)
                {
                    Bind(new NativeMenuEntry("收藏状态读取失败，请重新展开",Enabled:false));
                    _entry = entry;
                }
            }
        }
        private static MenuItem? NearestMenuItem(object source)
        {
            for(var node=source as DependencyObject;node!=null;node=node is Visual?VisualTreeHelper.GetParent(node):LogicalTreeHelper.GetParent(node))
                if(node is MenuItem row)return row;
            return null;
        }
        protected override DependencyObject GetContainerForItemOverride() => new EntryMenuItem(_builder);
        protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
        {
            base.PrepareContainerForItemOverride(element, item);
            if (element is EntryMenuItem row && item is NativeMenuEntry entry) row.Bind(entry);
        }
    }

    private sealed class SearchInputMenuItem : MenuItem
    {
        protected override void OnMouseLeave(System.Windows.Input.MouseEventArgs e)
        {
            // An IME candidate window can move the pointer out of this row.
            // MenuItem normally focuses its parent here, interrupting composition.
            if (!IsKeyboardFocusWithin) base.OnMouseLeave(e);
        }

        protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
        {
            // Let the editor and IME consume keys before menu navigation.
            if (!IsKeyboardFocusWithin || e.Key == System.Windows.Input.Key.Escape)
                base.OnKeyDown(e);
            else
                e.Handled = true;
        }

        protected override void OnTextInput(System.Windows.Input.TextCompositionEventArgs e)
        {
            if (IsKeyboardFocusWithin)
                e.Handled = true;
            else
                base.OnTextInput(e);
        }
    }

    private sealed record MenuBuilder(TrackedContextMenu Menu, CancellationTokenSource Lifetime)
    {
        internal HashSet<EntryMenuItem> Rows { get; } = new();
    }
    private static readonly NativeMenuEntry[] LoadingEntries = { new("正在读取…", Enabled: false) };
    private static readonly NativeMenuEntry[] EmptyEntries = { new("暂无歌曲", Enabled: false) };

    internal static void Show(IntPtr owner, IReadOnlyList<NativeMenuEntry> entries)
    {
        Close();
        var menu = new TrackedContextMenu
        {
            Placement = PlacementMode.MousePoint,
            PlacementTarget = HwndSource.FromHwnd(owner)?.RootVisual as UIElement
        };
        InstallResources(menu);
        // Keyed styles are explicit: a derived ContextMenu otherwise falls back to the system template.
        menu.Style = (Style)menu.Resources["LightMenu"];
        var screen = System.Windows.Forms.Screen.FromHandle(owner);
        double scale = menu.PlacementTarget is Visual target ? VisualTreeHelper.GetDpi(target).DpiScaleY : 1;
        double availableHeight = Math.Max(120, screen.WorkingArea.Height / scale - 12);
        menu.MaxHeight = availableHeight;
        menu.Resources["MenuMaxHeight"] = availableHeight;
        // Compact only the root rows on short work areas, keeping every root
        // action visible while long song lists retain their own scrolling.
        int fixedRows = 0, textRows = 0;
        foreach (var entry in entries)
        {
            if (entry.Content != null) fixedRows += entry.ContentHeight;
            else if (entry.Text == "-") fixedRows += 9;
            else textRows++;
        }
        double padding = Math.Clamp((availableHeight - fixedRows - 16 - textRows * 18) / Math.Max(1, textRows * 2), 0, 5);
        menu.Resources["RootRowPadding"] = new Thickness(8,padding,8,padding);
        menu.Builder = new MenuBuilder(menu, new CancellationTokenSource());
        menu.ItemsSource = entries;
        void Theme() { SetPalette(menu.Resources); ApplyBackdrop(menu); RefreshSubmenuBackdrops(menu); }
        Theme(); SystemTheme.Changed += Theme;
        MenuAppearanceSettings.Changed += Theme;
        _open = menu;
        menu.Closed += (_, _) =>
        {
            menu.Builder.Lifetime.Cancel();
            menu.KeepOpen = false;
            menu.Dismissal?.Dispose();
            SystemTheme.Changed -= Theme;
            MenuAppearanceSettings.Changed -= Theme;
            if (ReferenceEquals(_open, menu))
            {
                _open = null; _isOpen = false;
                Volatile.Write(ref _openHandle,IntPtr.Zero);
            }
            // WPF can retain the most recent popup. Do not retain its song list/delegates with it.
            menu.ItemsSource=null;
            menu.PlacementTarget=null;
        };
        menu.Opened += (_, _) =>
        {
            if (PresentationSource.FromVisual(menu) is HwndSource source)
            {
                Volatile.Write(ref _openHandle,source.Handle);
                _isOpen = true;
            }
            ApplyBackdrop(menu);
            BringToTop(menu, activate:true);
            menu.Dismissal?.Arm();
            if(menu.Template.FindName("MenuSurface",menu) is FrameworkElement surface)
            {
                var offset=TaskbarAnimationOffset();
                AnimateMenu(surface,offset.X,offset.Y);
            }
        };
        // Opening itself raises focus/capture notifications, before Opened.
        // Install protection and the close watchers before entering that path.
        menu.Dismissal = new MenuDismissal(menu.Dispatcher,menu.Dismiss,() => VisibleSurfaces(menu));
        menu.KeepOpen = menu.Dismissal.Start();
        menu.StaysOpen = menu.KeepOpen;
        menu.IsOpen = true;
    }

    internal static void Close()
    {
        var menu = _open;
        _open = null;
        _isOpen = false;
        Volatile.Write(ref _openHandle,IntPtr.Zero);
        menu?.Dismiss();
    }

    internal static void RaiseOpenMenus()
    {
        if (_open == null || !IsOpen) return;
        foreach (var surface in VisibleSurfaces(_open)) BringToTop(surface);
    }

    private static IEnumerable<Visual> VisibleSurfaces(TrackedContextMenu menu)
    {
        yield return menu;
        foreach (var row in menu.Builder.Rows.ToArray())
            if (row.IsSubmenuOpen && row.Template.FindName("PART_Popup",row) is Popup { IsOpen:true, Child:Visual surface })
                yield return surface;
    }

    internal static void ReplaceEntries(IReadOnlyList<NativeMenuEntry> entries)
    {
        if (_open == null) return;
        _open.Focus();
        _open.Builder.Lifetime.Cancel();
        _open.Builder.Rows.Clear();
        _open.ItemsSource = null;
        _open.Builder = new MenuBuilder(_open,new CancellationTokenSource());
        _open.ItemsSource = entries;
    }

    internal static void Refresh(bool reloadData = false)
    {
        if (_open == null) return;
        foreach (var row in _open.Builder.Rows.ToArray()) row.RefreshLive(reloadData);
        Updated?.Invoke();
    }

    internal static void ReloadData(string key)
    {
        if (_open == null) return;
        foreach (var row in _open.Builder.Rows.ToArray())
            if (row.DataKey == key && row.IsSubmenuOpen) _ = row.ReloadChildrenAsync();
    }

    internal static void InstallResources(FrameworkElement surface)
    {
        surface.Resources[typeof(MenuLighting)] = new MenuLighting(surface);
        surface.Resources.MergedDictionaries.Add(_styles ??= new ResourceDictionary { Source = new Uri("pack://application:,,,/LightSuite.Windows;component/FluentMenu.xaml", UriKind.Absolute) });
        surface.Resources["MenuMaxHeight"] = Math.Max(120, SystemParameters.WorkArea.Height - 12);
        SetPalette(surface.Resources);
    }
    private static void AnimateMenu(FrameworkElement surface,double fromX,double fromY)
    {
        if(!SystemParameters.MenuAnimation)return;
        var transform=new TranslateTransform();
        surface.RenderTransform=transform;
        var duration=new Duration(TimeSpan.FromMilliseconds(160));
        DoubleAnimation Animation(double from,double to)=>new(from,to,duration)
        {
            EasingFunction=new CubicEase{EasingMode=EasingMode.EaseOut},
            FillBehavior=FillBehavior.Stop
        };
        transform.BeginAnimation(TranslateTransform.XProperty,Animation(fromX,0));
        transform.BeginAnimation(TranslateTransform.YProperty,Animation(fromY,0));
        surface.BeginAnimation(UIElement.OpacityProperty,Animation(0,1));
    }
    private static Point TaskbarAnimationOffset()
    {
        var taskbar=NativeTaskbar.FindWindow("Shell_TrayWnd",null);
        if(taskbar==IntPtr.Zero || !NativeTaskbar.GetWindowRect(taskbar,out var bar))return new Point(0,14);
        var monitor=new MonitorInfo{Size=Marshal.SizeOf<MonitorInfo>()};
        if(!GetMonitorInfo(MonitorFromWindow(taskbar,2),ref monitor))return new Point(0,14);
        if(bar.Width>=bar.Height)
            return new Point(0,Math.Abs(bar.Bottom-monitor.Bounds.Bottom)<Math.Abs(bar.Top-monitor.Bounds.Top)?14:-14);
        return new Point(Math.Abs(bar.Right-monitor.Bounds.Right)<Math.Abs(bar.Left-monitor.Bounds.Left)?14:-14,0);
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo{public int Size;public NativeTaskbar.Rect Bounds,WorkArea;public uint Flags;}
    [DllImport("user32.dll")]private static extern IntPtr MonitorFromWindow(IntPtr hwnd,uint flags);
    [DllImport("user32.dll",CharSet=CharSet.Auto)]private static extern bool GetMonitorInfo(IntPtr monitor,ref MonitorInfo info);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    private static void BringToTop(Visual visual, bool activate = false)
    {
        if (PresentationSource.FromVisual(visual) is not HwndSource source) return;
        SetWindowPos(source.Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0013);
        if (activate) SetForegroundWindow(source.Handle);
    }
    private static void RefreshSubmenuBackdrops(ItemsControl parent)
    {
        for(int i=0;i<parent.Items.Count;i++)
        {
            if(parent.ItemContainerGenerator.ContainerFromIndex(i) is not MenuItem { IsSubmenuOpen:true } row)continue;
            if(row.Template.FindName("PART_Popup",row) is Popup { Child:Visual visual })ApplyBackdrop(visual);
            RefreshSubmenuBackdrops(row);
        }
    }
    internal static void SetPalette(ResourceDictionary resources)
    {
        bool dark = SystemTheme.MenuDark;
        var appearance = MenuAppearanceSettings.Current;
        var (surface,glow) = MenuAppearanceSettings.Colors(appearance,dark);
        resources["MenuInk"] = SystemTheme.MenuInk;
        resources["MenuMuted"] = SystemTheme.MenuSecondary;
        if (resources[typeof(MenuLighting)] is MenuLighting lighting)
        {
            lighting.Update(surface,glow,dark);
            if (!ReferenceEquals(resources["MenuBackground"],lighting.Background))
                resources["MenuBackground"] = lighting.Background;
        }
        Color outline = SystemTheme.Tone(dark ? 65u : 42u,8 + appearance.Glow * 16);
        outline.A = (byte)Math.Round(appearance.Opacity * (dark ? 50 : 62));
        resources["MenuBorder"] = new SolidColorBrush(outline);
        var focus = SystemTheme.Tone(dark ? 80u : 32u,32);
        focus.A = (byte)Math.Round(appearance.Opacity * 180);
        resources["MenuFocusBorder"] = new SolidColorBrush(focus);
        outline.A = (byte)Math.Round(appearance.Opacity * 45);
        resources["MenuLine"] = new SolidColorBrush(outline);
        var hover = SystemTheme.Tone(dark ? 70u : 55u,32);
        hover.A = (byte)Math.Round(appearance.Opacity * (dark ? 100 : 45));
        resources["MenuHover"] = new SolidColorBrush(hover);
        var track = SystemTheme.Tone(dark ? 75u : 45u,10);
        track.A = (byte)Math.Round(appearance.Opacity * 95);
        resources["MenuTrack"] = new SolidColorBrush(track);
        var input = SystemTheme.Tone(dark ? 5u : 99u,4);
        input.A = (byte)Math.Round(appearance.Opacity * 160);
        resources["InputBackground"] = new SolidColorBrush(input);
        resources["MenuAccent"] = SystemTheme.MenuInk;
    }
    internal static void ApplyBackdrop(Visual visual)
    {
        if (PresentationSource.FromVisual(visual) is not HwndSource source) return;
        BringToTop(visual);
        source.CompositionTarget.BackgroundColor = Colors.Transparent;
        var appearance = MenuAppearanceSettings.Current;
        var tint = SystemTheme.Blend(SystemTheme.Accent, SystemTheme.MenuDark ? Colors.Black : Colors.White, 1 - appearance.Glow * .16);
        uint color = 0x40000000u | (uint)tint.B << 16 | (uint)tint.G << 8 | tint.R;
        NativeMenuSurface.Apply(source.Handle, SystemTheme.MenuDark,
            (int)appearance.Blur, color, source.UsesPerPixelOpacity);
    }
    private static string Kind(string label) => label.Contains("暂停") ? "pause" : label.Contains("播放 / 继续") ? "play" : label.Contains("上一首") ? "previous" : label.Contains("下一首") ? "next" : label.Contains("喜欢") ? "heart" : label.Contains("歌单") || label.Contains("本地") ? "folder" : label.Contains("搜索") ? "search" : label.Contains("音量") ? "volume" : label.Contains("位置") || label.Contains("对齐") ? "layout" : label.Contains("登录") || label.Contains("账号") ? "user" : label.Contains("退出") ? "exit" : label.Contains("刷新") ? "refresh" : "music";
}
