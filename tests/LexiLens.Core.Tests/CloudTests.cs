using System.Net;
using System.Text.Json;
using LexiLens.Core;
using Xunit;

namespace LexiLens.Core.Tests;

public class CloudTests
{
    private const string ChatReply = """{"choices":[{"finish_reason":"stop","message":{"content":"找到古代遗物。"}}],"usage":{"total_tokens":45}}""";
    internal static AppSettings Settings(string id, string? model = null, bool consent = false, string endpoint = "") => new()
    {
        Provider = TranslationProvider.Cloud, CloudServiceId = id,
        CloudProfiles = new() { [id] = new CloudProfile { Model = model ?? ProviderCatalog.Get(id).Models.FirstOrDefault() ?? "test-model", CreditUseAcknowledged = consent, Endpoint = endpoint, AccountId = new string('a', 32) } }
    };

    [Theory]
    [InlineData("zhipu")]
    [InlineData("siliconflow")]
    [InlineData("modelscope")]
    [InlineData("groq")]
    [InlineData("openrouter")]
    [InlineData("cloudflare")]
    [InlineData("spark")]
    [InlineData("mistral")]
    [InlineData("cerebras")]
    [InlineData("huggingface")]
    [InlineData("dashscope")]
    [InlineData("doubao")]
    [InlineData("baidu")]
    [InlineData("tencent")]
    [InlineData("moonshot")]
    [InlineData("minimax")]
    [InlineData("stepfun")]
    [InlineData("deepseek")]
    [InlineData("openai")]
    [InlineData("sambanova")]
    [InlineData("together")]
    [InlineData("nvidia")]
    public async Task EveryChatPresetBuildsAnAuthenticatedRequestToItsOwnEndpoint(string id)
    {
        var settings = Settings(id, consent: true);
        var calls = 0;
        using var http = new HttpClient(new Handler(async (request, _) =>
        {
            calls++;
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("dummy-not-a-real-key", request.Headers.Authorization?.Parameter);
            Assert.EndsWith("/chat/completions", request.RequestUri!.AbsolutePath);
            Assert.Equal(ProviderCatalog.Endpoint(ProviderCatalog.Get(id), ProviderCatalog.Profile(settings)).Host, request.RequestUri.Host);
            Assert.DoesNotContain("dummy", request.RequestUri.AbsoluteUri);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal(ProviderCatalog.Profile(settings).Model, body.RootElement.GetProperty("model").GetString());
            Assert.False(body.RootElement.GetProperty("stream").GetBoolean());
            if (id == "zhipu") Assert.Equal("disabled", body.RootElement.GetProperty("thinking").GetProperty("type").GetString());
            if (id == "openrouter") Assert.Equal(0, body.RootElement.GetProperty("provider").GetProperty("max_price").GetProperty("prompt").GetInt32());
            return TranslationTests.Json(ChatReply);
        }));
        var client = new TranslationClient(http, settings, "dummy-not-a-real-key");
        Assert.Equal("找到古代遗物。", await client.TranslateAsync("Find the relic.", CancellationToken.None));
        Assert.Equal(1, calls);
        Assert.Equal(45, client.Cloud.ReportedTokens);
    }

    [Fact]
    public async Task GeminiUsesHeaderKeyNativeBodyAndIgnoresThinkingParts()
    {
        using var http = new HttpClient(new Handler(async (request, _) =>
        {
            Assert.Equal("dummy", request.Headers.GetValues("x-goog-api-key").Single());
            Assert.Null(request.Headers.Authorization);
            Assert.EndsWith(":generateContent", request.RequestUri!.AbsolutePath);
            Assert.Empty(request.RequestUri.Query);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal(JsonValueKind.Object, body.RootElement.GetProperty("systemInstruction").ValueKind);
            return TranslationTests.Json("""{"candidates":[{"finishReason":"STOP","content":{"parts":[{"thought":true,"text":"hidden reasoning"},{"text":"找到遗物。"}]}}],"usageMetadata":{"totalTokenCount":20}}""");
        }));
        var client = new TranslationClient(http, Settings("gemini"), "dummy");
        Assert.Equal("找到遗物。", await client.TranslateAsync("Find it.", CancellationToken.None));
        Assert.Equal(20, client.Cloud.ReportedTokens);
    }

