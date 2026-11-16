using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Interop;
using System.Windows;
using System.Windows.Controls;
using Forms = System.Windows.Forms;

namespace FeatherPlayer;

public partial class MainWindow
{
    private bool _nativeMenuOpen;
    private HashSet<string>? _likedMids;
    private bool _playlistMutationBusy;
    private CancellationTokenSource? _guessPlaybackCts;
    private IntPtr NativeOwner => _widget?.MenuOwner is { } handle && handle != IntPtr.Zero ? handle : new WindowInteropHelper(this).Handle;

    private void ShowSystemMenu() => ShowSystemMenu(refresh:false);

    private void ShowSystemMenu(bool refresh)
    {
        if (_nativeMenuOpen) return;
        var menus = new List<NativeMenuEntry>
        {
            new(_current?.Title ?? "light music", Content:()=>new MenuNowPlaying(()=>_current,()=>_coverImage),
                LoadChildren:ct=>_current == null ? Task.FromResult<IReadOnlyList<NativeMenuEntry>>(new[] { new NativeMenuEntry("尚未播放歌曲",Enabled:false) }) : SongActionsAsync(_current,_queue.ToArray(),ct),
                ReadText:()=>_current?.Title ?? "light music"),
            new("播放进度",Content:()=>new MenuSeekBar(new MenuSeekSource(()=>new PlaybackState(_current,_audio.IsPlaying,"",_audio.Position,_audio.Duration),seconds=>{_audio.Seek(seconds);UpdatePosition();})), ContentHeight:28),
            new("播放控制", Content: CreatePlaybackButtons, ContentHeight:52)
        };
        menus.Add(new("收藏",Enabled:false,Refresh:CurrentFavoriteAsync,Glyph:"star"));
        menus.Add(new("添加至歌单",LoadChildren:ct=>_current is { LocalPath:null } song && _hasSession
            ? SongPlaylistMenu(song,_accountGeneration).LoadChildren!(ct)
            : Task.FromResult<IReadOnlyList<NativeMenuEntry>>(new[] { new NativeMenuEntry("请先选择在线歌曲并登录",Enabled:false) }),
            ReadEnabled:()=>_current is { LocalPath:null } && _hasSession, Glyph:"addplaylist"));
        menus.Add(new("-"));
        var playlists = (PlaylistList.ItemsSource as IEnumerable<Playlist>)?.ToArray() ?? Array.Empty<Playlist>();
        var liked = playlists.FirstOrDefault(p => p.IsLiked) ?? LikedPlaylist;
        menus.Add(_hasSession ? PlaylistMenu(liked) with { Text="我喜欢", Glyph="heart" } : new("我喜欢", Children: new[] { new NativeMenuEntry("登录" + _api.Name + "后读取", () => _ = LoginAsync()) },Glyph:"heart"));
        menus.Add(_hasSession ? new("我的歌单", LoadChildren: LoadNativePlaylistsAsync,Glyph:"playlist") : new("我的歌单", Children: new[] { new NativeMenuEntry("请先登录", () => _ = LoginAsync()) },Glyph:"playlist"));
        menus.Add(RecommendationMenu(_api.Provider == MusicProvider.Qq ? "每日30首" : "每日推荐",true));
        menus.Add(RecommendationMenu(_api.Provider == MusicProvider.Netease ? "私人 FM" : "猜你想听",false));
        menus.Add(new("本地音乐", Children: SongMenu(_localSongs.ToArray()).Concat(new[] { new NativeMenuEntry("-"), new NativeMenuEntry("导入音乐文件…", () => ImportClick(this, new System.Windows.RoutedEventArgs())) }).ToArray()));
        menus.Add(new("搜索歌曲…", Search: SearchNativeAsync));
        menus.Add(new("播放模式", Children: new[] { "列表循环", "单曲循环", "随机播放" }.Select((name, mode) => new NativeMenuEntry(name, () => { _mode = mode; SavePreferences(); }, ReadChecked:()=>_mode == mode,Glyph:"none")).ToArray(),Glyph:"repeat"));
        var qualities = new List<NativeMenuEntry>();
        foreach (var quality in Enum.GetValues<AudioQuality>())
        {
            if (quality == AudioQuality.HiRes && _api.Provider == MusicProvider.Qq) continue;
            string label = quality switch
            {
                AudioQuality.Standard => "标准音质",
                AudioQuality.Lossless => "无损音质",
                _ => AudioQualityNames.Name(quality)
            };
            qualities.Add(new(label, () => _ = ChangeQualityAsync(quality), ReadChecked:()=>_api.Quality == quality,Glyph:"none"));
        }
        menus.Add(new("音质", Children: qualities, Glyph:"quality"));
        var appearance = new List<NativeMenuEntry>();
        appearance.Add(new("图标与文字跟随莫奈取色", () =>
        {
            SystemTheme.TintUi = !SystemTheme.TintUi;
            SavePreferences();
        }, ReadChecked:()=>SystemTheme.TintUi, Glyph:"sparkles"));
        appearance.Add(new("材质与光效", Children:new[] { new NativeMenuEntry("外观设置", Content:()=>new MenuAppearancePanel(SavePreferences)) }, Glyph:"settings"));
        appearance.Add(new("任务栏上方频谱",Children:new NativeMenuEntry[]
        {
            new("显示音频频谱条",() =>
            {
                if (_spectrum == null) return;
                _spectrum.Configure(_spectrum.Options with { Enabled = !_spectrum.Options.Enabled });
                SavePreferences();
            },ReadChecked:()=>_spectrum?.Options.Enabled == true,Glyph:"quality"),
            new("沿任务栏居中",() =>
            {
                if (_spectrum == null) return;
                _spectrum.Configure(_spectrum.Options with { Centered = true,Offset = 0 });
                SavePreferences();
            },ReadChecked:()=>_spectrum?.Options.Centered == true,Glyph:"layout"),
            new("拖动定位与尺寸…",()=>_spectrum?.EditPosition(SavePreferences),Glyph:"layout"),
            new("-"),
            new("频谱状态",Enabled:false,ReadText:()=>_spectrum?.Status ?? "频谱尚未初始化",Glyph:"none")
        },Glyph:"quality"));
        appearance.Add(new("Windows 任务栏外观", Children:new NativeMenuEntry[]
        {
            new("与菜单样式同步",() =>
            {
                if (_taskbarAppearance == null) return;
                _taskbarAppearance.Configure(!_taskbarAppearance.Enabled,_taskbarAppearance.Motion);
                SavePreferences();
            },ReadChecked:()=>_taskbarAppearance?.Enabled == true,Glyph:"layout"),
            new("流动光效",() =>
            {
                if (_taskbarAppearance == null) return;
                _taskbarAppearance.Configure(_taskbarAppearance.Enabled,!_taskbarAppearance.Motion);
                SavePreferences();
            },ReadChecked:()=>_taskbarAppearance?.Motion == true,ReadEnabled:()=>_taskbarAppearance?.Enabled == true,Glyph:"sparkles"),
            new("恢复 Windows 默认外观",() =>
            {
                _taskbarAppearance?.Configure(false,_taskbarAppearance.Motion);
                SavePreferences();
            },ReadEnabled:()=>_taskbarAppearance?.Enabled == true,Glyph:"refresh"),
            new("-"),
            new("任务栏状态",Enabled:false,ReadText:()=>_taskbarAppearance?.Status ?? "任务栏外观尚未初始化",Glyph:"none")
        },Glyph:"layout"));
        var widgetAppearance = new List<NativeMenuEntry>();
        widgetAppearance.Add(new("流光歌词（单行）", () =>
        {
            LyricStyleSettings.Luminous = !LyricStyleSettings.Luminous;
            SavePreferences();
        }, ReadChecked:()=>LyricStyleSettings.Luminous, Glyph:"sparkles"));
        widgetAppearance.Add(new("流光随机排布", Children:new[]
        {
            new NativeMenuEntry("随机排布参数", Content:()=>new LyricRandomnessPanel(SavePreferences), ContentHeight:145)
        }, ReadEnabled:()=>LyricStyleSettings.Luminous, Glyph:"layout"));
        widgetAppearance.Add(new("流光晃动", Children: new[]
        {
            new NativeMenuEntry("整句晃动", Children: new[] { new NativeMenuEntry("整句晃动参数", Content:()=>new LyricSwayPanel(false, SavePreferences), ContentHeight:205) }),
            new NativeMenuEntry("逐字晃动", Children: new[] { new NativeMenuEntry("逐字晃动参数", Content:()=>new LyricSwayPanel(true, SavePreferences), ContentHeight:205) })
        }, ReadEnabled:()=>LyricStyleSettings.Luminous, Glyph:"sparkles"));
        widgetAppearance.Add(new("任务栏显示封面", () =>
        {
            if (_widget == null) return;
            _widget.ApplyLayout(_widget.Layout with { ShowCover = !_widget.Layout.ShowCover });
            SavePreferences();
        }, ReadChecked:()=>_widget?.Layout.ShowCover == true));
        widgetAppearance.Add(new("歌词宽度与按钮位置…", () => { if (_widget != null) new WidgetLayoutWindow(_widget, SavePreferences).ShowDialog(); }));
        widgetAppearance.Add(new("组件对齐", Children: new[] { "空位靠左", "空位居中", "空位靠右" }.Select((label, alignment) => new NativeMenuEntry(label, () => { if (_widget != null) { _widget.Alignment = alignment; _widget.ApplyLayout(_widget.Layout with { Left = -1 }); } SavePreferences(); }, Checked: (_widget?.Alignment ?? 1) == alignment)).ToArray()));
        appearance.Insert(1, new("歌词与播放组件", Children:widgetAppearance, Glyph:"layout"));
        var settings = new List<NativeMenuEntry>
        {
            new("外观", Children:appearance, Glyph:"sparkles"),
            new("快捷键设置…", () =>
            {
                if (_hotkeys != null) new HotkeySettingsWindow(_hotkeys, SavePreferences).ShowDialog();
            }, Glyph:"settings")
        };
        menus.Add(new("音量", Children: new[] { 0, 25, 50, 75, 100 }.Select(volume => new NativeMenuEntry(volume == 0 ? "静音" : volume + "%", () => { VolumeSlider.Value = volume / 100d; SavePreferences(); }, ReadChecked:()=>Math.Abs(_audio.Volume * 100 - volume) < 1)).ToArray()));
        menus.Add(new("-"));
        settings.Add(new("开机自启动",()=>
        {
            try { StartupRegistration.SetEnabled(!StartupRegistration.IsEnabled); }
            catch(Exception ex) { NativeFailure("无法更改自启动设置",ex); }
        },ReadChecked:()=>StartupRegistration.IsEnabled));
        settings.Add(new("播放错误详情",()=>
        {
            string path=System.IO.Path.Combine(AppPaths.DataDirectory,"playback-error.json");
            string details=System.IO.File.Exists(path)?System.IO.File.ReadAllText(path):"尚无播放错误记录。";
            Forms.MessageBox.Show(details+"\n\n记录位置："+path,"light music · 播放诊断");
        }));
        menus.Add(new("音乐源 · " + _api.Name, Children: new[]
        {
            new NativeMenuEntry("QQ 音乐", () => _ = SwitchProviderAsync(MusicProvider.Qq), ReadChecked:()=>_api.Provider == MusicProvider.Qq, ReadEnabled:()=>!_loginOpen),
            new NativeMenuEntry("网易云音乐", () => _ = SwitchProviderAsync(MusicProvider.Netease), ReadChecked:()=>_api.Provider == MusicProvider.Netease, ReadEnabled:()=>!_loginOpen),
            new NativeMenuEntry("酷狗音乐", () => _ = SwitchProviderAsync(MusicProvider.Kugou), ReadChecked:()=>_api.Provider == MusicProvider.Kugou, ReadEnabled:()=>!_loginOpen)
        }, ReadText:()=>"音乐源 · " + _api.Name));
        var account = new List<NativeMenuEntry>
        {
            new((_hasSession ? "切换 / 重新登录" : "登录") + _api.Name + "…", () => _ = LoginAsync())
        };
        if (_hasSession) account.Add(new("退出账号", Logout));
        menus.Add(new("账号", Children: account, Glyph:"user"));
        menus.Add(new("设置", Children:settings, Glyph:"settings"));
        menus.Add(new("退出 light music", Quit));
        _nativeMenuOpen = true;
        try
        {
            if (refresh && NativePopupMenu.IsOpen) NativePopupMenu.ReplaceEntries(menus);
            else NativePopupMenu.Show(NativeOwner, menus);
        }
        finally { _nativeMenuOpen = false; }
    }

