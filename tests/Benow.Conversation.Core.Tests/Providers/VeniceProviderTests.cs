using System.Net;
using System.Text;
using Benow.Conversation.Providers;
using Xunit;

namespace Benow.Conversation.Core.Tests.Providers;

public class VeniceProviderTests
{
    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }

    [Fact]
    public void Config_ExposesLlmAndApiKeyField()
    {
        var provider = new VeniceProvider();

        Assert.Equal("venice", provider.Id);
        Assert.Equal("Venice AI", provider.DisplayName);
        Assert.Equal(AiCapability.Llm, provider.Capabilities);
        Assert.Equal("https://api.venice.ai/api/v1/", provider.GetBaseUrl());
        var field = Assert.Single(provider.GetConfigFields());
        Assert.Equal("VeniceApiKey", field.Key);
        Assert.Equal("password", field.Type);
    }

    [Fact]
    public async Task GetModels_FiltersToTextAndMapsToolCalling()
    {
        const string json = """
        {
          "data": [
            {
              "id": "venice-uncensored-1-2",
              "type": "text",
              "model_spec": {
                "name": "Venice Uncensored 1.2",
                "uncensored": true,
                "capabilities": { "supportsFunctionCalling": true }
              }
            },
            {
              "id": "text-no-tools",
              "type": "text",
              "model_spec": {
                "name": "Plain Text",
                "capabilities": { "supportsFunctionCalling": false }
              }
            },
            {
              "id": "some-image-model",
              "type": "image",
              "model_spec": { "name": "Image Model" }
            }
          ]
        }
        """;

        var provider = new VeniceProvider();
        using var http = new HttpClient(new StubHandler(json));
        var models = await provider.GetModelsAsync("test-key", null, http, CancellationToken.None);

        Assert.Equal(2, models.Count);
        Assert.DoesNotContain(models, m => m.Id == "some-image-model");

        var uncensored = models.Single(m => m.Id == "venice-uncensored-1-2");
        Assert.Equal("Venice Uncensored 1.2 — uncensored", uncensored.DisplayName);
        Assert.True(uncensored.ToolCalling);

        var plain = models.Single(m => m.Id == "text-no-tools");
        Assert.Equal("Plain Text", plain.DisplayName);
        Assert.False(plain.ToolCalling);
    }

    [Fact]
    public async Task GetModels_NoKey_ReturnsEmpty()
    {
        var provider = new VeniceProvider();
        using var http = new HttpClient(new StubHandler("{}"));

        var models = await provider.GetModelsAsync("", null, http, CancellationToken.None);

        Assert.Empty(models);
    }
}
