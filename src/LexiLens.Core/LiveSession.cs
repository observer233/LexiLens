using System.Threading.Channels;

namespace LexiLens.Core;

// OCR keeps running while a translation is in flight. A newer stable subtitle cancels
// the previous request, and its version also guards against uncooperative providers.
public sealed class LiveSession(ITextSource source, ITranslator translator, int intervalMs)
{
    private readonly object _sync = new();
    private CancellationTokenSource? _request;
    private long _version;
    public event Action<LiveUpdate>? Updated;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var channel = Channel.CreateBounded<(long Version, string Text)>(new BoundedChannelOptions(1)
        {
            SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest
        });
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var produce = ProduceAsync(channel.Writer, linked.Token);
        var consume = ConsumeAsync(channel.Reader, linked.Token);
        try
        {
            var first = await Task.WhenAny(produce, consume);
            await first;
        }
        finally
        {
            await linked.CancelAsync();
            channel.Writer.TryComplete();
            try { await Task.WhenAll(produce, consume); }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        }
    }

    private async Task ProduceAsync(ChannelWriter<(long, string)> writer, CancellationToken ct)
    {
        string previous = "", accepted = "";
        var repeats = 0;
        while (!ct.IsCancellationRequested)
        {
            var text = TextProcessing.Normalize(await source.ReadAsync(ct));
            repeats = text == previous ? repeats + 1 : 1;
            previous = text;
            // Two matching scans filter OCR flicker; three blank scans clear old subtitles.
            if (text != accepted && repeats >= (text.Length == 0 ? 3 : 2))
            {
                accepted = text;
                long version;
                lock (_sync)
                {
                    version = ++_version;
                    _request?.Cancel();
                }
                Updated?.Invoke(new LiveUpdate(text, IsBusy: text.Length > 0));
                writer.TryWrite((version, text));
            }
            await Task.Delay(Math.Clamp(intervalMs, 50, 5000), ct);
        }
    }

    private async Task ConsumeAsync(ChannelReader<(long Version, string Text)> reader, CancellationToken ct)
    {
        await foreach (var item in reader.ReadAllAsync(ct))
        {
            using var request = CancellationTokenSource.CreateLinkedTokenSource(ct);
            lock (_sync)
            {
                if (item.Version != _version) continue;
                _request = request;
            }
            try
            {
                var result = item.Text.Length == 0 ? "" : await translator.TranslateAsync(item.Text, request.Token);
                lock (_sync)
                {
                    if (!request.IsCancellationRequested && item.Version == _version)
                        Updated?.Invoke(new LiveUpdate(item.Text, result));
                }
            }
            catch (OperationCanceledException) when (request.IsCancellationRequested) { }
            catch (Exception ex)
            {
                lock (_sync)
                {
                    if (item.Version == _version && !ct.IsCancellationRequested)
                        Updated?.Invoke(new LiveUpdate(item.Text, Error: FriendlyError(ex)));
                }
            }
            finally
            {
                lock (_sync) { if (ReferenceEquals(_request, request)) _request = null; }
            }
        }
    }

    public static string FriendlyError(Exception ex) => ex switch
    {
        HttpRequestException => "无法连接翻译服务。请检查网络，或在设置中切换服务后重启翻译。",
        OperationCanceledException => "翻译请求超时。请检查服务，暂停后重新开始。",
        _ => ex.Message
    };
}
