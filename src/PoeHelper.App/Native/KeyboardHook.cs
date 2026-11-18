using System.Runtime.InteropServices;
using System.Windows.Input;

namespace PoeHelper.App.Native;

[Flags]
public enum Mods { None = 0, Ctrl = 1, Alt = 2, Shift = 4 }

public readonly record struct Hotkey(Mods Mods, int Vk)
{
    /// <summary>Parses "Ctrl+D", "Alt+Shift+F2" etc.</summary>
    public static Hotkey Parse(string text)
    {
        var mods = Mods.None;
        int vk = 0;
        foreach (var part in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= Mods.Ctrl; break;
                case "alt": mods |= Mods.Alt; break;
                case "shift": mods |= Mods.Shift; break;
                default:
                    if (Enum.TryParse<Key>(part, true, out var key)) vk = KeyInterop.VirtualKeyFromKey(key);
                    else throw new FormatException($"Unknown key '{part}' in hotkey '{text}'.");
                    break;
            }
        }
        if (vk == 0) throw new FormatException($"Hotkey '{text}' has no key.");
        return new Hotkey(mods, vk);
    }
}

/// <summary>
/// Low-level keyboard hook. Hotkeys fire (and are swallowed) only while Path of Exile is the foreground
/// window, so Ctrl+D etc. keep working everywhere else. Esc is swallowed only while an overlay is open.
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    readonly Win32.LowLevelKeyboardProc _proc;
    readonly Dictionary<Hotkey, Action> _bindings = new();
    nint _hook;

    public Func<bool> OverlayOpen { get; set; } = () => false;
    public event Action? Escape;

    public KeyboardHook()
    {
        _proc = HookProc; // keep the delegate alive
        _hook = Win32.SetWindowsHookExW(Win32.WH_KEYBOARD_LL, _proc, Win32.GetModuleHandleW(null), 0);
        if (_hook == 0) throw new InvalidOperationException($"SetWindowsHookEx failed ({Marshal.GetLastWin32Error()}).");
    }

    public void Bind(Hotkey key, Action action) => _bindings[key] = action;
    public void ClearBindings() => _bindings.Clear();

    nint HookProc(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0 && (wParam == Win32.WM_KEYDOWN || wParam == Win32.WM_SYSKEYDOWN))
        {
            var info = Marshal.PtrToStructure<Win32.KBDLLHOOKSTRUCT>(lParam);
            bool injected = (info.flags & Win32.LLKHF_INJECTED) != 0;
            if (!injected && Handle((int)info.vkCode)) return 1;
        }
        return Win32.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    bool Handle(int vk)
    {
        if (vk == Win32.VK_ESCAPE && OverlayOpen())
        {
            System.Windows.Application.Current.Dispatcher.BeginInvoke(() => Escape?.Invoke());
            return true;
        }
        var mods = Mods.None;
        if (Win32.IsDown(Win32.VK_CONTROL)) mods |= Mods.Ctrl;
        if (Win32.IsDown(Win32.VK_MENU)) mods |= Mods.Alt;
        if (Win32.IsDown(Win32.VK_SHIFT)) mods |= Mods.Shift;
        if (!_bindings.TryGetValue(new Hotkey(mods, vk), out var action)) return false;
        if (Win32.PoeForegroundWindow() == 0 && !OverlayOpen()) return false;
        // Never do work inside the hook: Windows drops hooks that take too long.
        System.Windows.Application.Current.Dispatcher.BeginInvoke(action);
        return true;
    }

    public void Dispose()
    {
        if (_hook != 0) Win32.UnhookWindowsHookEx(_hook);
        _hook = 0;
    }
}
