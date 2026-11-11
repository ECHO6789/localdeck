using System.Threading;
using System.Windows;
using LocalDeck.App.Services;

namespace LocalDeck.App;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private bool _ownsSingleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        AppPaths.EnsureCreated();
        if (e.Args.Contains("--proxy-watchdog", StringComparer.OrdinalIgnoreCase))
        {
            var index = Array.FindIndex(
                e.Args, arg => arg.Equals("--proxy-watchdog", StringComparison.OrdinalIgnoreCase));
            var parentId = index >= 0 &&
                           index + 2 < e.Args.Length &&
                           int.TryParse(e.Args[index + 1], out var pid)
                ? pid
                : 0;
            var port = index >= 0 &&
                       index + 2 < e.Args.Length &&
                       int.TryParse(e.Args[index + 2], out var parsedPort)
                ? parsedPort
                : 0;
            if (parentId > 0 && port is > 0 and <= 65535)
            {
                ProxyWatchdogService.Run(parentId, port);
            }
            Shutdown();
            return;
        }

        if (e.Args.Contains("--fps-helper", StringComparer.OrdinalIgnoreCase))
        {
            var parentIndex = Array.FindIndex(
                e.Args, arg => arg.Equals("--fps-helper", StringComparison.OrdinalIgnoreCase));
            var parentId = parentIndex >= 0 &&
                           parentIndex + 1 < e.Args.Length &&
                           int.TryParse(e.Args[parentIndex + 1], out var parsed)
                ? parsed
                : 0;
            FpsMonitorService.RunHelper(parentId);
            Shutdown();
            return;
        }

        if (e.Args.Contains("--gpu-helper", StringComparer.OrdinalIgnoreCase))
        {
            GpuControlService.RunHelper(e.Args);
            Shutdown();
            return;
        }

        _singleInstance = new Mutex(true, @"Local\LocalDeck.SingleInstance", out var created);
        _ownsSingleInstance = created;
        if (!created)
        {
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            AppPaths.Log($"Unhandled UI exception: {args.Exception}");
            args.Handled = true;
            MessageBox.Show(
                $"LocalDeck 遇到错误：{args.Exception.Message}\n\n详细信息已写入日志。",
                "LocalDeck",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        };

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsSingleInstance)
        {
            _singleInstance?.ReleaseMutex();
        }
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
