using System.Diagnostics;
using System.Runtime.InteropServices;
using LexiLens.Core;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace LexiLens.App.Platform;

// Diagnostic HWND capture. The pool retains two GPU frames; CPU readback happens
// only when a caller samples. There is no desktop capture or GDI fallback here.
internal sealed class WgcRegionCapture : IRegionCapture
{
    private readonly object _sync = new();
    private readonly nint _hwnd;
    private readonly TextBounds _roi;
    private readonly SemaphoreSlim _arrived = new(0, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private IDirect3DDevice? _winrtDevice;
    private D3D11Readback? _readback;
    private SizeInt32 _size;
    private volatile bool _closed;
    private bool _disposed;
    private long _dropped;
    private Direct3D11CaptureFrame? _latest;
    private CapturedRegionFrame? _latestCpu;
    private bool _latestRead;
    private Exception? _arrivalError;

    internal WgcRegionCapture(nint hwnd, TextBounds roi)
    {
        _hwnd = hwnd; _roi = roi;
        if (!GraphicsCaptureSession.IsSupported()) throw new NotSupportedException("当前系统或显卡不支持 Windows.Graphics.Capture。");
        try
        {
            _item = D3D11Readback.CreateItem(hwnd);
            _item.Closed += OnClosed;
            _readback = new D3D11Readback();
            _winrtDevice = _readback.CreateWinrtDevice();
            _size = _item.Size;
            _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _size);
            _pool.FrameArrived += OnFrameArrived;
            _session = _pool.CreateCaptureSession(_item);
            _session.IsCursorCaptureEnabled = false;
            _session.StartCapture();
        }
        catch { Dispose(); throw; }
    }

    private void OnClosed(GraphicsCaptureItem sender, object args) { _closed = true; Signal(); }
    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        lock (_sync)
        {
            if (_disposed) return;
            if (sender != _pool) return;
            Direct3D11CaptureFrame? frame = null;
            try
            {
                while ((frame = sender.TryGetNextFrame()) != null)
                {
                    var content = frame.ContentSize;
                    if (content.Width <= 0 || content.Height <= 0) { frame.Dispose(); frame = null; _dropped++; continue; }
                    if (content.Width != _size.Width || content.Height != _size.Height)
                    {
                        frame.Dispose(); frame = null; _dropped++;
                        if (_latest != null) { _latest.Dispose(); _latest = null; if (!_latestRead) _dropped++; }
                        _latestCpu = null;
                        _size = content;
                        _session!.Dispose();
                        sender.FrameArrived -= OnFrameArrived;
                        // A static target may not submit another changed frame
                        // after Recreate. A new pool/session requests its initial
                        // surface at the new dimensions. A closed session's old
                        // pool cannot be used to create another session.
                        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_winrtDevice!, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _size);
                        _pool.FrameArrived += OnFrameArrived;
                        _session = _pool.CreateCaptureSession(_item!);
                        _session.IsCursorCaptureEnabled = false;
                        _session.StartCapture();
                        sender.Dispose();
                        break;
                    }
                    // Drain on every notification, not only when OCR is ready:
                    // otherwise a full pool can retain old frames during inference.
                    if (_latest != null) { _latest.Dispose(); if (!_latestRead) _dropped++; }
                    _latestCpu = null; _latestRead = false;
                    _latest = frame; frame = null;
                }
            }
            catch (Exception ex) { _arrivalError = ex; }
            finally { frame?.Dispose(); }
        }
        Signal();
    }
    private void Signal()
    {
        // A signal represents "check the pool", never a count of GPU frames.
        try { _arrived.Release(); } catch (SemaphoreFullException) { }
    }

    public async ValueTask<CapturedRegionFrame> CaptureAsync(CancellationToken ct)
    {
        var waitStart = Stopwatch.GetTimestamp();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        linked.CancelAfter(TimeSpan.FromSeconds(3));
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_closed) throw new InvalidOperationException("WGC 目标窗口已关闭。");
                var window = RegionCapture.WindowRegion(_hwnd);
                if (_arrivalError != null) throw new InvalidOperationException("WGC 帧到达处理失败。", _arrivalError);
                var frame = _latest;
                // During resize, do not project an old-size surface onto the new
                // window. Movement without a content change needs only new bounds.
                if (frame != null && frame.ContentSize.Width == window.Width && frame.ContentSize.Height == window.Height)
                {
                    var waitMs = Stopwatch.GetElapsedTime(waitStart).TotalMilliseconds;
                    var screenRegion = RegionCapture.Crop(window, _roi);
                    if (_latestCpu != null)
                        return _latestCpu with { ScreenRegion = screenRegion, FrameWaitMs = waitMs, CaptureMs = 0,
                            DroppedFrames = _dropped, IsRepeatedFrame = true };
                    var copyStart = Stopwatch.GetTimestamp();
                    var sourceTime = frame.SystemRelativeTime;
                    // The documented value is compositor QPC in 100 ns units.
                    var capturedAt = (long)((decimal)sourceTime.Ticks * Stopwatch.Frequency / TimeSpan.TicksPerSecond);
                    var pixelRoi = RegionCapture.Crop(new(0, 0, frame.ContentSize.Width, frame.ContentSize.Height), _roi);
                    var bytes = _readback!.CopyRoi(frame.Surface, pixelRoi);
                    _latestRead = true;
                    _latestCpu = new(bytes, pixelRoi.Width, pixelRoi.Height,
                        screenRegion, capturedAt, waitMs,
                        Stopwatch.GetElapsedTime(copyStart).TotalMilliseconds, "WGC",
                        _dropped, sourceTime);
                    return _latestCpu;
                }
            }
            try { await _arrived.WaitAsync(linked.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && !_lifetime.IsCancellationRequested)
            {
                lock (_sync)
                {
                    var window = RegionCapture.WindowRegion(_hwnd);
                    throw new TimeoutException($"WGC 3 秒内未提供有效新帧（窗口 {window.Width}×{window.Height}，帧池 {_size.Width}×{_size.Height}，最新帧 {_latest?.ContentSize.Width}×{_latest?.ContentSize.Height}）；请检查目标窗口、显示模式或受保护画面。未自动切换捕获后端。");
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true; _lifetime.Cancel();
            if (_pool != null) _pool.FrameArrived -= OnFrameArrived;
            if (_item != null) _item.Closed -= OnClosed;
            _latest?.Dispose(); _latest = null;
            _session?.Dispose(); _session = null;
            _pool?.Dispose(); _pool = null;
            _winrtDevice?.Dispose(); _winrtDevice = null;
            _readback?.Dispose(); _readback = null;
        }
    }
}

