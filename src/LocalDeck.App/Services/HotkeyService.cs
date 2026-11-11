using System.Runtime.InteropServices;
using LocalDeck.App.Models;

namespace LocalDeck.App.Services;

public sealed class HotkeyService : IDisposable
{
    private readonly Dictionary<int, RegisteredHotkey> _registered = [];
    private IntPtr _window;
    private int _nextId = 1000;

    public IReadOnlyList<HotkeyRegistration> Registrations { get; private set; } = [];

    public void Reload(IntPtr window, AppSettings settings)
    {
        DisposeRegistrations();
        _window = window;
        var definitions = new List<HotkeyDefinition>
        {
            new("clipboard", "剪贴板历史", settings.Hotkeys.Clipboard, "showClipboard", ""),
            new("screenshot", "区域截图", settings.Hotkeys.Screenshot, "screenshot", ""),
            new("screenshot-pin", "截图并贴图", settings.Hotkeys.ScreenshotPin, "screenshotPin", ""),
            new("pin-image", "贴图剪贴板图片", settings.Hotkeys.PinImage, "pinImage", ""),
            new("search", "文件搜索", settings.Hotkeys.Search, "search", ""),
            new("panel", "打开 LocalDeck", settings.Hotkeys.OpenPanel, "showPanel", ""),
            new("proxy-toggle", "切换内置代理", settings.Hotkeys.ProxyToggle, "proxyToggle", ""),
            new("macro-editor", "打开宏编辑器", settings.Hotkeys.MacroEditor, "openMacro", ""),
            new("launcher", "打开快捷启动", settings.Hotkeys.Launcher, "showLauncher", ""),
            new("overlay", "显示性能悬浮窗", settings.Hotkeys.Overlay, "overlayToggle", "")
        };
        definitions.AddRange(settings.Hotkeys.Custom.Select(item =>
            new HotkeyDefinition(
                item.Id, item.Label, item.Combo, item.Action, item.Arguments)));

        var results = new List<HotkeyRegistration>();
        var usedCombos = new HashSet<(uint, uint)>();
        foreach (var definition in definitions.Where(item => !string.IsNullOrWhiteSpace(item.Combo)))
        {
            if (!TryParse(definition.Combo, out var modifiers, out var key, out var parseError))
            {
                results.Add(new HotkeyRegistration(
                    definition.Id, definition.Label, definition.Combo,
                    definition.Action, definition.Arguments, false, parseError));
                continue;
            }

            if (!usedCombos.Add((modifiers, key)))
            {
                results.Add(new HotkeyRegistration(
                    definition.Id, definition.Label, definition.Combo,
                    definition.Action, definition.Arguments, false, "组合键重复"));
                continue;
            }

            var id = _nextId++;
            var ok = RegisterHotKey(window, id, modifiers, key);
            var errorCode = ok ? 0 : Marshal.GetLastWin32Error();
            var error = ok
                ? null
                : errorCode == 1409
                    ? "已被其他程序占用"
                    : new System.ComponentModel.Win32Exception(errorCode).Message;
            if (ok)
            {
                _registered[id] = new RegisteredHotkey(
                    id, definition.Id, definition.Action, definition.Arguments, definition.Label);
            }
            results.Add(new HotkeyRegistration(
                definition.Id, definition.Label, definition.Combo,
                definition.Action, definition.Arguments, ok, error));
        }

        Registrations = results;
    }

    public bool TryGetAction(int id, out RegisteredHotkey hotkey) =>
        _registered.TryGetValue(id, out hotkey!);

    public static bool TryParse(
        string combo,
        out uint modifiers,
        out uint virtualKey,
        out string? error)
    {
        modifiers = 0;
        virtualKey = 0;
        error = null;
        var parts = combo.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            error = "组合键为空";
            return false;
        }

        foreach (var modifier in parts[..^1])
        {
            switch (modifier.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= 0x0002;
                    break;
                case "alt":
                    modifiers |= 0x0001;
                    break;
                case "shift":
                    modifiers |= 0x0004;
                    break;
                case "win":
                case "windows":
                    modifiers |= 0x0008;
                    break;
                default:
                    error = $"未知修饰键：{modifier}";
                    return false;
            }
        }

        var keyName = parts[^1].ToUpperInvariant();
        if (keyName.Length == 1 && char.IsAsciiLetterOrDigit(keyName[0]))
        {
            virtualKey = keyName[0];
            return true;
        }

        var symbolKeys = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
        {
            [","] = 0xBC, ["."] = 0xBE, ["/"] = 0xBF, [";"] = 0xBA,
            ["'"] = 0xDE, ["["] = 0xDB, ["]"] = 0xDD, ["\\"] = 0xDC,
            ["-"] = 0xBD, ["="] = 0xBB, ["`"] = 0xC0
        };
        if (symbolKeys.TryGetValue(keyName, out virtualKey))
        {
            return true;
        }

        if (keyName.StartsWith('F') &&
            int.TryParse(keyName.AsSpan(1), out var function) &&
            function is >= 1 and <= 24)
        {
            virtualKey = (uint)(0x70 + function - 1);
            return true;
        }

        var namedKeys = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
        {
            ["SPACE"] = 0x20,
            ["TAB"] = 0x09,
            ["ENTER"] = 0x0D,
            ["ESC"] = 0x1B,
            ["ESCAPE"] = 0x1B,
            ["BACKSPACE"] = 0x08,
            ["INSERT"] = 0x2D,
            ["DELETE"] = 0x2E,
            ["HOME"] = 0x24,
            ["END"] = 0x23,
            ["PAGEUP"] = 0x21,
            ["PAGEDOWN"] = 0x22,
            ["UP"] = 0x26,
            ["DOWN"] = 0x28,
            ["LEFT"] = 0x25,
            ["RIGHT"] = 0x27,
            ["PRINTSCREEN"] = 0x2C,
            ["PAUSE"] = 0x13,
            ["CAPSLOCK"] = 0x14
        };
        if (namedKeys.TryGetValue(keyName, out virtualKey))
        {
            return true;
        }

        error = $"未知按键：{keyName}";
        return false;
    }

    public void Dispose()
    {
        DisposeRegistrations();
    }

    private void DisposeRegistrations()
    {
        foreach (var id in _registered.Keys)
        {
            if (_window != IntPtr.Zero)
            {
                UnregisterHotKey(_window, id);
            }
        }
        _registered.Clear();
        Registrations = [];
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr window, int id);
}

public sealed record HotkeyDefinition(
    string Id, string Label, string Combo, string Action, string Arguments);

public sealed record RegisteredHotkey(
    int Id, string DefinitionId, string Action, string Arguments, string Label);

public sealed record HotkeyRegistration(
    string Id,
    string Label,
    string Combo,
    string Action,
    string Arguments,
    bool Registered,
    string? Error);
