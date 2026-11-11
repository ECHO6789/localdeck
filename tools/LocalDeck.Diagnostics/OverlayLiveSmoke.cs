using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LocalDeck.App.Models;
using LocalDeck.App.Services;
using LocalDeck.App.Windows;

namespace LocalDeck.Diagnostics;

internal static class OverlayLiveSmoke
{
    public static Task<string> RunAsync()
    {
        var completion = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            _ = dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    completion.TrySetResult(await ExerciseAsync());
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
                finally
                {
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            });
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "LocalDeck overlay live smoke"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static async Task<string> ExerciseAsync()
    {
        var settings = new SettingsService();
        settings.Load();
        var first = new AppSettings
        {
            PerformanceOverlayEnabled = true,
            PerformanceOverlayOpacity = 0.42,
            PerformanceOverlayFpsSource = "foreground",
            PerformanceOverlayMetrics = ["cpuUsage", "gpuUsage", "fps"],
            PerformanceOverlayWidth = 520,
            PerformanceOverlayHeight = 500
        };
        var second = new AppSettings
        {
            PerformanceOverlayEnabled = true,
            PerformanceOverlayOpacity = 0.88,
            PerformanceOverlayFpsSource = "process:chrome",
            PerformanceOverlayMetrics = ["cpuUsage"],
            PerformanceOverlayWidth = 620,
            PerformanceOverlayHeight = 560
        };

        using var metrics = new PerformanceOverlayService(
            new SystemMonitorService(), new VendorControlService(), settings);
        var window = new PerformanceOverlayWindow(
            metrics,
            (_, _) => { },
            (_, _) => { },
            () => { })
        {
            Left = -30000,
            Top = -30000,
            Topmost = false,
            ShowActivated = false
        };

        try
        {
            Apply(settings.Current, first);
            window.ApplyOptions(settings.Current);
            window.Show();
            await Task.Delay(1800);
            var wrap = (WrapPanel)window.FindName("MetricWrap");
            var surface = (Border)window.FindName("OverlaySurface");
            if (wrap.Children.Count != 3 || wrap.Children[0] is not Border firstCard)
            {
                throw new InvalidOperationException(
                    $"Expected three immediate metric cards, found {wrap.Children.Count}.");
            }
            var firstCardPanel = (StackPanel)firstCard.Child;
            var cpuValue = (TextBlock)firstCardPanel.Children[1];
            var cpuColor = ((SolidColorBrush)cpuValue.Foreground).Color;
            var firstAlpha = ((SolidColorBrush)surface.Background).Color.A;

            Apply(settings.Current, second);
            window.ApplyOptions(settings.Current);
            var secondAlpha = ((SolidColorBrush)surface.Background).Color.A;
            if (wrap.Children.Count != 1 || wrap.Children[0] is not Border secondCard)
            {
                throw new InvalidOperationException(
                    $"Metric removal was not immediate; found {wrap.Children.Count} cards.");
            }
            var secondCardPanel = (StackPanel)secondCard.Child;
            var secondCpuColor = ((SolidColorBrush)((TextBlock)secondCardPanel.Children[1]).Foreground).Color;

            if (window.Width != second.PerformanceOverlayWidth ||
                window.Height != second.PerformanceOverlayHeight)
            {
                throw new InvalidOperationException(
                    $"Live dimensions were {window.Width}×{window.Height}, expected 620×560.");
            }
            if (firstAlpha == secondAlpha || secondAlpha <= firstAlpha)
            {
                throw new InvalidOperationException(
                    $"Background alpha did not increase as expected: {firstAlpha} -> {secondAlpha}.");
            }
            if (cpuColor != secondCpuColor || window.Opacity != 1)
            {
                throw new InvalidOperationException(
                    $"Metric color changed or window opacity was applied: {cpuColor} -> {secondCpuColor}, opacity={window.Opacity}.");
            }

            return
                $"Overlay live settings: 3 -> 1 metrics, 520×500 -> 620×560, " +
                $"background alpha {firstAlpha} -> {secondAlpha}, CPU color #{cpuColor.R:X2}{cpuColor.G:X2}{cpuColor.B:X2}, " +
                $"window opacity={window.Opacity:0.##}.";
        }
        finally
        {
            window.Close();
        }
    }

    private static void Apply(AppSettings target, AppSettings source)
    {
        target.PerformanceOverlayEnabled = source.PerformanceOverlayEnabled;
        target.PerformanceOverlayOpacity = source.PerformanceOverlayOpacity;
        target.PerformanceOverlayFpsSource = source.PerformanceOverlayFpsSource;
        target.PerformanceOverlayMetrics = source.PerformanceOverlayMetrics.ToList();
        target.PerformanceOverlayWidth = source.PerformanceOverlayWidth;
        target.PerformanceOverlayHeight = source.PerformanceOverlayHeight;
    }
}