    [Fact]
    public async Task AnthropicUsesMessagesAndSeparateSystemField()
    {
        using var http = new HttpClient(new Handler(async (request, _) =>
        {
            Assert.Equal("dummy", request.Headers.GetValues("x-api-key").Single());
            Assert.Equal("2023-06-01", request.Headers.GetValues("anthropic-version").Single());
            Assert.Null(request.Headers.Authorization);
            Assert.EndsWith("/messages", request.RequestUri!.AbsolutePath);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal(JsonValueKind.String, body.RootElement.GetProperty("system").ValueKind);
            Assert.Equal("user", body.RootElement.GetProperty("messages")[0].GetProperty("role").GetString());
            return TranslationTests.Json("""{"stop_reason":"end_turn","content":[{"type":"thinking","thinking":"hidden"},{"type":"text","text":"找到遗物。"}],"usage":{"input_tokens":10,"output_tokens":5}}""");
        }));
        var client = new TranslationClient(http, Settings("anthropic", consent: true), "dummy");
        Assert.Equal("找到遗物。", await client.TranslateAsync("Find it.", CancellationToken.None));
        Assert.Equal(15, client.Cloud.ReportedTokens);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(402)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(400)]
    public async Task PermanentAccountErrorsStopRequestsAndNeverEchoServerBodies(int status)
    {
        var calls = 0;
        using var http = new HttpClient(new Handler((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("SECRET-BODY-DO-NOT-DISPLAY") });
        }));
        var client = new TranslationClient(http, Settings("zhipu"), "dummy");
        var first = await Assert.ThrowsAsync<CloudApiException>(() => client.TranslateAsync("First.", CancellationToken.None));
        var second = await Assert.ThrowsAsync<CloudApiException>(() => client.TranslateAsync("Second.", CancellationToken.None));
        Assert.DoesNotContain("SECRET", first.Message + second.Message);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RateLimitRespectsCooldownWithoutSwitchingToAnotherService()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler((request, _) =>
        {
            calls++;
            Assert.Equal("open.bigmodel.cn", request.RequestUri!.Host);
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(60));
            return Task.FromResult(response);
        }));
        var client = new TranslationClient(http, Settings("zhipu"), "dummy");
        await Assert.ThrowsAsync<CloudApiException>(() => client.TranslateAsync("First.", CancellationToken.None));
        await Assert.ThrowsAsync<CloudApiException>(() => client.TranslateAsync("Second.", CancellationToken.None));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task PaidOrUnknownSelectionRequiresAcknowledgementBeforeSending()
    {
        using var http = new HttpClient(new Handler((_, _) => throw new Exception("Should never send")));
        var client = new TranslationClient(http, Settings("deepseek"), "dummy");
        await Assert.ThrowsAsync<ArgumentException>(() => client.TranslateAsync("text", CancellationToken.None));
        var manual = new TranslationClient(http, Settings("zhipu", "unverified-model"), "dummy");
        await Assert.ThrowsAsync<ArgumentException>(() => manual.TranslateAsync("text", CancellationToken.None));
    }

    [Fact]
    public async Task SharedHttpClientDoesNotLeakHeadersBetweenProviders()
    {
        using var http = new HttpClient(new Handler((request, _) =>
        {
            if (request.RequestUri!.Host.Contains("googleapis", StringComparison.Ordinal))
            {
                Assert.Equal("google-dummy", request.Headers.GetValues("x-goog-api-key").Single());
                Assert.Null(request.Headers.Authorization);
                return Task.FromResult(TranslationTests.Json("""{"candidates":[{"content":{"parts":[{"text":"好"}]}}]}"""));
            }
            Assert.False(request.Headers.Contains("x-goog-api-key"));
            Assert.Equal("zhipu-dummy", request.Headers.Authorization?.Parameter);
            return Task.FromResult(TranslationTests.Json(ChatReply));
        }));
        await new TranslationClient(http, Settings("gemini"), "google-dummy").TranslateAsync("a", CancellationToken.None);
        await new TranslationClient(http, Settings("zhipu"), "zhipu-dummy").TranslateAsync("b", CancellationToken.None);
        Assert.Null(http.DefaultRequestHeaders.Authorization);
        Assert.False(http.DefaultRequestHeaders.Contains("x-goog-api-key"));
    }

    [Fact]
    public async Task GeminiModelDiscoveryFollowsPagesAndFiltersNonTextModels()
    {
        var count = 0;
        using var http = new HttpClient(new Handler((request, _) =>
        {
            count++;
            Assert.Equal("dummy", request.Headers.GetValues("x-goog-api-key").Single());
            if (count == 1) return Task.FromResult(TranslationTests.Json("""{"models":[{"name":"models/text-a","supportedGenerationMethods":["generateContent"]},{"name":"models/embedding","supportedGenerationMethods":["embedContent"]}],"nextPageToken":"next"}"""));
            Assert.Contains("pageToken=next", request.RequestUri!.Query);
            return Task.FromResult(TranslationTests.Json("""{"models":[{"name":"models/text-b","supportedGenerationMethods":["generateContent"]}]}"""));
        }));
        var models = await new CloudTranslation(http, Settings("gemini"), "dummy").ListModelsAsync(CancellationToken.None);
        Assert.Equal(new[] { "text-a", "text-b" }, models);
    }

    [Fact]
    public void AllCatalogEntriesHaveDistinctIdsAndValidEndpointsAndLinks()
    {
        Assert.Equal(25, ProviderCatalog.All.Count);
        Assert.Equal(25, ProviderCatalog.All.Select(x => x.Id).Distinct().Count());
        foreach (var preset in ProviderCatalog.All)
        {
            Assert.Equal("https", new Uri(preset.DocsUrl).Scheme);
            Assert.Equal("https", new Uri(preset.KeyUrl).Scheme);
            if (preset.Id == "custom") continue;
            var profile = preset.DefaultProfile with { AccountId = new string('a', 32) };
            Assert.Equal("https", ProviderCatalog.Endpoint(preset, profile).Scheme);
        }
        Assert.Equal(AccessCategory.TrialOrCredits, ProviderCatalog.Get("cerebras").Access);
        Assert.Equal(AccessCategory.PaidOrUnknown, ProviderCatalog.Get("openai").Access);
    }

    [Theory]
    [InlineData("https://example.com/v1?key=secret")]
    [InlineData("https://user:secret@example.com/v1")]
    [InlineData("http://example.com/v1")]
    [InlineData("https://example.com/v1/chat/completions")]
    [InlineData("file:///C:/secret")]
    public void CustomEndpointsRejectCredentialsInUrlAndInsecureRemote(string endpoint) =>
        Assert.Throws<ArgumentException>(() => ProviderCatalog.Endpoint(ProviderCatalog.Get("custom"), new CloudProfile { Endpoint = endpoint }));

    [Fact]
    public async Task TruncatedReplyDoesNotEnterTranslationCache()
    {
        var count = 0;
        using var http = new HttpClient(new Handler((_, _) =>
        {
            count++;
            return Task.FromResult(TranslationTests.Json(count == 1 ? """{"choices":[{"finish_reason":"length","message":{"content":"half"}}]}""" : ChatReply));
        }));
        var client = new TranslationClient(http, Settings("zhipu"), "dummy");
        await Assert.ThrowsAsync<CloudApiException>(() => client.TranslateAsync("same text", CancellationToken.None));
        Assert.Equal("找到古代遗物。", await client.TranslateAsync("same text", CancellationToken.None));
        Assert.Equal(2, count);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handler(request, cancellationToken);
    }
}
