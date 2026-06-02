using System.Net;
using System.Text.Json.Nodes;
using benow_conversation.Configuration;
using benow_conversation.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace benow_conversation.Tests;

public class ModifierInjectorTests
{
    private static IOptions<AppSettings> CreateOptions(
        bool autoInject = true,
        string baseUrl = "https://openrouter.ai/api/v1",
        string apiKey = "test-key",
        string modifierModel = "test-model",
        int timeoutMs = 5000,
        string systemPrompt = "You are a modifier annotator.")
    {
        return Microsoft.Extensions.Options.Options.Create(new AppSettings
        {
            MultiCharacter = new MultiCharacterSettings
            {
                AutoInjectModifiers = autoInject,
                ModifierModel = modifierModel,
                ModifierTimeoutMs = timeoutMs,
                ModifierSystemPrompt = systemPrompt,
            },
            OpenRouter = new OpenRouterSettings
            {
                BaseUrl = baseUrl,
                ApiKey = apiKey
            }
        });
    }

    private static IHttpClientFactory CreateMockFactory(HttpResponseMessage response)
    {
        var handler = new MockHttpHandler(response);
        var client = new HttpClient(handler);
        return new MockHttpClientFactory(client);
    }

    [Fact]
    public async Task AutoInjectFalse_ReturnsOriginalText()
    {
        var factory = CreateMockFactory(new HttpResponseMessage(HttpStatusCode.OK));
        var injector = new ModifierInjector(factory, CreateOptions(autoInject: false), Mock.Of<ILogger<ModifierInjector>>());

        var result = await injector.InjectModifiersAsync("[Alice]Hello", CancellationToken.None);
        Assert.Equal("[Alice]Hello", result);
    }

    [Fact]
    public async Task SuccessfulInjection_CorrectRequestFormat()
    {
        var responseJson = @"{
            ""choices"": [{
                ""message"": {
                    ""role"": ""assistant"",
                    ""content"": ""[Alice](whisper)Hello there""
                }
            }]
        }";

        var handler = new CapturingMockHandler(
            new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(responseJson)
            });
        var client = new HttpClient(handler);
        var factory = new MockHttpClientFactory(client);

        var injector = new ModifierInjector(factory, CreateOptions(systemPrompt: "Annotate modifiers."), Mock.Of<ILogger<ModifierInjector>>());
        var result = await injector.InjectModifiersAsync("[Alice]Hello", CancellationToken.None);

        Assert.Equal("[Alice](whisper)Hello there", result);
        Assert.Single(handler.Requests);

        var req = handler.Requests[0];
        Assert.Equal("Bearer test-key", req.Headers.Authorization?.ToString());
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.EndsWith("/chat/completions", req.RequestUri?.ToString());

        var body = JsonNode.Parse(handler.RequestBodies[0]);
        Assert.NotNull(body);
        Assert.Equal("test-model", body["model"]?.ToString());
        Assert.Equal("Annotate modifiers.", body["messages"]?[0]?["content"]?.ToString());
        Assert.Equal("[Alice]Hello", body["messages"]?[1]?["content"]?.ToString());
    }

    [Fact]
    public async Task HttpFailure_ReturnsOriginalText()
    {
        var factory = CreateMockFactory(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var injector = new ModifierInjector(factory, CreateOptions(), Mock.Of<ILogger<ModifierInjector>>());
        var result = await injector.InjectModifiersAsync("[Alice]Hello", CancellationToken.None);

        Assert.Equal("[Alice]Hello", result);
    }

    private class MockHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public MockHttpClientFactory(HttpClient client) => _client = client;

        public HttpClient CreateClient(string name) => _client;
    }

    private class MockHttpHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;

        public MockHttpHandler(HttpResponseMessage response) => _response = response;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_response);
        }
    }

    private class CapturingMockHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;

        public List<HttpRequestMessage> Requests { get; } = new();
        public List<string> RequestBodies { get; } = new();

        public CapturingMockHandler(HttpResponseMessage response) => _response = response;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            RequestBodies.Add(request.Content != null ? await request.Content.ReadAsStringAsync(cancellationToken) : "");
            return _response;
        }
    }
}
