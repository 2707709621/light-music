using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace FeatherPlayer;
internal static class WebLoginSmoke
{
    internal static async Task<int> RunAsync(string output)
    {
        var browser = new WebView2();
        var window = new Window { Title = "light music网页登录组件检查", Width = 600, Height = 400, Opacity = 0, ShowInTaskbar = false, Content = browser };
        object report; int exit = 1;
        try
        {
            window.Show();
            string folder = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, "isolated-webview-probe");
            var env = await CoreWebView2Environment.CreateAsync(null, folder);
            await browser.EnsureCoreWebView2Async(env);
            var ready = new TaskCompletionSource<CoreWebView2NavigationCompletedEventArgs>();
            browser.CoreWebView2.NavigationCompleted += (_, args) => ready.TrySetResult(args);
            browser.CoreWebView2.Navigate("https://y.qq.com/portal/profile.html");
            var navigation = await ready.Task.WaitAsync(TimeSpan.FromSeconds(40));
            report = new { initialized = true, navigationSuccess = navigation.IsSuccess, status = navigation.HttpStatusCode, error = navigation.WebErrorStatus.ToString(), runtime = env.BrowserVersionString, accountLoginTested = false };
            exit = navigation.IsSuccess ? 0 : 1;
        }
        catch (Exception ex) { report = new { initialized = false, errorType = ex.GetType().Name, accountLoginTested = false }; }
        finally { browser.Dispose(); window.Close(); }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report));
        return exit;
    }
}
