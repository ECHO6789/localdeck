using System.Diagnostics;
using System.Net.Sockets;
using Microsoft.Win32;

namespace LocalDeck.App.Services;

public static class ProxyWatchdogService
{
    private const string InternetSettings =
        @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    public static Process? Start(int parentProcessId, int port)
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            return null;
        }

        var guardExecutable = executable;
        try
        {
            var guardPath = Path.Combine(
                AppContext.BaseDirectory, "LocalDeckProxyGuard.exe");
            if (!File.Exists(guardPath))
            {
                File.Copy(executable, guardPath);
            }
            guardExecutable = guardPath;
        }
        catch
        {
            // A read-only portable folder can still use the main executable.
        }

        return Process.Start(new ProcessStartInfo(guardExecutable)
        {
            Arguments = $"--proxy-watchdog {parentProcessId} {port}",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });
    }

    public static void Run(int parentProcessId, int port)
    {
        using var mutex = new Mutex(
            true, $@"Local\LocalDeck.ProxyWatchdog.{parentProcessId}", out var ownsMutex);
        if (!ownsMutex)
        {
            return;
        }

        var lastListenerCheck = DateTime.MinValue;
        var listenerAlive = true;
        try
        {
            while (!IsParentRunning(parentProcessId))
            {
                return;
            }

            while (IsParentRunning(parentProcessId))
            {
                if (!SystemProxyPointsTo(port))
                {
                    return;
                }

                if (DateTime.UtcNow - lastListenerCheck > TimeSpan.FromSeconds(2))
                {
                    listenerAlive = IsListenerAlive(port);
                    lastListenerCheck = DateTime.UtcNow;
                    if (!listenerAlive)
                    {
                        AppPaths.Log("Proxy watchdog detected a stopped listener and disabled system proxy.");
                        return;
                    }
                }
                Thread.Sleep(500);
            }
        }
        catch (Exception ex)
        {
            AppPaths.Log($"Proxy watchdog stopped: {ex.Message}");
        }
        finally
        {
            DisableIfOwned(port);
        }
    }

    private static bool IsParentRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsListenerAlive(int port)
    {
        try
        {
            using var client = new TcpClient();
            var task = client.ConnectAsync("127.0.0.1", port);
            return task.Wait(TimeSpan.FromMilliseconds(350)) && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static bool SystemProxyPointsTo(int port)
    {
        using var key = Registry.CurrentUser.OpenSubKey(InternetSettings);
        return Convert.ToInt32(key?.GetValue("ProxyEnable") ?? 0) == 1 &&
               key?.GetValue("ProxyServer")?.ToString() == $"127.0.0.1:{port}";
    }

    private static void DisableIfOwned(int port)
    {
        if (!SystemProxyPointsTo(port))
        {
            return;
        }
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(InternetSettings, writable: true);
            if (key is null)
            {
                return;
            }
            key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
            InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0);
            InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0);
        }
        catch (Exception ex)
        {
            AppPaths.Log($"Proxy watchdog could not disable system proxy: {ex.Message}");
        }
    }

    [System.Runtime.InteropServices.DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(
        IntPtr internet, int option, IntPtr buffer, int bufferLength);
}
