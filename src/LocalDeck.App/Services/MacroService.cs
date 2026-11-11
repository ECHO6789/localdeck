using System.Diagnostics;
using System.Runtime.InteropServices;
using LocalDeck.App.Models;

namespace LocalDeck.App.Services;

public sealed class MacroService : IDisposable
{
    private const int WhMouseLl = 14;
    private const int WmXButtonDown = 0x020B;
    private const int WmXButtonUp = 0x020C;
    private readonly SettingsService _settings;
    private readonly LowLevelMouseProc _hookProc;
    private IntPtr _hook;
    private int _running;

    public MacroService(SettingsService settings)
    {
        _settings = settings;
        _hookProc = MouseHookCallback;
    }

    public bool HookInstalled => _hook != IntPtr.Zero;

    public void Start()
    {
        if (_hook != IntPtr.Zero)
        {
            return;
        }

        _hook = SetWindowsHookEx(WhMouseLl, _hookProc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
        {
            AppPaths.Log($"Mouse macro hook failed: {Marshal.GetLastWin32Error()}");
        }
    }

    public void Stop()
    {
        if (_hook == IntPtr.Zero)
        {
            return;
        }
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    public bool RunMacro(string macroId)
    {
        var macro = _settings.Current.Macros.FirstOrDefault(item =>
            item.Id.Equals(macroId, StringComparison.OrdinalIgnoreCase) && item.Enabled);
        if (macro is null)
        {
            return false;
        }

        _ = Task.Run(() => ExecuteMacroAsync(macro));
        return true;
    }

    public void TypeText(string text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            SendUnicodeText(text);
        }
    }

    public void RunCommand(string command)
    {
        if (!string.IsNullOrWhiteSpace(command))
        {
            StartCommand(command);
        }
    }

    public void Dispose()
    {
        Stop();
    }

    private IntPtr MouseHookCallback(int code, IntPtr message, IntPtr data)
    {
        if (code < 0 || !_settings.Current.MouseMacrosEnabled)
        {
            return CallNextHookEx(_hook, code, message, data);
        }

        var info = Marshal.PtrToStructure<MouseHookData>(data);
        var button = (info.MouseData >> 16) & 0xFFFF;
        var binding = button switch
        {
            1 => "MouseX1",
            2 => "MouseX2",
            _ => string.Empty
        };
        if (string.IsNullOrEmpty(binding) ||
            !_settings.Current.MacroBindings.TryGetValue(binding, out var macroId) ||
            string.IsNullOrWhiteSpace(macroId))
        {
            return CallNextHookEx(_hook, code, message, data);
        }

        var macro = _settings.Current.Macros.FirstOrDefault(item =>
            item.Id == macroId && item.Enabled);
        if (macro is null)
        {
            return CallNextHookEx(_hook, code, message, data);
        }

        if (message.ToInt32() == WmXButtonDown)
        {
            _ = Task.Run(() => ExecuteMacroAsync(macro));
        }

        if (message.ToInt32() is WmXButtonDown or WmXButtonUp)
        {
            return (IntPtr)1;
        }

        return CallNextHookEx(_hook, code, message, data);
    }

    private async Task ExecuteMacroAsync(MacroDefinition macro)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return;
        }

