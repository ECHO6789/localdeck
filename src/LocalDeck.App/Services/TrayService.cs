using System.Drawing;
using System.Windows;
using System.Windows.Forms;

namespace LocalDeck.App.Services;

public sealed class TrayService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _icon;
    private readonly Window _window;
    private readonly Action _openClipboard;
    private readonly Action _captureScreenshot;
    private readonly Action _toggleOverlay;

    public TrayService(
        Window window,
        Action openClipboard,
        Action captureScreenshot,
        Action toggleProxy,
        Action toggleOverlay,
        Action exitApplication)
    {
        _window = window;
        _openClipboard = openClipboard;
        _captureScreenshot = captureScreenshot;
        _toggleOverlay = toggleOverlay;

        var menu = new ContextMenuStrip();
        menu.Items.Add("打开 LocalDeck", null, (_, _) => ShowWindow());
        menu.Items.Add("剪贴板历史", null, (_, _) => _openClipboard());
        menu.Items.Add("截图并贴图", null, (_, _) => _captureScreenshot());
        menu.Items.Add("切换内置代理", null, (_, _) => toggleProxy());
        menu.Items.Add("性能悬浮窗", null, (_, _) => _toggleOverlay());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => exitApplication());

        _icon = Environment.ProcessPath is { } processPath
            ? Icon.ExtractAssociatedIcon(processPath) ?? (Icon)SystemIcons.Application.Clone()
            : (Icon)SystemIcons.Application.Clone();
        _notifyIcon = new NotifyIcon
        {
            Icon = _icon,
            Text = "LocalDeck · 本地控制台",
            Visible = true,
            ContextMenuStrip = menu
        };
        _notifyIcon.DoubleClick += (_, _) => ShowWindow();
    }

    public void ShowWindow()
    {
        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _icon.Dispose();
    }
}
