using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace FeatherPlayer;

public partial class MainWindow : Window
{
    private readonly MusicServiceClient _api = new();
    private readonly AudioEngine _audio = new();
    private readonly AlbumArtwork _artwork = new();
    private ImageSource? _coverImage;
    private readonly PlaybackStore _playbackStore = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _clock;
    private TaskbarWidget? _widget;
    private TaskbarAppearanceController? _taskbarAppearance;
    private SpectrumController? _spectrum;
    private MusicLinkPublisher? _musicLink;
    private System.Windows.Forms.NotifyIcon? _tray;
    private System.Drawing.Icon? _ownedIcon;
    private CancellationTokenSource? _loadCts, _playCts;
    private CancellationTokenSource _accountCts = new();
    private int _accountGeneration;
    private IReadOnlyList<Song> _tracks = Array.Empty<Song>();
    private IReadOnlyList<Song> _queue = Array.Empty<Song>();
    private IReadOnlyList<LyricLine> _lyrics = Array.Empty<LyricLine>();
    private readonly List<Song> _localSongs = new();
    private Song? _current;
    private bool _isSeeking, _quitting, _ready;
    private int _mode, _playGeneration, _lyricIndex = -2;
    private string _lyricText = "";
    private bool _hasSession => _api.Session!=null;
    private bool _openingSong;
    private bool _loginOpen;
    private CancellationTokenSource? _qualityChangeCts;
    private string _currentQuality = "";
    private bool _openingAutoPlay;
    private long _lastResumeWrite;
    private double _retryPosition;
    private readonly TextState AccountButton=new(),PageTitle=new(),TrackCount=new(),PageSubtitle=new(),EmptyPanel=new(),EmptyTitle=new(),EmptyHint=new(),HeroLogin=new(),NowTitle=new(),NowArtist=new(),NowLyric=new(),PositionLabel=new(),DurationLabel=new(),PlayButton=new(),ModeButton=new(),StatusText=new(),TaskbarStatus=new(),TrackHeading=new(),SearchBox=new();
    private readonly ListState PlaylistList=new(),TracksGrid=new();
    private readonly ValueState VolumeSlider=new(){Value=.6},PositionSlider=new();
    private readonly CheckState TaskbarCheck=new();

    public MainWindow()
    {
        InitializeComponent();
        _clock = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _clock.Tick += (_, _) => UpdatePosition();
        _audio.Changed += OnAudioChanged;
        _audio.Ended += () => { _ = AdvanceAsync(1); };
        _audio.Error += text => { StatusText.Text = text; PushPlaybackState(); };
        VolumeSlider.Changed=value=>_audio.Volume=value;
        TaskbarCheck.Changed=()=>TaskbarToggled(this,new RoutedEventArgs());
        Loaded += OnLoaded;
        StateChanged += (_, _) => UpdateClock();
        Closing += OnClosing;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        DarkTitleBar();
        _widget = new TaskbarWidget(() => _ = AdvanceAsync(-1), () => _ = ToggleAsync(), () => _ = AdvanceAsync(1), RestoreWindow);
        _taskbarAppearance = new TaskbarAppearanceController(Dispatcher);
        _spectrum = new SpectrumController(Dispatcher);
        _musicLink = new MusicLinkPublisher(Dispatcher, () => _audio.IsPlaying);
        _widget.StatusChanged += text => Dispatcher.InvokeAsync(() => TaskbarStatus.Text = text);
        _hotkeys = new GlobalHotkeys(new WindowInteropHelper(this).Handle, HandleHotkey);
        SystemTheme.TintUi = false;
        LoadPreferences();
        _hotkeys.Load(_savedHotkeys);
        TaskbarCheck.IsChecked = true;
        LoadLocalLibrary();
        _ready = true;
        _audio.Volume = VolumeSlider.Value;
        _widget.SetEnabled(TaskbarCheck.IsChecked == true);
        CreateTray();
        Deactivated += (_, _) => { if (!OwnedWindows.Cast<Window>().Any(w => w.IsVisible)) Hide(); };
        Hide();
        var resume=(Keyboard.Modifiers & ModifierKeys.Shift)!=0 || Environment.GetCommandLineArgs().Contains("--safe-start") ? null : _playbackStore.Load();
        int initialGeneration=_playGeneration;
        if(resume?.Bookmark.Song.LocalPath!=null)await RestorePlaybackAsync(resume);
        try
        {
            var session = SessionStore.Load(_api.Provider);
            if (session != null)
            {
                _api.SetSession(session);
                SetAccountUi(session);
                await SyncAccountAsync(true,false);
            }
        }
        catch { StatusText.Text = "本地登录状态无法读取，请重新扫码登录。"; }
        if(resume!=null && _playGeneration==initialGeneration && _current==null)
            await RestorePlaybackAsync(resume);
        if(_hasSession)_=RefreshPlaylistsAsync();
        UpdateClock();
    }

