using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace FeatherPlayer;

public partial class App : Application
{
    private Mutex? _singleInstance;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--test-webview"))
        {
            Shutdown(await WebLoginSmoke.RunAsync(ArgumentAfter(e.Args, "--test-webview") ?? Path.Combine(Environment.CurrentDirectory, "artifacts", "webview-probe.json"))); return;
        }
        string? isolatedData = ArgumentAfter(e.Args, "--test-data-dir");
        if (isolatedData != null) AppPaths.DiagnosticDirectory = Path.GetFullPath(isolatedData);
        AppDomain.CurrentDomain.UnhandledException += (_, args) => { if(args.ExceptionObject is Exception error) LogFailure(error); };
        TaskScheduler.UnobservedTaskException += (_, args) => { LogFailure(args.Exception); args.SetObserved(); };
        DispatcherUnhandledException += (_, args) =>
        {
            LogFailure(args.Exception);
            MessageBox.Show("操作未能完成，请重试。若问题持续，请重新启动light music。", "light music", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };
        if (e.Args.Contains("--self-test"))
        {
            string output = ArgumentAfter(e.Args, "--self-test") ?? Path.Combine(Environment.CurrentDirectory, "artifacts", "self-test");
            try { Shutdown(await SelfTests.RunAsync(output)); }
            catch (Exception ex)
            {
                Directory.CreateDirectory(output);
                await File.WriteAllTextAsync(Path.Combine(output, "failure.txt"), ex.GetType().Name + ": " + ex.Message);
                Shutdown(1);
            }
            return;
        }
        if (e.Args.Contains("--probe-online"))
        {
            string output = ArgumentAfter(e.Args, "--probe-online") ?? Path.Combine(Environment.CurrentDirectory, "artifacts", "online-probe.json");
            await RunOnlineProbeAsync(output);
            Shutdown();
            return;
        }
        if (e.Args.Contains("--probe-login"))
        {
            string output = ArgumentAfter(e.Args, "--probe-login") ?? Path.Combine(Environment.CurrentDirectory, "artifacts", "login-probe.json");
            var states = new System.Collections.Generic.List<string>();
            try
            {
                using var api = new QQMusicClient(); using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                var qr = await api.CreateQrAsync(ct.Token);
                for (int i = 0; i < 3; i++) { await Task.Delay(2000, ct.Token); states.Add((await api.CheckQrAsync(qr, ct.Token)).State); }
            }
            catch (Exception ex) { states.Add(SafeProbeMessage(ex)); }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(states)); Shutdown(); return;
        }
        _singleInstance = new Mutex(true, "Local\\FeatherPlayer.Desktop.v1", out bool created);
        if (!created) { if(!e.Args.Contains("--startup"))MessageBox.Show("light music已经在运行，可从任务栏或通知区域打开。", "light music"); Shutdown(); return; }
        if(e.Args.Contains("--enable-autostart") && isolatedData==null)
        {
            try { StartupRegistration.SetEnabled(true); }
            catch(Exception ex) { LogFailure(ex); }
        }
        if (isolatedData == null)
        {
            try { StartupRegistration.RefreshEnabledRegistration(); }
            catch (Exception ex) { LogFailure(ex); }
        }
        var window = new MainWindow();
        MainWindow = window;
        SessionEnding+=(_,_)=>window.PrepareForSystemShutdown();
        string? uiSmoke = ArgumentAfter(e.Args, "--ui-smoke");
        if (uiSmoke != null) window.Loaded += async (_, _) => await window.RunUiSmokeAsync(uiSmoke);
        if (e.Args.Contains("--ui-check")) window.EnableTaskbarForCheck();
        window.Show();
        if (e.Args.Contains("--official-login")) _ = Dispatcher.BeginInvoke((Action)window.OpenOfficialLoginForUser);
        if (e.Args.Contains("--open-menu")) _ = Dispatcher.BeginInvoke((Action)window.OpenMenuForCheck);
        string? diagnosticPath = ArgumentAfter(e.Args, "--diagnostics");
        if (diagnosticPath != null) _ = window.WriteDiagnosticsAsync(diagnosticPath);
    }

    private static string? ArgumentAfter(string[] args, string flag)
    {
        int at = Array.IndexOf(args, flag);
        return at >= 0 && at + 1 < args.Length && !args[at + 1].StartsWith("--", StringComparison.Ordinal) ? args[at + 1] : null;
    }

    internal static void LogFailure(Exception error)
    {
        // Retain diagnostic types/stacks only, never exception messages or account tokens.
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            string path=Path.Combine(AppPaths.DataDirectory,"errors.log");
            if(File.Exists(path) && new FileInfo(path).Length>262144)File.Move(path,path+".previous",true);
            File.AppendAllText(path,$"{DateTimeOffset.Now:O} {error.GetType().FullName}\n{error.StackTrace}\n");
        }
        catch { }
    }

    private static async Task RunOnlineProbeAsync(string output)
    {
        using var api = new QQMusicClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(75));
        var results = new System.Collections.Generic.Dictionary<string, object>();
        async Task Probe(string name, Func<Task<object>> action)
        {
            var start = System.Diagnostics.Stopwatch.StartNew();
            try { object result = await action(); results[name] = new { ok = true, elapsedMs = start.ElapsedMilliseconds, result }; }
            catch (Exception ex) { results[name] = new { ok = false, type = ex.GetType().Name, message = SafeProbeMessage(ex) }; }
        }
        await Probe("qr", async () => { var qr = await api.CreateQrAsync(timeout.Token); return new { bytes = qr.Image.Length, hasSignature = !string.IsNullOrEmpty(qr.Signature) }; });
        System.Collections.Generic.IReadOnlyList<Song> songs = Array.Empty<Song>();
        await Probe("search", async () => { songs = await api.SearchAsync("周杰伦 稻香", timeout.Token); return new { count = songs.Count }; });
        Song track = songs.FirstOrDefault() ?? new Song("003rJSwm3TechU", "probe", "", "", 0);
        await Probe("lyrics", async () => { var lyrics = await api.GetLyricsAsync(track, timeout.Token); return new { lines = lyrics.Count }; });
        await Probe("playback", async () => { var url = await api.GetPlaybackUrlAsync(track, timeout.Token); return new { hasUrl = Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https" }; });
        results["accountVerification"] = "not performed: requires the user's QR authorization";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string SafeProbeMessage(Exception ex) => ex is OperationCanceledException ? "request cancelled or timed out" : ex is System.Net.Http.HttpRequestException ? "network request failed" : ex.Message.Replace("\r", " ").Replace("\n", " ");
    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
