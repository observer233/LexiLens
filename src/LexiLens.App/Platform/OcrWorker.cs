using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using LexiLens.Core;

namespace LexiLens.App.Platform;

// Isolate native inference: a stuck native call must not freeze stop/exit/fallback.
internal sealed class OcrWorker(bool stallForDiagnostics = false, int initialTimeoutMs = 15000) : IDisposable
{
    private NamedPipeServerStream? _pipe;
    private Process? _process;
    private StreamReader? _reader;
    internal bool WasTerminated { get; private set; }
    internal bool WasConnected { get; private set; }

    internal async Task<Recognition> ReadAsync(byte[] bytes, int width, int height, OcrImageMode mode, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_process == null ? TimeSpan.FromMilliseconds(initialTimeoutMs) : TimeSpan.FromSeconds(8));
        try
        {
            if (_process == null) await StartAsync(timeout.Token);
            using var header = new MemoryStream();
            using (var writer = new BinaryWriter(header, Encoding.UTF8, leaveOpen: true))
            { writer.Write(bytes.Length); writer.Write(width); writer.Write(height); writer.Write((int)mode); }
            await _pipe!.WriteAsync(header.ToArray(), timeout.Token);
            await _pipe.WriteAsync(bytes, timeout.Token);
            await _pipe.FlushAsync(timeout.Token);
            var line = await _reader!.ReadLineAsync(timeout.Token);
            if (line == null || line.Length > 200_000) throw new IOException("OCR 子进程未返回结果。");
            var reply = JsonSerializer.Deserialize<WorkerReply>(line) ?? throw new IOException("OCR 子进程响应格式错误。");
            if (reply.Result == null) throw new InvalidOperationException("OCR 子进程识别失败（" + reply.Error + "）。");
            return reply.Result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { Dispose(); throw new TimeoutException("增强识别超时，已终止 OCR 子进程。"); }
        catch { Dispose(); throw; }
    }

    private async Task StartAsync(CancellationToken ct)
    {
        var name = "LexiLens-OCR-" + Guid.NewGuid().ToString("N");
        _pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var executable = Environment.ProcessPath ?? throw new IOException("无法定位当前程序。");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);
        start.ArgumentList.Add("--ocr-worker"); start.ArgumentList.Add(name);
        if (stallForDiagnostics) start.ArgumentList.Add("--diagnostic-stall");
        _process = Process.Start(start) ?? throw new IOException("无法启动 OCR 子进程。");
        // Drain native diagnostics so a full stderr pipe can never deadlock OCR.
        _process.OutputDataReceived += (_, _) => { }; _process.ErrorDataReceived += (_, _) => { };
        _process.BeginOutputReadLine(); _process.BeginErrorReadLine();
        await _pipe.WaitForConnectionAsync(ct);
        WasConnected = true;
        _reader = new StreamReader(_pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
    }

    internal static void Run(string name, bool stallForDiagnostics = false)
    {
        if (!name.StartsWith("LexiLens-OCR-", StringComparison.Ordinal) || name.Length > 80) return;
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.None);
        pipe.Connect(10_000);
        if (stallForDiagnostics) Thread.Sleep(Timeout.Infinite);
        using var input = new BinaryReader(pipe, Encoding.UTF8, leaveOpen: true);
        using var output = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        using var ocr = new LocalOcr();
        while (pipe.IsConnected)
        {
            int length;
            try { length = input.ReadInt32(); } catch (EndOfStreamException) { break; }
            var width = input.ReadInt32(); var height = input.ReadInt32(); var mode = (OcrImageMode)input.ReadInt32();
            if (width is < 1 or > 2560 || height is < 1 or > 2560 || length != checked(width * height * 4) || !Enum.IsDefined(mode)) return;
            var bytes = input.ReadBytes(length); if (bytes.Length != length) return;
            try { output.WriteLine(JsonSerializer.Serialize(new WorkerReply(ocr.Recognize(bytes, width, height, mode, CancellationToken.None), null))); }
            catch (Exception ex) { output.WriteLine(JsonSerializer.Serialize(new WorkerReply(null, ex.GetType().Name))); }
        }
    }

    public void Dispose()
    {
        if (_process != null)
        {
            try { if (!_process.HasExited) { _process.Kill(entireProcessTree: true); WasTerminated = true; } } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
            _process.Dispose(); _process = null;
        }
        _reader?.Dispose(); _reader = null; _pipe?.Dispose(); _pipe = null;
    }
    private sealed record WorkerReply(Recognition? Result, string? Error);
}