// Small, private ABI boundary for the official D3D11 / DXGI interop APIs.
// Vtable slots below are from the Windows SDK d3d11.h, not projected WinRT types.
internal sealed class D3D11Readback : IDisposable
{
    private nint _device, _context, _staging;
    private int _width, _height;
    private static readonly Guid DxgiDeviceId = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    private static readonly Guid SurfaceAccessId = new("a9b3d012-3df2-4ee3-b8d1-8695f457d3c1");
    private static readonly Guid TextureId = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    internal D3D11Readback()
    {
        try
        {
            // Hardware, BGRA support, default feature levels. Errors stay explicit.
            Marshal.ThrowExceptionForHR(D3D11CreateDevice(0, 1, 0, 0x20, 0, 0, 7,
                out _device, out _, out _context));
        }
        catch { Dispose(); throw; }
    }

    internal static GraphicsCaptureItem CreateItem(nint hwnd)
    {
        const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        nint hstring = 0, factory = 0, item = 0;
        try
        {
            Marshal.ThrowExceptionForHR(WindowsCreateString(className, className.Length, out hstring));
            var interop = new Guid("3628e81b-3cac-4c60-b7f4-23ce0e0c3356");
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(hstring, in interop, out factory));
            var itemId = new Guid("79c3f95b-31f7-4ec2-a464-632ef5d30760");
            Marshal.ThrowExceptionForHR(Method<CreateForWindow>(factory, 3)(factory, hwnd, in itemId, out item));
            return GraphicsCaptureItem.FromAbi(item);
        }
        finally
        {
            if (item != 0) Marshal.Release(item);
            if (factory != 0) Marshal.Release(factory);
            if (hstring != 0) WindowsDeleteString(hstring);
        }
    }

    internal IDirect3DDevice CreateWinrtDevice()
    {
        nint dxgi = 0, inspectable = 0;
        try
        {
            var id = DxgiDeviceId;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(_device, in id, out dxgi));
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out inspectable));
            return WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        }
        finally
        {
            if (inspectable != 0) Marshal.Release(inspectable);
            if (dxgi != 0) Marshal.Release(dxgi);
        }
    }

    internal byte[] CopyRoi(IDirect3DSurface surface, CaptureRegion roi)
    {
        if ((long)roi.Width * roi.Height * 4 > 134_217_728) throw new ArgumentException("捕获区域过大，请缩小到字幕区域（最多 128 MiB BGRA）。");
        nint inspectable = 0, access = 0, texture = 0;
        try
        {
            inspectable = WinRT.MarshalInterface<IDirect3DSurface>.FromManaged(surface);
            var accessId = SurfaceAccessId;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(inspectable, in accessId, out access));
            var textureId = TextureId;
            Marshal.ThrowExceptionForHR(Method<GetInterface>(access, 3)(access, in textureId, out texture));
            if (_staging == 0 || _width != roi.Width || _height != roi.Height)
            {
                if (_staging != 0) { Marshal.Release(_staging); _staging = 0; }
                var desc = new TextureDescription
                {
                    Width = (uint)roi.Width, Height = (uint)roi.Height, MipLevels = 1,
                    ArraySize = 1, Format = 87, SampleCount = 1, Usage = 3, CpuAccessFlags = 0x20000
                };
                Marshal.ThrowExceptionForHR(Method<CreateTexture2D>(_device, 5)(_device, in desc, 0, out _staging));
                _width = roi.Width; _height = roi.Height;
            }
            var box = new TextureBox { Left = (uint)roi.X, Top = (uint)roi.Y, Front = 0,
                Right = (uint)(roi.X + roi.Width), Bottom = (uint)(roi.Y + roi.Height), Back = 1 };
            // Copy only subtitle pixels to a CPU-readable staging texture.
            Method<CopySubresourceRegion>(_context, 46)(_context, _staging, 0, 0, 0, 0, texture, 0, in box);
            Marshal.ThrowExceptionForHR(Method<Map>(_context, 14)(_context, _staging, 0, 1, 0, out var mapped));
            try
            {
                var stride = checked(roi.Width * 4);
                if (mapped.Data == 0 || mapped.RowPitch < stride) throw new InvalidOperationException("WGC 映射缓冲区无效。");
                var bytes = new byte[checked(stride * roi.Height)];
                for (var row = 0; row < roi.Height; row++)
                    Marshal.Copy(mapped.Data + checked((int)mapped.RowPitch * row), bytes, row * stride, stride);
                return bytes;
            }
            finally { Method<Unmap>(_context, 15)(_context, _staging, 0); }
        }
        finally
        {
            if (texture != 0) Marshal.Release(texture);
            if (access != 0) Marshal.Release(access);
            if (inspectable != 0) Marshal.Release(inspectable);
        }
    }

    private static T Method<T>(nint instance, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * nint.Size));

    public void Dispose()
    {
        if (_staging != 0) { Marshal.Release(_staging); _staging = 0; }
        if (_context != 0) { Marshal.Release(_context); _context = 0; }
        if (_device != 0) { Marshal.Release(_device); _device = 0; }
    }

    [StructLayout(LayoutKind.Sequential)] private struct TextureDescription
    {
        public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CpuAccessFlags, MiscFlags;
    }
    [StructLayout(LayoutKind.Sequential)] private struct TextureBox { public uint Left, Top, Front, Right, Bottom, Back; }
    [StructLayout(LayoutKind.Sequential)] private struct MappedResource { public nint Data; public uint RowPitch, DepthPitch; }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateForWindow(nint instance, nint hwnd, in Guid iid, out nint result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetInterface(nint instance, in Guid iid, out nint result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateTexture2D(nint instance, in TextureDescription description, nint initialData, out nint texture);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void CopySubresourceRegion(nint instance, nint dest, uint destSubresource, uint x, uint y, uint z, nint source, uint sourceSubresource, in TextureBox box);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Map(nint instance, nint resource, uint subresource, uint mapType, uint flags, out MappedResource mapped);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void Unmap(nint instance, nint resource, uint subresource);
    [DllImport("d3d11.dll")] private static extern int D3D11CreateDevice(nint adapter, uint driverType, nint software, uint flags, nint featureLevels, uint featureLevelsCount, uint sdkVersion, out nint device, out uint featureLevel, out nint context);
    [DllImport("d3d11.dll")] private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint device);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string source, int length, out nint value);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(nint value);
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(nint classId, in Guid iid, out nint factory);
}
