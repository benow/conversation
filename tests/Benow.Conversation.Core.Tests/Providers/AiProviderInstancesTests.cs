using Benow.Conversation.Providers;
using Xunit;

namespace Benow.Conversation.Core.Tests.Providers;

/// <summary>
/// Catalog resolution semantics ported from NASTV: legacy synthesis, type-id fallback,
/// key/base-URL resolution through provider field types.
/// </summary>
public class AiProviderInstancesTests
{
    private sealed class TestConfig : ILegacyProviderConfig
    {
        public string AiProviders { get; set; } = "";
        public string OpenRouterApiKey { get; set; } = "";
        public string OpenAiCompatUrl { get; set; } = "";
        public string OpenAiCompatApiKey { get; set; } = "";
        public string ReplicateApiKey { get; set; } = "";
        public string GroqApiKey { get; set; } = "";
    }

    [Fact]
    public void ParseSerialize_RoundTripsCamelCase()
    {
        // camelCase wire JSON (as stored by NASTV plugin_settings / frontend writes).
        var json = """[{"id":"abc12345","name":"My OpenRouter","typeId":"openrouter","params":{"OpenRouterApiKey":"sk-1"}}]""";
        var parsed = AiProviderInstances.Parse(json);
        var inst = Assert.Single(parsed);
        Assert.Equal("abc12345", inst.Id);
        Assert.Equal("openrouter", inst.TypeId);
        Assert.Equal("sk-1", inst.Params["OpenRouterApiKey"]);
        Assert.Contains("\"typeId\":\"openrouter\"", AiProviderInstances.Serialize(parsed));
    }

    [Fact]
    public void ResolveAll_EmptyCatalog_SynthesizesLegacyInstances_OnlyConfiguredOnes()
    {
        var cfg = new TestConfig { OpenRouterApiKey = "sk-or", GroqApiKey = "gsk_1" };
        var all = AiProviderInstances.ResolveAll(cfg);
        // Only the two providers with configured values — no ghost openai-compatible/replicate.
        Assert.Equal(2, all.Count);
        Assert.Contains(all, i => i.TypeId == "openrouter" && i.Params["OpenRouterApiKey"] == "sk-or");
        Assert.Contains(all, i => i.TypeId == "groq" && i.Params["GroqApiKey"] == "gsk_1");
    }

    [Fact]
    public void Find_LegacyTypeId_ResolvesRealInstanceWithKey()
    {
        // Root cause of the 2026-08-19 TTS "No API key" outage: a legacy type id stored in
        // TtsProvider must resolve to the catalog instance of that type (which holds the key).
        var cfg = new TestConfig
        {
            AiProviders = """[{"id":"xyz98765","name":"Replicate","typeId":"replicate","params":{"ReplicateApiKey":"rkey"}}]"""
        };
        var found = AiProviderInstances.Find(cfg, "replicate");
        Assert.NotNull(found);
        Assert.Equal("xyz98765", found!.Id);
        Assert.Equal("rkey", AiProviderInstances.ResolveApiKey(cfg, found));
    }

    [Fact]
    public void ResolveBaseUrl_OpenAiCompatible_UsesInstanceUrlField()
    {
        var cfg = new TestConfig
        {
            AiProviders = """[{"id":"l","name":"Ollama","typeId":"openai-compatible","params":{"OpenAiCompatUrl":"http://127.0.0.1:11434/v1/","OpenAiCompatApiKey":"ollama"}}]"""
        };
        var found = AiProviderInstances.Find(cfg, "l");
        Assert.Equal("http://127.0.0.1:11434/v1/", AiProviderInstances.ResolveBaseUrl(cfg, found!));
    }

    [Fact]
    public void ResolveBaseUrl_KnownProvider_UsesBuiltInDefault()
    {
        var cfg = new TestConfig { OpenRouterApiKey = "k" };
        var all = AiProviderInstances.ResolveAll(cfg);
        var or = all.Single(i => i.TypeId == "openrouter");
        Assert.Equal("https://openrouter.ai/api/v1/", AiProviderInstances.ResolveBaseUrl(cfg, or));
    }

    [Fact]
    public void CapabilityNames_ListsSetFlags()
    {
        Assert.Equal(new[] { "Embedding", "Tts", "Llm" }, AiProviderInstances.CapabilityNames(AiCapability.Embedding | AiCapability.Llm | AiCapability.Tts));
    }

    [Fact]
    public void Registry_FourProviders_CapabilityFilters()
    {
        Assert.Equal(4, AiProviderRegistry.All.Count);
        Assert.Contains(AiProviderRegistry.ForCapability(AiCapability.Stt), p => p.Id == "groq");
        Assert.Contains(AiProviderRegistry.ForCapability(AiCapability.Tts), p => p.Id == "replicate");
        Assert.Contains(AiProviderRegistry.ForCapability(AiCapability.Llm), p => p.Id == "openai-compatible");
    }
}
