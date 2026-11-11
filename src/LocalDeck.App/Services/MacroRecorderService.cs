using System.Diagnostics;
using System.Runtime.InteropServices;
using LocalDeck.App.Models;

namespace LocalDeck.App.Services;

public sealed class MacroRecorderService : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WhMouseLl = 14;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyUp = 0x0105;
    private const int WmLButtonDown = 0x0201;
    private const int WmRButtonDown = 0x0204;
    private const int WmMButtonDown = 0x0207;
    private const int WmXButtonDown = 0x020B;
    private const uint LlkhfInjected = 0x10;
    private const uint LlmhfInjected = 0x01;
    private readonly object _sync = new();
    private readonly List<MacroStep> _steps = [];
    private readonly HashSet<int> _modifiers = [];
    private readonly HashSet<int> _keysDown = [];
    private readonly LowLevelKeyboardProc _keyboardProc;
    private readonly LowLevelMouseProc _mouseProc;
    private IntPtr _keyboardHook;
    private IntPtr _mouseHook;
    private Stopwatch _clock = new();
    private long _lastEventTicks;

    public MacroRecorderService()
    {
        _keyboardProc = KeyboardCallback;
        _mouseProc = MouseCallback;
    }

    public bool IsRecording => _keyboardHook != IntPtr.Zero || _mouseHook != IntPtr.Zero;

    public bool Start()
    {
        if (IsRecording)
        {
            return true;
        }

        lock (_sync)
        {
            _steps.Clear();
            _modifiers.Clear();
            _keysDown.Clear();
            _clock = Stopwatch.StartNew();
            _lastEventTicks = 0;
        }
        _keyboardHook = SetWindowsHookEx(
            WhKeyboardLl, _keyboardProc, GetModuleHandle(null), 0);
        _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseProc, GetModuleHandle(null), 0);
        if (_keyboardHook == IntPtr.Zero || _mouseHook == IntPtr.Zero)
        {
            Stop();
            return false;
        }
        return true;
    }

    public IReadOnlyList<MacroStep> Stop()
    {
        if (_keyboardHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }
        if (_mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
        lock (_sync)
        {
            return _steps.Select(CloneStep).ToList();
        }
    }

    public IReadOnlyList<MacroStep> GetSteps()
    {
        lock (_sync)
        {
            return _steps.Select(CloneStep).ToList();
        }
    }

    public void Dispose()
    {
        Stop();
    }

    private IntPtr KeyboardCallback(int code, IntPtr message, IntPtr data)
    {
        if (code < 0)
        {
            return CallNextHookEx(_keyboardHook, code, message, data);
        }

        var info = Marshal.PtrToStructure<KeyboardHookData>(data);
        if ((info.Flags & LlkhfInjected) != 0)
        {
            return CallNextHookEx(_keyboardHook, code, message, data);
        }

        var messageId = message.ToInt32();
        if (messageId is WmKeyUp or WmSysKeyUp)
        {
            _keysDown.Remove((int)info.VirtualKey);
            if (IsModifier((int)info.VirtualKey))
            {
                _modifiers.Remove((int)info.VirtualKey);
            }
            return CallNextHookEx(_keyboardHook, code, message, data);
        }

        if (messageId is not (WmKeyDown or WmSysKeyDown))
        {
            return CallNextHookEx(_keyboardHook, code, message, data);
        }

        if (info.VirtualKey == 0x77)
        {
            Stop();
            return (IntPtr)1;
        }

        if (IsModifier((int)info.VirtualKey))
        {
            _modifiers.Add((int)info.VirtualKey);
            return CallNextHookEx(_keyboardHook, code, message, data);
        }

        if (!_keysDown.Add((int)info.VirtualKey))
        {
            return CallNextHookEx(_keyboardHook, code, message, data);
        }

        var combo = BuildCombo((int)info.VirtualKey);
        if (!string.IsNullOrWhiteSpace(combo))
        {
            AddStep("key", combo, 0, 0);
        }
        return CallNextHookEx(_keyboardHook, code, message, data);
    }

    private IntPtr MouseCallback(int code, IntPtr message, IntPtr data)
    {
        if (code < 0)
        {
            return CallNextHookEx(_mouseHook, code, message, data);
        }

        var info = Marshal.PtrToStructure<MouseHookData>(data);
        if ((info.Flags & LlmhfInjected) != 0)
        {
            return CallNextHookEx(_mouseHook, code, message, data);
        }

        var button = message.ToInt32() switch
        {
            WmLButtonDown => "left",
            WmRButtonDown => "right",
            WmMButtonDown => "middle",
            WmXButtonDown when ((info.MouseData >> 16) & 0xFFFF) == 1 => "back",
            WmXButtonDown when ((info.MouseData >> 16) & 0xFFFF) == 2 => "forward",
            _ => null
        };
        if (button is not null)
        {
            AddStep("mouseAt", button, info.Point.X, info.Point.Y);
        }
        return CallNextHookEx(_mouseHook, code, message, data);
    }

    private void AddStep(string type, string value, int x, int y)
    {
        lock (_sync)
        {
            var now = _clock.ElapsedMilliseconds;
            var delay = _lastEventTicks == 0 ? 0 : (int)Math.Clamp(now - _lastEventTicks, 15, 2000);
            _steps.Add(new MacroStep { Type = type, Value = value, DelayMs = delay, X = x, Y = y });
            _lastEventTicks = now;
        }
    }

    private string BuildCombo(int virtualKey)
    {
        var parts = new List<string>();
        if (_modifiers.Contains(0x11) || _modifiers.Contains(0xA2) || _modifiers.Contains(0xA3))
            parts.Add("Ctrl");
        if (_modifiers.Contains(0x12) || _modifiers.Contains(0xA4) || _modifiers.Contains(0xA5))
            parts.Add("Alt");
        if (_modifiers.Contains(0x10) || _modifiers.Contains(0xA0) || _modifiers.Contains(0xA1))
            parts.Add("Shift");
        if (_modifiers.Contains(0x5B) || _modifiers.Contains(0x5C))
            parts.Add("Win");
        parts.Add(KeyName(virtualKey));
        return string.Join("+", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static bool IsModifier(int key) => key is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5;

    private static string KeyName(int key)
    {
        if (key is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A)
            return ((char)key).ToString();
        if (key is >= 0x70 and <= 0x87)
            return $"F{key - 0x6F}";
        var names = new Dictionary<int, string>
        {
            [0x08] = "Backspace", [0x09] = "Tab", [0x0D] = "Enter", [0x1B] = "Esc",
            [0x20] = "Space", [0x21] = "PageUp", [0x22] = "PageDown", [0x23] = "End",
            [0x24] = "Home", [0x25] = "Left", [0x26] = "Up", [0x27] = "Right",
            [0x28] = "Down", [0x2D] = "Insert", [0x2E] = "Delete", [0x2C] = "PrintScreen",
            [0x13] = "Pause", [0x14] = "CapsLock", [0xBA] = ";", [0xBB] = "=",
            [0xBC] = ",", [0xBD] = "-", [0xBE] = ".", [0xBF] = "/", [0xC0] = "`",
            [0xDB] = "[", [0xDC] = "\\", [0xDD] = "]", [0xDE] = "'"
        };
        return names.GetValueOrDefault(key, $"VK_{key:X2}");
    }

    private static MacroStep CloneStep(MacroStep step) => new()
    {
        Type = step.Type,
        Value = step.Value,
        DelayMs = step.DelayMs,
        X = step.X,
        Y = step.Y
    };

    private delegate IntPtr LowLevelKeyboardProc(int code, IntPtr message, IntPtr data);
    private delegate IntPtr LowLevelMouseProc(int code, IntPtr message, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardHookData
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseHookData
    {
        public NativePoint Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int hookType, Delegate callback, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