    private void CreateTray()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("打开 light music", null, (_, _) => Dispatcher.Invoke(RestoreWindow));
        menu.Items.Add("播放 / 暂停", null, (_, _) => Dispatcher.InvokeAsync(() => _ = ToggleAsync()));
        menu.Items.Add("上一首", null, (_, _) => Dispatcher.InvokeAsync(() => _ = AdvanceAsync(-1)));
        menu.Items.Add("下一首", null, (_, _) => Dispatcher.InvokeAsync(() => _ = AdvanceAsync(1)));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Dispatcher.Invoke(Quit));
        try { if (Environment.ProcessPath != null) _ownedIcon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath); } catch { }
        _tray = new System.Windows.Forms.NotifyIcon { Icon = _ownedIcon ?? System.Drawing.SystemIcons.Application, Text = "light music", Visible = true };
        menu.Dispose();
        _tray.MouseUp += (_, args) => { if (args.Button == System.Windows.Forms.MouseButtons.Right) Dispatcher.Invoke(ShowSystemMenu); };
        UpdateTrayTheme(); SystemTheme.Changed += UpdateTrayTheme;
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(RestoreWindow);
    }

    private void UpdateTrayTheme()
    {
        if (_tray == null) return;
        System.Drawing.Icon? replacement = null;
        try
        {
            replacement = LineIcons.TrayIcon();
            _tray.Icon = replacement;
            var previous = _ownedIcon;
            _ownedIcon = replacement;
            replacement = null;
            previous?.Dispose();
        }
        catch (Exception error)
        {
            replacement?.Dispose();
            App.LogFailure(error);
        }
    }

    private void SetAccountUi(AccountSession session)
    {
        AccountButton.Content = "●  " + (string.IsNullOrWhiteSpace(session.Nickname) ? _api.Name + "账号" : session.Nickname);
        AccountButton.ToolTip = "点击管理登录状态";
        HeroLogin.Visibility = Visibility.Collapsed;
    }

    private async Task SyncAccountAsync(bool silent=false,bool refreshPlaylists=true)
    {
        int generation = _accountGeneration;
        CancellationToken ct = _accountCts.Token;
        StatusText.Text = "正在验证登录状态…";
        try
        {
            var account = await _api.GetAccountAsync(ct);
            if (ct.IsCancellationRequested || generation != _accountGeneration) return;
            _api.SetSession(account);
            SessionStore.Save(account);
            SetAccountUi(account);
            if(refreshPlaylists)await RefreshPlaylistsAsync();
            if (NativePopupMenu.IsOpen) ShowSystemMenu(refresh:true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (generation == _accountGeneration) { if(silent)ReportError("账号同步失败",ex);else NativeFailure("账号同步失败", ex); } }
    }

    private async Task RefreshPlaylistsAsync()
    {
        if (!_hasSession) { StatusText.Text = "请先登录" + _api.Name + "。"; return; }
        int generation = _accountGeneration;
        CancellationToken ct = _accountCts.Token;
        StatusText.Text = "正在读取歌单与收藏…";
        try
        {
            var playlists = await CachedPlaylistsAsync(ct);
            if (ct.IsCancellationRequested || generation != _accountGeneration) return;
            PlaylistList.ItemsSource = playlists;
            StatusText.Text = "已同步 " + playlists.Count + " 个歌单与收藏。";

        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (generation == _accountGeneration) ReportError("读取歌单失败", ex); }
    }

    private async void AccountClick(object sender, RoutedEventArgs e)
    {
        if (_hasSession)
        {
            var menu = new ContextMenu();
            var refresh = new MenuItem { Header = "重新同步账号与歌单" };
            refresh.Click += async (_, _) => await SyncAccountAsync();
            var login = new MenuItem { Header = "重新扫码 / 切换账号" };
            login.Click += async (_, _) => await LoginAsync();
            var logout = new MenuItem { Header = "退出" + _api.Name + "登录" };
            logout.Click += (_, _) => Logout();
            menu.Items.Add(refresh); menu.Items.Add(login); menu.Items.Add(new Separator()); menu.Items.Add(logout);
            menu.PlacementTarget = sender as UIElement ?? this;
            menu.IsOpen = true;
        }
        else await LoginAsync();
    }

    private async Task LoginAsync()
    {
        if (_loginOpen) return;
        _loginOpen = true;
        try
        {
            var provider = _api.Provider;
            AccountSession? loginSession;
            if (provider == MusicProvider.Kugou)
            {
                var dialog = new KugouLoginWindow { Owner = this };
                dialog.ShowDialog();
                loginSession = dialog.Session;
            }
            else
            {
                var dialog = new OfficialLoginWindow(provider) { Owner = this };
                dialog.ShowDialog();
                loginSession = dialog.Session;
            }
            if (loginSession != null && provider == _api.Provider)
            {
                InvalidateAccountRequests();
                _loadCts?.Cancel(); _playCts?.Cancel();
                ResetOnlinePlayback();
                _api.SetSession(loginSession);
                SetAccountUi(loginSession);
                PlaylistList.ItemsSource = null;
                ShowTracks(Array.Empty<Song>(), "我的音乐", "正在同步账号…");
                await SyncAccountAsync();
            }
        }
        finally { _loginOpen = false; }
    }
    internal async void OpenOfficialLoginForUser() => await LoginAsync();

    private void Logout()
    {
        InvalidateAccountRequests();
        _loadCts?.Cancel();
        _playCts?.Cancel();
        _api.SetSession(null); SessionStore.Clear(_api.Provider);
        PlaylistList.ItemsSource = null;
        AccountButton.Content = "登录" + _api.Name + "   →";
        HeroLogin.Visibility = Visibility.Visible;
        ResetOnlinePlayback();
        ShowTracks(_localSongs.ToArray(), "本地音乐", "已退出" + _api.Name + "登录。");
        PushPlaybackState();
        if (NativePopupMenu.IsOpen) ShowSystemMenu(refresh:true);
    }

    private void InvalidateAccountRequests()
    {
        _accountGeneration++;
        _likedMids = null;
        _guessPlaybackCts?.Cancel();
        _qualityChangeCts?.Cancel();
        _accountCts.Cancel(); _accountCts.Dispose(); _accountCts = new CancellationTokenSource();
    }
    private void ResetOnlinePlayback()
    {
        if (_current?.LocalPath != null) return;
        try { _playbackStore.Clear(); } catch { }
        _audio.Close(); _current = null; _queue = Array.Empty<Song>(); _lyrics = Array.Empty<LyricLine>();
        _currentQuality = "";
        _coverImage = null;
        SystemTheme.SetAccent(null);
        _lyricIndex = -2; _lyricText = "";
        NowTitle.Text = "尚未播放"; NowArtist.Text = "选择一首喜欢的音乐"; NowLyric.Text = "";
        PushPlaybackState();
    }

    private async void RefreshClick(object sender, RoutedEventArgs e) => await RefreshPlaylistsAsync();
    private void LikedClick(object sender, RoutedEventArgs e)
    {
        if (!_hasSession) { AccountClick(sender, e); return; }
        var liked = (PlaylistList.ItemsSource as IEnumerable<Playlist>)?.FirstOrDefault(p => p.IsLiked);
        if (liked == null) { _ = RefreshPlaylistsAsync(); return; }
        if (Equals(PlaylistList.SelectedItem, liked)) _ = LoadPlaylistAsync(liked);
        else PlaylistList.SelectedItem = liked;
    }

    private async void PlaylistChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PlaylistList.SelectedItem is Playlist playlist) await LoadPlaylistAsync(playlist);
    }

    private Task LoadPlaylistAsync(Playlist playlist) => LoadViewAsync(ct => _api.GetPlaylistSongsAsync(playlist, ct), playlist.Name, _api.Name + " · " + (playlist.IsLiked ? "我的收藏" : "歌单"));

    private async Task LoadViewAsync(Func<CancellationToken, Task<IReadOnlyList<Song>>> action, string title, string subtitle)
    {
        _loadCts?.Cancel(); _loadCts?.Dispose();
        var source = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _loadCts = source;
        ShowTracks(Array.Empty<Song>(), title, subtitle);
        EmptyTitle.Text = "正在读取音乐…"; EmptyHint.Text = "";
        StatusText.Text = "加载中…";
        try
        {
            var songs = await action(source.Token);
            source.Token.ThrowIfCancellationRequested();
            ShowTracks(songs, title, subtitle);
            StatusText.Text = songs.Count == 0 ? "没有读取到歌曲。私密歌单需要有效登录状态。" : "已加载 " + songs.Count + " 首歌曲，双击播放。";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (ReferenceEquals(source, _loadCts))
            {
                EmptyTitle.Text = "暂时无法读取"; EmptyHint.Text = "可以重试，或先播放本地音乐。";
                ReportError("音乐读取失败", ex);
            }
        }
    }

    private void ShowTracks(IReadOnlyList<Song> songs, string title, string subtitle)
    {
        _tracks = songs;
        TracksGrid.ItemsSource = songs;
        PageTitle.Text = title;
        PageSubtitle.Text = subtitle;
        TrackHeading.Text = title == "本地音乐" ? "本地歌曲" : "歌曲列表";
        TrackCount.Text = songs.Count == 0 ? "" : songs.Count + " 首歌曲";
        EmptyPanel.Visibility = songs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = "这里还没有歌曲";
        EmptyHint.Text = "搜索歌曲、打开歌单，或拖入本地音乐。";
    }

    private async void SearchClick(object sender, RoutedEventArgs e) => await SearchAsync();
    private async void SearchKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; await SearchAsync(); } }
    private Task SearchAsync()
    {
        string query = SearchBox.Text.Trim();
        if (query.Length == 0) return Task.CompletedTask;
        PlaylistList.SelectedItem = null;
        return LoadViewAsync(ct => _api.SearchAsync(query, ct), "搜索 · " + query, _api.Name + " · 搜索结果");
    }

    private void LocalClick(object sender, RoutedEventArgs e)
    {
        _loadCts?.Cancel(); PlaylistList.SelectedItem = null;
        ShowTracks(_localSongs.ToArray(), "本地音乐", "拖入音乐文件，或点击右上角导入。");
        StatusText.Text = "支持 MP3、FLAC、M4A、WAV、WMA、AAC，实际解码取决于系统。";
    }

    private void ImportClick(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = "选择音乐文件", Filter = "音频文件|*.mp3;*.flac;*.m4a;*.wav;*.wma;*.aac|所有文件|*.*", Multiselect = true };
        if (picker.ShowDialog(this) == true) ImportFiles(picker.FileNames);
    }
    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) ImportFiles(files);
    }
    private void ImportFiles(IEnumerable<string> paths)
    {
        var known = new HashSet<string>(_localSongs.Select(s => s.LocalPath!), StringComparer.OrdinalIgnoreCase);
        var allowed = new HashSet<string>(new[] { ".mp3", ".flac", ".m4a", ".wav", ".wma", ".aac" }, StringComparer.OrdinalIgnoreCase);
        int added = 0;
        foreach (string path in paths)
        {
            if (!File.Exists(path) || !allowed.Contains(Path.GetExtension(path)) || !known.Add(path)) continue;
            _localSongs.Add(new Song("local:" + path, Path.GetFileNameWithoutExtension(path), "本地音乐", "", 0, LocalPath: path)); added++;
        }
        SaveLocalLibrary(); LocalClick(this, new RoutedEventArgs());
        StatusText.Text = "已导入 " + added + " 首音乐。同名 .lrc 文件会自动读取。";
    }

    private async void TrackDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var element = e.OriginalSource as DependencyObject;
        while (element != null && element is not DataGridRow) element = VisualTreeHelper.GetParent(element);
        if (element is DataGridRow { Item: Song song }) { _queue = _tracks.ToArray(); await PlaySongAsync(song); }
    }
    private async void PlayAllClick(object sender, RoutedEventArgs e)
    {
        if (_tracks.Count == 0) { StatusText.Text = "先打开一个歌单，搜索歌曲或导入本地音乐。"; return; }
        _queue = _tracks.ToArray(); await PlaySongAsync(_queue[0]);
    }
    private async Task PlaySongAsync(Song song,double startPosition=0,bool autoPlay=true,bool restoring=false,PlaybackSource? resolvedSource=null)
    {
        _guessPlaybackCts?.Cancel();
        _qualityChangeCts?.Cancel();
        SavePlaybackResume(true);
        int generation = ++_playGeneration;
        _playCts?.Cancel(); _playCts?.Dispose();
        var source = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _playCts = source;
        _openingSong=true;_retryPosition=startPosition;
        _openingAutoPlay = autoPlay;
        _currentQuality = "";
        _audio.Close(); _current = song; _lyrics = Array.Empty<LyricLine>(); _lyricIndex = -2; _lyricText = "";
        _coverImage = null;
        SystemTheme.SetAccent(null);
        _ = LoadArtworkAsync(song, source.Token, generation);
        NowTitle.Text = song.Title; NowArtist.Text = song.Artist; NowLyric.Text = "正在打开…";
        StatusText.Text = "正在打开：" + song.Title;
        PushPlaybackState();
        NativePopupMenu.Refresh(reloadData:true);
        try
        {
            var playback = song.LocalPath != null ? new PlaybackSource(song.LocalPath, "本地文件")
                : resolvedSource ?? await _api.GetPlaybackSourceAsync(song, source.Token);
            source.Token.ThrowIfCancellationRequested();
            await _audio.OpenAsync(playback.Url, source.Token);
            source.Token.ThrowIfCancellationRequested();
            if (generation != _playGeneration) return;
            if(startPosition>0)_audio.Seek(PlaybackStore.ClampPosition(startPosition,_audio.Duration));
            if(autoPlay)_audio.Play();
            _currentQuality = playback.QualityLabel;
            _openingSong=false;_retryPosition=0;
            StatusText.Text = (autoPlay?"正在播放：":"已恢复上次位置：") + song.Title;
            _ = LoadLyricsAsync(song, source.Token, generation);
            UpdatePosition();
            SavePlaybackResume(true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (generation == _playGeneration)
            {
                WritePlaybackError(song,ex,restoring);
                NowLyric.Text = "暂时无法播放这首歌";
                if(restoring)ReportError("上次歌曲暂时无法恢复，可稍后点击播放重试",ex);
                else NativeFailure("播放失败；可能是账号权限、版权限制或接口变动", ex);
                PushPlaybackState();
            }
        }
        finally { if(generation==_playGeneration)_openingSong=false; }
    }

    private async Task LoadArtworkAsync(Song song, CancellationToken ct, int generation)
    {
        try
        {
            if (song.LocalPath == null && string.IsNullOrWhiteSpace(song.CoverUrl))
                song = song with { CoverUrl = await _api.GetCoverUrlAsync(song, ct) };
            var image = await _artwork.ReadAsync(song, ct);
            if (ct.IsCancellationRequested || generation != _playGeneration) return;
            _coverImage = image?.Image;
            SystemTheme.SetAccent(image?.Dominant);
            PushPlaybackState();
        }
        catch (OperationCanceledException) { }
        catch { /* Missing artwork must not interrupt audio. */ }
    }

    private async Task RestorePlaybackAsync(RestoredPlayback saved)
    {
        var song=saved.Bookmark.Song;
        if(song.LocalPath!=null && !File.Exists(song.LocalPath))return;
        if(song.LocalPath==null && (song.Provider != _api.Provider || saved.Bookmark.AccountId != PlaybackAccountId))return;
        _queue=saved.Queue;
        await PlaySongAsync(song,saved.Bookmark.Position,autoPlay:false,restoring:true);
    }
    private void WritePlaybackError(Song song,Exception error,bool restoring)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            var codes=new List<object>();
            for(Exception? item=error;item!=null;item=item.InnerException)
                codes.Add(new
                {
                    type=item.GetType().FullName,
                    hresult="0x"+item.HResult.ToString("X8"),
                    message=System.Text.RegularExpressions.Regex.Replace(item.Message, @"https?://[^\s""'<>]+", "[音频地址]", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                });
            File.WriteAllText(Path.Combine(AppPaths.DataDirectory,"playback-error.json"),JsonSerializer.Serialize(new{
                version=typeof(MainWindow).Assembly.GetName().Version?.ToString(),time=DateTimeOffset.Now,
                os=Environment.OSVersion.VersionString,runtime=System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                architecture=System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),localFile=song.LocalPath!=null,
                restoring,provider=_api.Provider.ToString(),transport=_audio.PlaybackTransport,hasMusicSession=_api.Session!=null,qqCode=(error as QQMusicException)?.Code,
                neteaseCode=(error as NeteaseMusicException)?.Code,kugouCode=(error as KugouMusicException)?.Code,errors=codes
            },new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }));
        }
        catch { }
    }
    private void SavePlaybackResume(bool force=false)
    {
        if(!_ready || _openingSong || _current==null || _audio.Duration<=0)return;
        long now=Environment.TickCount64;
        if(!force && now-_lastResumeWrite<10000)return;
        try
        {
            _playbackStore.Save(_current,_audio.Position,_audio.IsPlaying,_current.LocalPath==null?PlaybackAccountId:"",_queue);
            _lastResumeWrite=now;
        }
        catch { }
    }
    internal void PrepareForSystemShutdown(){SavePlaybackResume(true);SavePreferences();_quitting=true;}

    private async Task LoadLyricsAsync(Song song, CancellationToken ct, int generation)
    {
        try
        {
            IReadOnlyList<LyricLine> lines;
            if (song.LocalPath != null)
            {
                string path = Path.ChangeExtension(song.LocalPath, ".lrc");
                lines = File.Exists(path) ? LyricTimeline.Parse(await File.ReadAllTextAsync(path, ct)) : Array.Empty<LyricLine>();
            }
            else lines = await _api.GetLyricsAsync(song, ct);
            if (generation != _playGeneration || ct.IsCancellationRequested) return;
            _lyrics = lines; _lyricIndex = -2; UpdatePosition();
        }
        catch (OperationCanceledException) { }
        catch { if (generation == _playGeneration) { _lyrics = Array.Empty<LyricLine>(); _lyricIndex = -2; UpdatePosition(); } }
    }

    private async Task ToggleAsync()
    {
        if (_current == null)
        {
            Song? selected = TracksGrid.SelectedItem as Song ?? _tracks.FirstOrDefault();
            if (selected != null) { _queue = _tracks.ToArray(); await PlaySongAsync(selected); }
            else StatusText.Text = "先选一首音乐。";
            return;
        }
        if (_audio.Duration <= 0 && !_audio.IsOpening) { await PlaySongAsync(_current,_retryPosition); return; }
        _audio.Toggle(); UpdatePosition();
    }
    private async Task AdvanceAsync(int direction)
    {
        if (_mode == 1 && _current != null)
        {
            if (_audio.Duration > 0 && !_audio.IsOpening)
            {
                _audio.Seek(0);
                _audio.Play();
                UpdatePosition();
            }
            else await PlaySongAsync(_current);
            return;
        }
        if (_queue.Count == 0) { await ToggleAsync(); return; }
        int index=-1;
        if(_current!=null)for(int i=0;i<_queue.Count;i++)if(_queue[i].Mid==_current.Mid){index=i;break;}
        int next = _mode == 2 && _queue.Count > 1 ? Random.Shared.Next(_queue.Count - 1) : (index + direction + _queue.Count) % _queue.Count;
        if (_mode == 2 && next >= index && index >= 0) next++;
        await PlaySongAsync(_queue[Math.Clamp(next, 0, _queue.Count - 1)]);
    }
    private async void ToggleClick(object sender, EventArgs e) => await ToggleAsync();
    private async void PreviousClick(object sender, EventArgs e) => await AdvanceAsync(-1);
    private async void NextClick(object sender, EventArgs e) => await AdvanceAsync(1);
    private void ModeClick(object sender, RoutedEventArgs e) { _mode = (_mode + 1) % 3; ModeButton.Content = new[] { "顺序", "单曲", "随机" }[_mode]; SavePreferences(); }
    private void VolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (_audio != null) _audio.Volume = e.NewValue; }
    private void SeekStarted(object sender, MouseButtonEventArgs e) => _isSeeking = true;
    private void SeekEnded(object sender, MouseButtonEventArgs e) { _audio.Seek(PositionSlider.Value); _isSeeking = false; UpdatePosition(); }
    private void SeekKeyUp(object sender, KeyEventArgs e) { _audio.Seek(PositionSlider.Value); UpdatePosition(); }

    private void OnAudioChanged()
    {
        PlayButton.Content = _audio.IsPlaying ? "Ⅱ" : "▶";
        if (_tray != null) _tray.Text = _current == null ? "light music" : Limit(_current.Title, 60);
        UpdateClock(); PushPlaybackState();
        SavePlaybackResume(true);
    }
    private void UpdateClock()
    {
        if (_audio.IsPlaying && (IsVisible && WindowState != WindowState.Minimized || TaskbarCheck.IsChecked == true)) _clock.Start();
        else _clock.Stop();
    }
    private void UpdatePosition()
    {
        double position = _audio.Position, duration = _audio.Duration;
        if(IsVisible)
        {
            if (!_isSeeking) { PositionSlider.Maximum = Math.Max(1, duration); PositionSlider.Value = Math.Clamp(position, 0, PositionSlider.Maximum); }
            PositionLabel.Text = FormatTime(position); DurationLabel.Text = FormatTime(duration);
        }
        SavePlaybackResume();
        int index = LyricTimeline.FindIndex(_lyrics, position);
        if (_lyricIndex != index)
        {
            _lyricIndex = index;
            _lyricText = index >= 0 ? _lyrics[index].Text : _lyrics.Count > 0 ? "♪" : "暂无歌词";
            NowLyric.Text = _current == null ? "" : _lyricText;
            PushPlaybackState();
        }
    }
    private void PushPlaybackState()
    {
        _musicLink?.Publish();
        _spectrum?.SetPlaying(_audio.IsPlaying);
        _widget?.Update(new PlaybackState(_current, _audio.IsPlaying, _lyricText, _audio.Position, _audio.Duration, _lyricIndex >= 0 && _lyricIndex < _lyrics.Count ? _lyrics[_lyricIndex].Seconds : 0, _lyricIndex >= 0 && _lyricIndex + 1 < _lyrics.Count ? _lyrics[_lyricIndex + 1].Seconds : _audio.Duration, _coverImage));
        NativePopupMenu.Refresh();
    }
    private static string FormatTime(double seconds) => TimeSpan.FromSeconds(Math.Max(0, double.IsFinite(seconds) ? seconds : 0)).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss");
    private static string Limit(string text, int length) => text.Length > length ? text[..length] : text;

    private void TaskbarToggled(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _widget?.SetEnabled(TaskbarCheck.IsChecked == true);
        TaskbarStatus.Text = _widget?.Status ?? "未启用";
        PushPlaybackState(); UpdateClock(); SavePreferences();
    }
    public void EnableTaskbarForCheck() => Loaded += (_, _) => TaskbarCheck.IsChecked = true;
    private void RestoreWindow() => ShowSystemMenu();
    private void DismissClick(object sender, RoutedEventArgs e) { Hide(); UpdateClock(); }
    internal void OpenMenuForCheck() => RestoreWindow();
    private void ExitClick(object sender, RoutedEventArgs e) => Quit();
    private void Quit() { _quitting = true; NativePopupMenu.Close(); Close(); }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_quitting)
        {
            e.Cancel = true; Hide(); UpdateClock(); SavePreferences();
            if (_tray != null) _tray.ShowBalloonTip(1800, "light music 仍在播放", "双击通知区域图标可打开，右键可退出。", System.Windows.Forms.ToolTipIcon.Info);
            return;
        }
        SavePlaybackResume(true);SavePreferences(); _lifetime.Cancel(); _guessPlaybackCts?.Cancel(); _qualityChangeCts?.Cancel(); _loadCts?.Cancel(); _playCts?.Cancel(); _accountCts.Cancel();
        _hotkeys?.Dispose();
        _clock.Stop(); _musicLink?.Dispose(); _spectrum?.Dispose(); _taskbarAppearance?.Dispose(); _widget?.Dispose(); _audio.Dispose(); _api.Dispose(); _artwork.Dispose();
        if (_tray != null) { _tray.Visible = false; _tray.ContextMenuStrip?.Dispose(); _tray.Dispose(); }
        SystemTheme.Changed -= UpdateTrayTheme; _ownedIcon?.Dispose();
        _loadCts?.Dispose(); _playCts?.Dispose(); _accountCts.Dispose(); _lifetime.Dispose();
        Application.Current.Shutdown();
    }
    private void ReportError(string prefix, Exception error)

    {

        string detail = error is System.Net.Http.HttpRequestException ? "网络请求失败，请检查网络连接。" : error is TimeoutException ? "请求超时，请重试。" : error.Message;

        // Never put credentials, signed URLs, or raw HTTP responses into the UI or logs.

        if (detail.Contains("http", StringComparison.OrdinalIgnoreCase) || detail.Contains("key=", StringComparison.OrdinalIgnoreCase) || detail.Length > 220) detail = "服务返回异常，请重试。";

        StatusText.Text = prefix + "。" + detail;

    }



    private string PreferencesPath => Path.Combine(AppPaths.DataDirectory, "preferences.json");
    private void LoadPreferences()
    {
        try
        {
            Preferences? p = null;
            foreach (string path in new[] { PreferencesPath, PreferencesPath + ".bak" })
            {
                try
                {
                    if (File.Exists(path)) p = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(path));
                    if (p != null) break;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
            }
            p ??= new Preferences(Volume: .75, Taskbar: true, Mode: 0);
            SystemTheme.TintUi = p.MonetUiColors;
            _savedHotkeys = p.Hotkeys;
            LyricStyleSettings.SetSway(false, p.WholeSway ?? new LyricSwayOptions());
            LyricStyleSettings.SetSway(true, p.LetterSway ?? new LyricSwayOptions(Amount: 1.2));
            LyricStyleSettings.RandomLayout = p.LyricRandomLayout;
            LyricStyleSettings.Randomness = p.LyricRandomness;
            LyricStyleSettings.Luminous = p.LuminousLyrics;
            _api.Select(Enum.IsDefined(p.Provider) ? p.Provider : MusicProvider.Qq);
            _api.QqQuality = Enum.IsDefined(p.QqQuality) && p.QqQuality != AudioQuality.HiRes ? p.QqQuality : AudioQuality.Auto;
            _api.NeteaseQuality = Enum.IsDefined(p.NeteaseQuality) ? p.NeteaseQuality : AudioQuality.Auto;
            _api.KugouQuality = Enum.IsDefined(p.KugouQuality) ? p.KugouQuality : AudioQuality.Auto;
            MenuAppearanceSettings.Set(p.Appearance ?? new MenuAppearance(
                Opacity: .6448663853727145, Blur: MenuBlurMode.Acrylic, Glow: .47327707454289725));
            _taskbarAppearance?.Configure(p.TaskbarStyle,p.TaskbarStyleMotion);
            _spectrum?.Configure(p.Spectrum ?? new SpectrumOptions());
            if (_widget != null)
            {
                _widget.Alignment = Math.Clamp(p.LyricAlignment, 0, 2);
                _widget.Layout = p.Layout ?? new WidgetLayout(
                    Width: 299, Left: 8, TextX: 124, TextY: 8, ButtonsX: 8, ButtonsY: 7);
            }
            VolumeSlider.Value = Math.Clamp(p.Volume, 0, 1); TaskbarCheck.IsChecked = p.Taskbar;
            _mode = Math.Clamp(p.Mode, 0, 2); ModeButton.Content = new[] { "顺序", "单曲", "随机" }[_mode];
        }
        catch { }
    }
    private void SavePreferences()
    {
        if (!_ready) return;
        string temporary = PreferencesPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            var preferences = new Preferences(VolumeSlider.Value, TaskbarCheck.IsChecked == true, _mode,
                _widget?.Alignment ?? 1, _widget?.Layout, _api.Provider, _api.QqQuality, _api.NeteaseQuality, MenuAppearanceSettings.Current,
                _taskbarAppearance?.Enabled ?? false,_taskbarAppearance?.Motion ?? true,_spectrum?.Options, _api.KugouQuality, _hotkeys?.Bindings.ToDictionary(pair => pair.Key, pair => pair.Value), LyricStyleSettings.Luminous, LyricStyleSettings.RandomLayout, LyricStyleSettings.Randomness, SystemTheme.TintUi, LyricStyleSettings.WholeSway, LyricStyleSettings.LetterSway);
            byte[] data = JsonSerializer.SerializeToUtf8Bytes(preferences);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(data);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(PreferencesPath))
                File.Replace(temporary, PreferencesPath, PreferencesPath + ".bak");
            else
                File.Move(temporary, PreferencesPath);
        }
        catch { StatusText.Text = "设置保存失败，请检查本地数据目录的可写权限。"; }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
    private void LoadLocalLibrary()
    {
        try
        {
            string file = Path.Combine(AppPaths.DataDirectory, "library.json");
            if (!File.Exists(file)) return;
            var paths = JsonSerializer.Deserialize<string[]>(File.ReadAllText(file)) ?? Array.Empty<string>();
            foreach (string path in paths.Distinct(StringComparer.OrdinalIgnoreCase).Where(File.Exists)) _localSongs.Add(new Song("local:" + path, Path.GetFileNameWithoutExtension(path), "本地音乐", "", 0, LocalPath: path));
        }
        catch { }
    }
    private void SaveLocalLibrary()
    {
        try { Directory.CreateDirectory(AppPaths.DataDirectory); File.WriteAllText(Path.Combine(AppPaths.DataDirectory, "library.json"), JsonSerializer.Serialize(_localSongs.Select(s => s.LocalPath).ToArray())); }
        catch { StatusText.Text = "无法保存本地资料库。"; }
    }
    internal async Task RunUiSmokeAsync(string output)
    {
        var checks = new Dictionary<string, bool>();
        string folder = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, "gui-fixtures");
        try
        {
            _audio.Volume = 0;
            ImportFiles(new[] { Path.Combine(folder, "界面验证01.wav"), Path.Combine(folder, "界面验证02.wav") });
            checks["importTwoSongs"] = _tracks.Count == 2;
            _queue = _tracks.ToArray();
            await PlaySongAsync(_queue[0]);
            await Task.Delay(700);
            checks["playFirst"] = _audio.IsPlaying && _current == _queue[0] && _audio.Position > .2;
            _audio.Pause();
            checks["pause"] = !_audio.IsPlaying;
            _audio.Seek(7.5); UpdatePosition();
            checks["seekLyric"] = NowLyric.Text.Contains("暂停、上一首、下一首", StringComparison.Ordinal);
            await AdvanceAsync(1);
            checks["next"] = _current == _queue[1] && _audio.IsPlaying;
            await AdvanceAsync(-1);
            checks["previous"] = _current == _queue[0] && _audio.IsPlaying;
            _audio.Pause();
            checks["idleClockStopped"] = !_clock.IsEnabled;
            await Task.Delay(1200);
            checks["taskbarParent"] = TaskbarCheck.IsChecked == true && _widget?.IsEmbedded == true;
            File.WriteAllText(output, JsonSerializer.Serialize(new { passed = checks.Values.Count(x => x), failed = checks.Values.Count(x => !x), checks }, new JsonSerializerOptions { WriteIndented = true }));
            _audio.Close(); _current = null; _queue = Array.Empty<Song>(); _localSongs.Clear(); SaveLocalLibrary();
            ShowTracks(Array.Empty<Song>(), "音乐，本来就该轻盈。", "登录" + _api.Name + "，找回你的歌单与收藏。");
            NowTitle.Text = "尚未播放"; NowArtist.Text = "选择一首喜欢的音乐"; NowLyric.Text = ""; _lyricText = "";
            PushPlaybackState(); StatusText.Text = "本地播放与切歌检查已完成。";
        }
        catch (Exception ex)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            File.WriteAllText(output, JsonSerializer.Serialize(new { failed = 1, type = ex.GetType().Name, checks }));
        }
    }

    private void DarkTitleBar()
    {
        int on = 1;
        try { DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref on, sizeof(int)); } catch { }
    }
    private static ImageSource ThumbnailIcon(string path)
    {
        var image = new DrawingImage(new GeometryDrawing(Brushes.DimGray, null, Geometry.Parse(path)));
        image.Freeze(); return image;
    }
    internal async Task WriteDiagnosticsAsync(string output)
    {
        try
        {
            await Task.Delay(20000, _lifetime.Token);
            using var process = Process.GetCurrentProcess();
            var before = process.TotalProcessorTime;
            var elapsed = Stopwatch.StartNew();
            await Task.Delay(10000, _lifetime.Token);
            process.Refresh();
            double cpuOneCore = (process.TotalProcessorTime - before).TotalSeconds / elapsed.Elapsed.TotalSeconds * 100;
            var report = new
            {
                version = typeof(MainWindow).Assembly.GetName().Version?.ToString(),
                runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                os = Environment.OSVersion.VersionString,
                measurementSeconds = elapsed.Elapsed.TotalSeconds,
                workingSetMiB = Math.Round(process.WorkingSet64 / 1048576d, 2),
                privateBytesMiB = Math.Round(process.PrivateMemorySize64 / 1048576d, 2),
                managedHeapMiB = Math.Round(GC.GetTotalMemory(false) / 1048576d, 2),
                cpuOneCorePercent = Math.Round(cpuOneCore, 3),
                cpuMachinePercent = Math.Round(cpuOneCore / Environment.ProcessorCount, 3),
                playing = _audio.IsPlaying,
                mainVisible = IsVisible,
                playbackTimerRunning = _clock.IsEnabled,
                taskbar = _widget?.GetDiagnostics()
            };
            string full = Path.GetFullPath(output);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllTextAsync(full, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), _lifetime.Token);
            // Render only our own WPF surface, with no screen capture or account credentials.
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(this);
            var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
            png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.ChangeExtension(full, ".png"));
            png.Save(stream);
        }
        catch (OperationCanceledException) { }
        catch { }
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    private string PlaybackAccountId => _api.Provider switch { MusicProvider.Netease => "netease/" + (_api.Session?.Uin ?? ""), MusicProvider.Kugou => "kugou/" + (_api.Session?.Uin ?? ""), _ => _api.Session?.Uin ?? "" };

    private async Task SwitchProviderAsync(MusicProvider provider)
    {
        if (provider == _api.Provider || _loginOpen) return;
        SavePlaybackResume(true);
        InvalidateAccountRequests();
        _loadCts?.Cancel();
        _playCts?.Cancel();
        ResetOnlinePlayback();
        _api.Select(provider);
        PlaylistList.ItemsSource = null;
        ShowTracks(Array.Empty<Song>(), "我的音乐", _api.Name);
        SavePreferences();
        try
        {
            var session = SessionStore.Load(provider);
            _api.SetSession(session);
            if (session != null)
            {
                SetAccountUi(session);
                await SyncAccountAsync(true);
            }
            else
            {
                AccountButton.Content = "登录" + _api.Name + "   →";
                HeroLogin.Visibility = Visibility.Visible;
                StatusText.Text = "已切换到" + _api.Name + "，请从菜单登录。";
            }
        }
        catch (Exception error) { NativeFailure("切换音乐源失败", error); }
        PushPlaybackState();
        if (NativePopupMenu.IsOpen) ShowSystemMenu(refresh:true);
    }

    private async Task ChangeQualityAsync(AudioQuality quality)
    {
        if (_api.Quality == quality) return;
        _api.Quality = quality;
        SavePreferences();
        _qualityChangeCts?.Cancel();
        var song = _current;
        if (song == null || song.LocalPath != null || song.Provider != _api.Provider) return;
        var request = CancellationTokenSource.CreateLinkedTokenSource(_accountCts.Token, _lifetime.Token);
        _qualityChangeCts = request;
        int playGeneration = _playGeneration;
        int accountGeneration = _accountGeneration;
        try
        {
            StatusText.Text = "正在获取“" + AudioQualityNames.Name(quality) + "”播放地址…";
            var playback = await _api.GetPlaybackSourceAsync(song, request.Token);
            request.Token.ThrowIfCancellationRequested();
            if (playGeneration != _playGeneration || accountGeneration != _accountGeneration) return;
            double position = _openingSong ? _retryPosition : _audio.Position;
            bool playing = _openingSong ? _openingAutoPlay : _audio.IsPlaying;
            await PlaySongAsync(song, position, playing, resolvedSource: playback);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!request.IsCancellationRequested && playGeneration == _playGeneration && accountGeneration == _accountGeneration)
                NativeFailure("音质切换失败", error);
        }
        finally
        {
            if (ReferenceEquals(_qualityChangeCts, request)) _qualityChangeCts = null;
            request.Dispose();
        }
    }

    private sealed record Preferences(double Volume, bool Taskbar, int Mode, int LyricAlignment = 0, WidgetLayout? Layout = null, MusicProvider Provider = MusicProvider.Qq, AudioQuality QqQuality = AudioQuality.Auto, AudioQuality NeteaseQuality = AudioQuality.Auto, MenuAppearance? Appearance = null, bool TaskbarStyle = true, bool TaskbarStyleMotion = true, SpectrumOptions? Spectrum = null, AudioQuality KugouQuality = AudioQuality.Auto, Dictionary<HotkeyAction, HotkeyBinding?>? Hotkeys = null, bool LuminousLyrics = true, bool LyricRandomLayout = true, double LyricRandomness = .7798874824191278, bool MonetUiColors = true, LyricSwayOptions? WholeSway = null, LyricSwayOptions? LetterSway = null);
}

