using System.Net;
using System.Text;
using LexiLens.Core;
using Xunit;

namespace LexiLens.Core.Tests;

public class TranslationTests
{
    [Fact]
    public void Utf8ChunksPreserveEmojiAndStayWithinServiceLimit()
    {
        var text = string.Concat(Enumerable.Repeat("Ancient relic 遗物 🗡️ ", 100));
        var chunks = TextProcessing.SplitUtf8(text);
        Assert.Equal(text, string.Concat(chunks));
        Assert.All(chunks, x => Assert.InRange(Encoding.UTF8.GetByteCount(x), 1, 480));
    }

    [Fact]
    public async Task MyMemorySplitsLongRequestsAndCachesRepeatedText()
    {
        var requests = new List<Uri>();
        using var http = new HttpClient(new StubHandler(request =>
        {
            requests.Add(request.RequestUri!);
            return Json("""{"responseStatus":200,"responseData":{"translatedText":"遗物 &amp; 草药"}}""");
        }));
        var translator = new TranslationClient(http, new AppSettings());
        var text = new string('a', 960);
        var first = await translator.TranslateAsync(text, CancellationToken.None);
        var second = await translator.TranslateAsync(text, CancellationToken.None);
        Assert.Equal("遗物 & 草药 遗物 & 草药", first);
        Assert.Equal(first, second);
        Assert.Equal(2, requests.Count);
        Assert.All(requests, u => Assert.Equal("api.mymemory.translated.net", u.Host));
    }

    [Fact]
    public async Task QuotaErrorsNeverBecomeSuccessfulTranslations()
    {
        using var http = new HttpClient(new StubHandler(_ => Json("""{"responseStatus":429,"responseData":{"translatedText":"QUOTA EXCEEDED"}}""")));
        var translator = new TranslationClient(http, new AppSettings());
        await Assert.ThrowsAsync<InvalidOperationException>(() => translator.TranslateAsync("relic", CancellationToken.None));
    }

    [Theory]
    [InlineData("http://example.com:11434")]
    [InlineData("file:///C:/data")]
    [InlineData("https://user:secret@example.com")]
    [InlineData("https://example.com?key=secret")]
    public void EndpointValidationRejectsInsecureRemoteOrMalformedAddresses(string endpoint) =>
        Assert.Throws<ArgumentException>(() => TranslationClient.ValidateOllamaEndpoint(endpoint));

    [Theory]
    [InlineData("http://localhost:11434")]
    [InlineData("http://127.0.0.1:11434")]
    [InlineData("https://example.com/ollama/")]
    public void EndpointValidationAcceptsLocalOrHttps(string endpoint) =>
        Assert.NotNull(TranslationClient.ValidateOllamaEndpoint(endpoint));

    [Fact]
    public async Task OllamaUsesNonStreamingChatAndSelectedModel()
    {
        string? body = null; Uri? url = null;
        using var http = new HttpClient(new AsyncStubHandler(async request =>
        {
            body = await request.Content!.ReadAsStringAsync(); url = request.RequestUri;
            return Json("""{"message":{"content":"找到遗物。"}}""");
        }));
        var translator = new TranslationClient(http, new AppSettings { Provider = TranslationProvider.Ollama, OllamaModel = "my-model" });
        Assert.Equal("找到遗物。", await translator.TranslateAsync("Find the relic.", CancellationToken.None));
        Assert.Equal("/api/chat", url!.AbsolutePath);
        Assert.Contains("\"stream\":false", body);
        Assert.Contains("my-model", body);
    }

    internal static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(handler(request));
    }
    private sealed class AsyncStubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handler(request);
    }
}
