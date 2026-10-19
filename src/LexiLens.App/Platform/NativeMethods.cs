using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using LexiLens.Core;

namespace LexiLens.App.Platform;

internal static class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] internal struct BitmapInfo
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount;
        public uint Compression, SizeImage; public int XPels, YPels; public uint ClrUsed, ClrImportant;
    }
    internal delegate bool MonitorCallback(nint monitor, nint dc, ref Rect bounds, nint data);
    [DllImport("user32.dll")] internal static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorCallback callback, nint data);
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(nint hwnd, int id);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] internal static extern int GetWindowLong(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] internal static extern int SetWindowLong(nint hwnd, int index, int value);
    [DllImport("user32.dll")] internal static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] internal static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("gdi32.dll")] internal static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] internal static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] internal static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] internal static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] internal static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll", SetLastError = true)] internal static extern bool StretchBlt(nint dest, int x, int y, int width, int height, nint source, int sx, int sy, int sw, int sh, uint rop);
    [DllImport("gdi32.dll")] internal static extern int SetStretchBltMode(nint dc, int mode);

    internal static bool ExcludeFromCapture(Window window) => SetWindowDisplayAffinity(new WindowInteropHelper(window).Handle, 0x11);

    internal static void SetClickThrough(Window window, bool enabled)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        const int index = -20, transparent = 0x20, noActivate = 0x08000000;
        var style = GetWindowLong(hwnd, index);
        SetWindowLong(hwnd, index, enabled ? style | transparent | noActivate : style & ~transparent & ~noActivate);
    }

    internal static IReadOnlyList<Rect> Monitors()
    {
        var result = new List<Rect>();
        EnumDisplayMonitors(0, 0, (nint _, nint _, ref Rect rect, nint _) => { result.Add(rect); return true; }, 0);
        return result;
    }

    internal static bool IsOnDesktop(CaptureRegion region) => Monitors().Any(m =>
        region.X >= m.Left && region.Y >= m.Top && (long)region.X + region.Width <= m.Right && (long)region.Y + region.Height <= m.Bottom);

    internal static byte[] Capture(CaptureRegion region, int maxDimension, out int width, out int height)
    {
        if (!region.IsValid) throw new ArgumentException("识别区域无效，请重新框选。");
        var scale = Math.Min(1.0, (double)maxDimension / Math.Max(region.Width, region.Height));
        width = Math.Max(1, (int)Math.Round(region.Width * scale));
        height = Math.Max(1, (int)Math.Round(region.Height * scale));
        var screen = GetDC(0);
        var memory = CreateCompatibleDC(screen);
        nint bitmap = 0, old = 0;
        try
        {
            var info = new BitmapInfo { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
            bitmap = CreateDIBSection(screen, ref info, 0, out var bits, 0, 0);
            if (screen == 0 || memory == 0 || bitmap == 0 || bits == 0) throw new Win32Exception("无法创建截图缓冲区。");
            old = SelectObject(memory, bitmap);
            SetStretchBltMode(memory, 4);
            if (!StretchBlt(memory, 0, 0, width, height, screen, region.X, region.Y, region.Width, region.Height, 0x00CC0020 | 0x40000000))
                throw new Win32Exception("截图失败。请切换游戏为无边框窗口模式。");
            var bytes = new byte[checked(width * height * 4)];
            Marshal.Copy(bits, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            if (old != 0) SelectObject(memory, old);
            if (bitmap != 0) DeleteObject(bitmap);
            if (memory != 0) DeleteDC(memory);
            if (screen != 0) ReleaseDC(0, screen);
        }
    }
}