    private FrameworkElement CreatePlaybackButtons()
    {
        var panel = new Grid { Margin = new Thickness(12,4,12,8), Height = 40 };
        var actions = new (string Glyph, string Label, Action Run)[]
        {
            ("previous", "上一首", ()=>_=AdvanceAsync(-1)),
            (_audio.IsPlaying ? "pause" : "play", _audio.IsPlaying ? "暂停" : "播放 / 继续", ()=>_=ToggleAsync()),
            ("next", "下一首", ()=>_=AdvanceAsync(1))
        };
        for (int index = 0; index < actions.Length; index++)
        {
            panel.ColumnDefinitions.Add(new ColumnDefinition());
            var action = actions[index];
            var glyph = LineIcons.Create(action.Glyph, 22, 2.6);
            if (glyph is Viewbox box && box.Child is System.Windows.Shapes.Path path)
                path.SetResourceReference(System.Windows.Shapes.Path.StrokeProperty, "MenuInk");
            var button = new Button { Content = glyph, Width = 48, Height = 36, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            System.Windows.Automation.AutomationProperties.SetName(button, action.Label);
            string lastGlyph = action.Glyph;
            button.SetResourceReference(FrameworkElement.StyleProperty, "LightTransportButton");
            button.Click += (_, _) => { action.Run(); NativePopupMenu.Refresh(); };
            void RefreshButton()
            {
                bool playing = _audio.IsPlaying;
                string kind = action.Glyph is "play" or "pause" ? playing ? "pause" : "play" : action.Glyph;
                if (lastGlyph == kind) return;
                lastGlyph = kind;
                var icon = LineIcons.Create(kind,22,2.6);
                if (icon is Viewbox view && view.Child is System.Windows.Shapes.Path stroke)
                    stroke.SetResourceReference(System.Windows.Shapes.Path.StrokeProperty,"MenuInk");
                button.Content = icon;
                if (action.Glyph is "play" or "pause") System.Windows.Automation.AutomationProperties.SetName(button, playing ? "暂停" : "播放 / 继续");
            }
            button.Loaded += (_, _) => NativePopupMenu.Updated += RefreshButton;
            button.Unloaded += (_, _) => NativePopupMenu.Updated -= RefreshButton;
            Grid.SetColumn(button, index); panel.Children.Add(button);
        }
        return panel;
    }

    private async Task<NativeMenuEntry> CurrentFavoriteAsync(CancellationToken ct)
    {
        if (_current is not { LocalPath:null } song) return new("收藏",Enabled:false,Glyph:"star");
        if (!_hasSession) return new("收藏（需登录）",()=>_=LoginAsync(),Glyph:"star");
        return (await FavoriteEntryAsync(song,ct)) with { Glyph="star" };
    }

    private NativeMenuEntry PlaylistMenu(Playlist playlist) => new(playlist.Name, LoadChildren: async ct =>
    {
        int generation = _accountGeneration;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _accountCts.Token);
        var songs = await CachedPlaylistAsync(playlist, linked.Token);
        linked.Token.ThrowIfCancellationRequested();
        if (generation != _accountGeneration) throw new OperationCanceledException();
        return SongMenu(songs, playlist);
    }, Glyph:playlist.IsLiked ? "heart" : "playlist", DataKey:PlaylistCacheKey(playlist));