        try
        {
            foreach (var step in macro.Steps)
            {
                if (step.DelayMs > 0)
                {
                    await Task.Delay(Math.Min(step.DelayMs, 10_000));
                }

                switch (step.Type.ToLowerInvariant())
                {
                    case "key":
                        SendKeyCombo(step.Value);
                        break;
                    case "text":
                        SendUnicodeText(step.Value);
                        break;
                    case "mouse":
                        SendMouseButton(step.Value);
                        break;
                    case "mouseat":
                        SetCursorPos(step.X, step.Y);
                        SendMouseButton(step.Value);
                        break;
                    case "run":
                        if (!string.IsNullOrWhiteSpace(step.Value))
                        {
                            StartCommand(step.Value);
                        }
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            AppPaths.Log($"Macro {macro.Name} failed: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    private static void StartCommand(string value)
    {
        if (File.Exists(value) ||
            Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto")
        {
            Process.Start(new ProcessStartInfo(value) { UseShellExecute = true });
            return;
        }

        Process.Start(new ProcessStartInfo("cmd.exe", $"/c {value}")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }

    private static void SendKeyCombo(string combo)
    {
        if (!HotkeyService.TryParse(combo, out var modifiers, out var key, out _))
        {
            return;
        }

        var virtualKeys = new List<ushort>();
        if ((modifiers & 0x0002) != 0) virtualKeys.Add(0x11);
        if ((modifiers & 0x0001) != 0) virtualKeys.Add(0x12);
        if ((modifiers & 0x0004) != 0) virtualKeys.Add(0x10);
        if ((modifiers & 0x0008) != 0) virtualKeys.Add(0x5B);

        foreach (var modifier in virtualKeys)
        {
            SendVirtualKey(modifier, keyUp: false);
            Thread.Sleep(8);
        }
        SendVirtualKey((ushort)key, keyUp: false);
        Thread.Sleep(20);
        SendVirtualKey((ushort)key, keyUp: true);
        for (var i = virtualKeys.Count - 1; i >= 0; i--)
        {
            SendVirtualKey(virtualKeys[i], keyUp: true);
            Thread.Sleep(8);
        }
    }

    private static void SendUnicodeText(string text)
    {
        foreach (var character in text)
        {
            var input = KeyboardInput(character, keyUp: false);
            SendInput(1, [input], Marshal.SizeOf<Input>());
            input = KeyboardInput(character, keyUp: true);
            SendInput(1, [input], Marshal.SizeOf<Input>());
            Thread.Sleep(3);
        }
    }

    private static void SendVirtualKey(ushort key, bool keyUp)
    {
        var input = new Input
        {
            Type = 1,
            Data = new InputUnion
            {
                Keyboard = new KeyboardInputData
                {
                    VirtualKey = key,
                    Flags = keyUp ? 0x0002u : 0u
                }
            }
        };
        SendInput(1, [input], Marshal.SizeOf<Input>());
    }

    private static Input KeyboardInput(char character, bool keyUp) => new()
    {
        Type = 1,
        Data = new InputUnion
        {
            Keyboard = new KeyboardInputData
            {
                ScanCode = character,
                Flags = 0x0004u | (keyUp ? 0x0002u : 0u)
            }
        }
    };

    private static void SendMouseButton(string value)
    {
        if (value.Trim().Equals("back", StringComparison.OrdinalIgnoreCase) ||
            value.Trim().Equals("forward", StringComparison.OrdinalIgnoreCase))
        {
            var data = value.Trim().Equals("back", StringComparison.OrdinalIgnoreCase) ? 1u : 2u;
            var downInput = new Input
            {
                Type = 0,
                Data = new InputUnion { Mouse = new MouseInputData { MouseData = data, Flags = 0x0080 } }
            };
            SendInput(1, [downInput], Marshal.SizeOf<Input>());
            downInput.Data.Mouse.Flags = 0x0100;
            SendInput(1, [downInput], Marshal.SizeOf<Input>());
            return;
        }

        var (down, up) = value.Trim().ToLowerInvariant() switch
        {
            "right" => (0x0008u, 0x0010u),
            "middle" => (0x0020u, 0x0040u),
            _ => (0x0002u, 0x0004u)
        };
        var input = new Input
        {
            Type = 0,
            Data = new InputUnion { Mouse = new MouseInputData { Flags = down } }
        };
        SendInput(1, [input], Marshal.SizeOf<Input>());
        input.Data.Mouse.Flags = up;
        SendInput(1, [input], Marshal.SizeOf<Input>());
    }

    private delegate IntPtr LowLevelMouseProc(int code, IntPtr message, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseHookData
    {
        public Point Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInputData Mouse;
        [FieldOffset(0)] public KeyboardInputData Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInputData
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInputData
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int hookType, LowLevelMouseProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(
        uint count, [MarshalAs(UnmanagedType.LPArray)] Input[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);
}
