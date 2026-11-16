using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Security.Principal;
using System.Threading;

namespace LightSuite;

internal sealed record MusicVisualState(int ProcessId, bool Playing, uint Accent,
    double Opacity, double Glow, int Blur, int CaptureStatus, float Energy, float Bass);

/// <summary>当前 Windows 用户会话内的视觉状态。仅传颜色、外观与频谱强度。</summary>
internal sealed class MusicLinkChannel : IDisposable
{
    private const int Magic = 0x4C4D5631;
    private readonly MemoryMappedFile _mapping;
    private readonly MemoryMappedViewAccessor _view;
    private int _revision;

    private MusicLinkChannel(MemoryMappedFile mapping)
    {
        _mapping = mapping;
        _view = mapping.CreateViewAccessor(0, 128);
    }

    private static readonly string Name = CreateName();

    private static string CreateName()
    {
        using var identity = WindowsIdentity.GetCurrent();
        string name = "Local\\LightSuite.Music.Visual.v1." + identity.User!.Value;
        string? testChannel = Environment.GetEnvironmentVariable("LIGHTSUITE_TEST_CHANNEL");
        return string.IsNullOrWhiteSpace(testChannel) ? name : name + ".Test." + testChannel;
    }

    public static MusicLinkChannel CreatePublisher() => new(MemoryMappedFile.CreateOrOpen(Name, 128));

    public static MusicLinkChannel? OpenReader()
    {
        try { return new(MemoryMappedFile.OpenExisting(Name)); }
        catch (FileNotFoundException) { return null; }
    }

    public bool HasActiveReader => IsRecent(_view.ReadInt64(64), 2000);

    public void RequestAudio(bool enabled) => _view.Write(64, enabled ? Environment.TickCount64 : 0L);

    public void Publish(MusicVisualState state)
    {
        int revision = unchecked(_revision += 2);
        _view.Write(8, revision - 1);
        Thread.MemoryBarrier();
        _view.Write(0, Magic);
        _view.Write(4, 1);
        _view.Write(12, state.ProcessId);
        _view.Write(16, Environment.TickCount64);
        _view.Write(24, state.Playing ? 1 : 0);
        _view.Write(28, state.Accent);
        _view.Write(32, state.Opacity);
        _view.Write(40, state.Glow);
        _view.Write(48, state.Blur);
        _view.Write(52, state.CaptureStatus);
        _view.Write(56, state.Energy);
        _view.Write(60, state.Bass);
        Thread.MemoryBarrier();
        _view.Write(8, revision);
    }

    public MusicVisualState? Read()
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            int before = _view.ReadInt32(8);
            if ((before & 1) != 0) continue;
            Thread.MemoryBarrier();
            if (_view.ReadInt32(0) != Magic || _view.ReadInt32(4) != 1) return null;
            long heartbeat = _view.ReadInt64(16);
            var state = new MusicVisualState(_view.ReadInt32(12), _view.ReadInt32(24) == 1,
                _view.ReadUInt32(28), _view.ReadDouble(32), _view.ReadDouble(40),
                _view.ReadInt32(48), _view.ReadInt32(52), _view.ReadSingle(56), _view.ReadSingle(60));
            Thread.MemoryBarrier();
            if (before != _view.ReadInt32(8)) continue;
            if (!IsRecent(heartbeat, 1800)) return null;
            return state with
            {
                Opacity = Unit(state.Opacity, .65), Glow = Unit(state.Glow, .25),
                Blur = Math.Clamp(state.Blur, 0, 2),
                Energy = (float)Unit(state.Energy, 0), Bass = (float)Unit(state.Bass, 0),
            };
        }
        return null;
    }

    private static double Unit(double value, double fallback) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : fallback;
    private static bool IsRecent(long ticks, int maximumAge)
    {
        long age = Environment.TickCount64 - ticks;
        return ticks > 0 && age >= 0 && age <= maximumAge;
    }

    public void Dispose()
    {
        _view.Dispose();
        _mapping.Dispose();
    }
}