    private async Task<IReadOnlyList<NativeMenuEntry>> LoadNativePlaylistsAsync(CancellationToken ct)
    {
        int generation = _accountGeneration;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _accountCts.Token);
        var lists = await CachedPlaylistsAsync(linked.Token);
        linked.Token.ThrowIfCancellationRequested();
        if (generation != _accountGeneration) throw new OperationCanceledException();
        PlaylistList.ItemsSource = lists;
        return lists.Where(p => !p.IsLiked).Select(PlaylistMenu).DefaultIfEmpty(new NativeMenuEntry("暂无歌单", Enabled: false)).ToArray();
    }

    private IReadOnlyList<NativeMenuEntry> SongMenu(IReadOnlyList<Song> songs, Playlist? playlist = null)
    {
        if (songs.Count == 0) return new[] { new NativeMenuEntry("暂无歌曲", Enabled: false) };
        var queue=songs as Song[] ?? songs.ToArray();
        NativeMenuEntry Entry(Song song) => new(song.Title, () => { _queue = queue; _ = PlaySongAsync(song); }, ReadChecked:()=>_current?.Mid == song.Mid,
            LoadChildren:ct=>SongActionsAsync(song,queue,ct,playlist));
        return songs.Select(Entry).ToArray();
    }

    private NativeMenuEntry RecommendationMenu(string title,bool daily)
    {
        string glyph = daily ? "calendar" : "sparkles";
        if(!_hasSession)return new(title,Children:new[]{new NativeMenuEntry("登录后读取个人推荐",()=>_=LoginAsync())},Glyph:glyph);
        return new(title,Action:daily?null:()=>_=PlayGuessFromStartAsync(),LoadChildren:async ct=>
        {
            int generation=_accountGeneration;
            using var linked=CancellationTokenSource.CreateLinkedTokenSource(ct,_accountCts.Token);
            var songs=await CachedRecommendationAsync(daily,linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            if(generation!=_accountGeneration)throw new OperationCanceledException();
            return SongMenu(songs);
        },Glyph:glyph);
    }

    private async Task PlayGuessFromStartAsync()
    {
        _guessPlaybackCts?.Cancel();
        var source=CancellationTokenSource.CreateLinkedTokenSource(_accountCts.Token,_lifetime.Token);
        _guessPlaybackCts=source;
        var token=source.Token;
        int accountGeneration=_accountGeneration;
        int playGeneration=_playGeneration;
        try
        {
            StatusText.Text=_api.Provider == MusicProvider.Netease ? "正在读取私人 FM…" : "正在读取猜你想听…";
            var songs=await CachedRecommendationAsync(false,token);
            token.ThrowIfCancellationRequested();
            if(accountGeneration!=_accountGeneration || playGeneration!=_playGeneration || !ReferenceEquals(_guessPlaybackCts,source))return;
            if(songs.Count==0)throw new InvalidOperationException(_api.Name + "暂未返回推荐歌曲，请稍后再试。");
            _queue=songs.ToArray();
            await PlaySongAsync(_queue[0]);
        }
        catch(OperationCanceledException) { }
        catch(Exception error)
        {
            if(accountGeneration==_accountGeneration && playGeneration==_playGeneration && ReferenceEquals(_guessPlaybackCts,source))
                NativeFailure("读取推荐歌曲失败",error);
        }
        finally
        {
            if(ReferenceEquals(_guessPlaybackCts,source))_guessPlaybackCts=null;
            source.Dispose();
        }
    }

    private Task<IReadOnlyList<NativeMenuEntry>> SongActionsAsync(Song song,Song[] queue,CancellationToken ct, Playlist? playlist = null)
    {
        ct.ThrowIfCancellationRequested();
        var actions = new List<NativeMenuEntry>
        {
            new("歌手 · " + (string.IsNullOrWhiteSpace(song.Artist) ? "未知歌手" : song.Artist), Enabled:false, Glyph:"user"),
            new("专辑 · " + (string.IsNullOrWhiteSpace(song.Album) ? "未知专辑" : song.Album), Enabled:false, Glyph:"folder"),
            new("时长 · " + song.DurationText, Enabled:false),
            new(song.LocalPath != null ? "本地音乐" : MusicServiceClient.ProviderName(song.Provider), Enabled:false),
            new("-"),
            new("播放",()=>{_queue=queue;_=PlaySongAsync(song);}, Glyph:"play")
        };
        if (song.LocalPath == null)
        {
            if (_hasSession)
            {
                actions.Add(new("收藏状态读取中…", Enabled:false, Refresh:token=>FavoriteEntryAsync(song,token), Glyph:"heart"));
                actions.Add(SongPlaylistMenu(song,_accountGeneration));
                if (playlist is { Writable:true } || playlist is { IsLiked:true })
                    actions.Add(new("从歌单中移除",()=>_=ChangeSongPlaylistAsync(song,playlist!,false), Glyph:"remove"));
            }
            else actions.Add(new("登录后收藏或添加歌单",()=>_=LoginAsync()));
        }
        return Task.FromResult<IReadOnlyList<NativeMenuEntry>>(actions);
    }

    private async Task<NativeMenuEntry> FavoriteEntryAsync(Song song, CancellationToken ct)
    {
        int generation=_accountGeneration;
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(ct,_accountCts.Token);
        var liked=LikedPlaylist;
            var songs=await CachedPlaylistAsync(liked,linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            if(generation!=_accountGeneration)throw new OperationCanceledException();
            _likedMids=songs.Select(s=>s.Mid).ToHashSet(StringComparer.Ordinal);
        bool isLiked=_likedMids.Contains(song.Mid);
        return new(isLiked?"取消收藏":"收藏",()=>_=ChangeSongPlaylistAsync(song,liked,!isLiked), Glyph:"heart");
    }

    private NativeMenuEntry SongPlaylistMenu(Song song,int generation) => new("添加至歌单",LoadChildren:async token=>
        {
            using var request=CancellationTokenSource.CreateLinkedTokenSource(token,_accountCts.Token);
            var playlists=await CachedPlaylistsAsync(request.Token);
            request.Token.ThrowIfCancellationRequested();
            if(generation!=_accountGeneration)throw new OperationCanceledException();
            return playlists.Where(p=>p.Writable && !p.IsLiked && (p.Provider != MusicProvider.Qq || p.DirectoryId>0))
                .Select(p=>new NativeMenuEntry(p.Name,()=>_=ChangeSongPlaylistAsync(song,p,true)))
                .DefaultIfEmpty(new NativeMenuEntry("暂无可添加的自建歌单",Enabled:false)).ToArray();
        });

    private async Task ChangeSongPlaylistAsync(Song song,Playlist playlist,bool add)
    {
        if(_playlistMutationBusy)return;
        _playlistMutationBusy=true;int generation=_accountGeneration;
        using var request=CancellationTokenSource.CreateLinkedTokenSource(_accountCts.Token,_lifetime.Token);
        string account="";
        try
        {
            account=CacheAccount;
            var actual=await _api.SetSongInPlaylistAsync(song,playlist,add,request.Token);
            if(generation!=_accountGeneration)return;
            _menuDataCache.Set(account,PlaylistCacheKey(playlist),actual,MenuRefreshInterval);
            if(_menuDataCache.Peek<IReadOnlyList<Playlist>>(account,"playlists") is { } lists)
                _menuDataCache.Set<IReadOnlyList<Playlist>>(account,"playlists",lists.Select(p=>(playlist.IsLiked ? p.IsLiked : p.Id==playlist.Id)?p with{Count=actual.Count}:p).ToArray(),MenuRefreshInterval);
            if(playlist.IsLiked && _likedMids!=null){if(add)_likedMids.Add(song.Mid);else _likedMids.Remove(song.Mid);}
            StatusText.Text=add?"已添加至“"+playlist.Name+"”":playlist.IsLiked?"已取消收藏":"已从“"+playlist.Name+"”移除";
            NativePopupMenu.Refresh(reloadData:true);
            NativePopupMenu.ReloadData(PlaylistCacheKey(playlist));
        }
        catch(OperationCanceledException) { }
        catch(Exception error)
        {
            if(generation==_accountGeneration)
            {
                if(account.Length>0)_menuDataCache.Invalidate(account,PlaylistCacheKey(playlist));
                NativeFailure("歌单操作未确认成功，请重新展开查看",error);
            }
        }
        finally{_playlistMutationBusy=false;}
    }

    private async Task<IReadOnlyList<NativeMenuEntry>> SearchNativeAsync(string query, CancellationToken ct)
    {
        int generation = _accountGeneration;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _accountCts.Token, _lifetime.Token);
        var songs = await _api.SearchAsync(query, linked.Token);
        linked.Token.ThrowIfCancellationRequested();
        if (generation != _accountGeneration) throw new OperationCanceledException();
        return SongMenu(songs);
    }
    private void NativeFailure(string title, Exception ex)
    {
        ReportError(title, ex);
        Forms.MessageBox.Show(StatusText.Text, "light music", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Information);
    }
}

