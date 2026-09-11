using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace Ferry;

// System-wide hotkeys, hung off the main window's HWND.
//
// RegisterHotKey is first-come-first-served across the whole session: if
// another app already holds a combination, registration fails and there is
// nothing to be done about it but say so. That is why TryRegister returns a
// bool instead of throwing — a taken hotkey is a normal condition, not a fault.
internal sealed class HotkeyManager : IDisposable
{
    private const int WmHotkey = 0x0312;

    // MOD_NOREPEAT is always OR'd in by Register: without it, holding the keys
    // down repeats the message at the keyboard's auto-repeat rate, which for a
    // "send my clipboard" action means sending it twenty times.
    private const uint ModNoRepeat = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly IntPtr _hwnd;
    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _handlers = new();
    private int _nextId = 0xF100;
    private bool _disposed;

    // The window must already have an HWND: construct from SourceInitialized
    // or later, never from the constructor.
    public HotkeyManager(Window window)
    {
        _hwnd = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_hwnd)
            ?? throw new InvalidOperationException("window has no HwndSource yet");
        _source.AddHook(Hook);
    }

    public bool TryRegister(ModifierKeys modifiers, Key key, Action onPressed)
    {
        var id = _nextId++;
        var vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (!RegisterHotKey(_hwnd, id, Translate(modifiers) | ModNoRepeat, vk))
        {
            App.Log($"hotkey {Describe(modifiers, key)} unavailable " +
                    $"(win32 {Marshal.GetLastWin32Error()})");
            return false;
        }
        _handlers[id] = onPressed;
        App.Log($"hotkey {Describe(modifiers, key)} registered");
        return true;
    }

    public static string Describe(ModifierKeys modifiers, Key key)
    {
        var parts = new List<string>(4);
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(key.ToString());
        return string.Join("+", parts);
    }

    // WPF's ModifierKeys and Win32's MOD_ flags happen to disagree: MOD_ALT is
    // 1 and MOD_CONTROL is 2, the other way round from ModifierKeys.
    private static uint Translate(ModifierKeys modifiers)
    {
        uint flags = 0;
        if (modifiers.HasFlag(ModifierKeys.Alt)) flags |= 0x0001;
        if (modifiers.HasFlag(ModifierKeys.Control)) flags |= 0x0002;
        if (modifiers.HasFlag(ModifierKeys.Shift)) flags |= 0x0004;
        if (modifiers.HasFlag(ModifierKeys.Windows)) flags |= 0x0008;
        return flags;
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmHotkey || !_handlers.TryGetValue(wParam.ToInt32(), out var action))
            return IntPtr.Zero;
        handled = true;
        // A throw here would travel out through the window procedure, which is
        // not a place an exception can be recovered from sensibly.
        try { action(); }
        catch (Exception ex) { App.Log($"hotkey handler: {ex}"); }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _source.RemoveHook(Hook);
        foreach (var id in _handlers.Keys) UnregisterHotKey(_hwnd, id);
        _handlers.Clear();
    }
}
