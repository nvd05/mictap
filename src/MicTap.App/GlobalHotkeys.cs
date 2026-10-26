using System.Runtime.InteropServices;
using System.Windows.Interop;
using MicTap.Core;

namespace MicTap.App;

/// <summary>
/// System-wide hotkeys through RegisterHotKey on a message-only window, so they work while the main window is
/// hidden in the tray. Actions run on the UI thread.
/// </summary>
public sealed class GlobalHotkeys : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;
    private static readonly IntPtr MessageOnlyParent = new(-3); // HWND_MESSAGE

    private readonly HwndSource _window;
    private readonly Dictionary<int, Action> _actions = [ ];

    public GlobalHotkeys()
    {
        _window = new HwndSource(new HwndSourceParameters("mictap hotkeys") { ParentWindow = MessageOnlyParent, WindowStyle = 0 });
        _window.AddHook(OnMessage);
    }

    /// <summary>Registers <paramref name="text"/> such as "Ctrl+Alt+L"; returns why it is not available, or null.</summary>
    public string? Register(string text, Action action)
    {
        if (!Hotkey.TryParse(text, out Hotkey? hotkey, out string error))
        {
            return error;
        }

        int id = _actions.Count + 1;
        if (!RegisterHotKey(_window.Handle, id, (uint)hotkey!.Modifiers | ModNoRepeat, (uint)hotkey.VirtualKey))
        {
            return $"«{text}» уже занято другой программой (ошибка Windows {Marshal.GetLastWin32Error()}).";
        }

        _actions[id] = action;
        return null;
    }

    public void Dispose()
    {
        foreach (int id in _actions.Keys)
        {
            UnregisterHotKey(_window.Handle, id);
        }

        _window.Dispose();
    }

    private IntPtr OnMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmHotkey && _actions.TryGetValue(wParam.ToInt32(), out Action? action))
        {
            handled = true;
            action();
        }

        return IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr window, int id);
}
