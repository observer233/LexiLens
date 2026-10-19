using System.Windows;
using System.Windows.Interop;

namespace LexiLens.App.Platform;

internal sealed class Hotkeys : IDisposable
{
    private readonly nint _hwnd;
    private readonly HwndSource _source;
    private readonly List<int> _registered = new();
    internal event Action<int>? Pressed;
    internal List<string> Conflicts { get; } = new();

    internal Hotkeys(Window window)
    {
        _hwnd = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_hwnd);
        _source.AddHook(Hook);
        Register(1, 0x54, "Ctrl+Alt+T");
        Register(2, 0x52, "Ctrl+Alt+R");
        Register(3, 0x48, "Ctrl+Alt+H");
        Register(4, 0x4C, "Ctrl+Alt+L");
        Register(5, 0x57, "Ctrl+Alt+W");
        Register(6, 0x46, "Ctrl+Alt+F");
        Register(7, 0x41, "Ctrl+Alt+A");
        Register(8, 0x51, "Ctrl+Alt+Q");
        Register(9, 0x58, "Ctrl+Alt+X");
        Register(10, 0x4D, "Ctrl+Alt+M");
        Register(11, 0x56, "Ctrl+Alt+V");
    }

    private void Register(int id, uint key, string label)
    {
        if (NativeMethods.RegisterHotKey(_hwnd, id, 0x4000 | 0x2 | 0x1, key)) _registered.Add(id);
        else Conflicts.Add(label);
    }

    private nint Hook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == 0x0312) { Pressed?.Invoke((int)wParam); handled = true; }
        return 0;
    }

    public void Dispose()
    {
        foreach (var id in _registered) NativeMethods.UnregisterHotKey(_hwnd, id);
        _source.RemoveHook(Hook);
    }
}
