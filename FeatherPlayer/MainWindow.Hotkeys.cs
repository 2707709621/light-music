using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace FeatherPlayer;

public partial class MainWindow
{
    private GlobalHotkeys? _hotkeys;
    private Dictionary<HotkeyAction, HotkeyBinding?>? _savedHotkeys;
    private bool _favoriteHotkeyBusy;
    private double _unmutedVolume = .65;

    private async void HandleHotkey(HotkeyAction action)
    {
        if (_quitting || _loginOpen) return;
        try
        {
            switch (action)
            {
                case HotkeyAction.PlayPause: await ToggleAsync(); break;
                case HotkeyAction.Next: await AdvanceAsync(1); break;
                case HotkeyAction.Previous: await AdvanceAsync(-1); break;
                case HotkeyAction.Favorite: await ToggleFavoriteFromHotkeyAsync(); break;
                case HotkeyAction.VolumeUp: VolumeSlider.Value = Math.Min(1, VolumeSlider.Value + .05); SavePreferences(); break;
                case HotkeyAction.VolumeDown: VolumeSlider.Value = Math.Max(0, VolumeSlider.Value - .05); SavePreferences(); break;
                case HotkeyAction.Mute:
                    if (VolumeSlider.Value > 0) { _unmutedVolume = VolumeSlider.Value; VolumeSlider.Value = 0; }
                    else VolumeSlider.Value = _unmutedVolume;
                    SavePreferences();
                    break;
                case HotkeyAction.ShowMenu: ShowSystemMenu(); break;
                case HotkeyAction.PlaybackMode: _mode = (_mode + 1) % 3; SavePreferences(); NativePopupMenu.Refresh(); break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { ReportError("快捷键操作未完成", error); }
    }

    private async Task ToggleFavoriteFromHotkeyAsync()
    {
        if (_favoriteHotkeyBusy || _playlistMutationBusy || !_hasSession || _current is not { LocalPath: null } song) return;
        _favoriteHotkeyBusy = true;
        int generation = _accountGeneration;
        try
        {
            var entry = await FavoriteEntryAsync(song, _accountCts.Token);
            if (generation == _accountGeneration && ReferenceEquals(_current, song) && entry.Enabled) entry.Action?.Invoke();
        }
        finally { _favoriteHotkeyBusy = false; }
    }
}
