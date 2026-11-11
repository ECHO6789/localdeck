using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace LocalDeck.App.Windows;

public partial class MacroWindow : Window
{
    private readonly Func<string, Task<string>> _dispatch;

    public MacroWindow(Func<string, Task<string>> dispatch)
    {
        InitializeComponent();
        _dispatch = dispatch;
        Loaded += OnLoaded;
        Closed += (_, _) => WebView.Dispose();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await WebView.EnsureCoreWebView2Async();
        var core = WebView.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.NewWindowRequested += (_, args) => args.Handled = true;
        core.WebMessageReceived += async (_, args) =>
        {
            var response = await _dispatch(args.WebMessageAsJson);
            core.PostWebMessageAsJson(response);
        };

        var webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        core.SetVirtualHostNameToFolderMapping(
            "localdeck.app", webRoot, CoreWebView2HostResourceAccessKind.DenyCors);
        core.Navigate("https://localdeck.app/index.html?view=macro");
    }
}
