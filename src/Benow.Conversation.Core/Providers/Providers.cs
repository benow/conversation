using System.Net.Http.Json;

namespace Benow.Conversation.Providers;

// Ports of the four NASTV provider type implementations (nastv-ai-plugin/Providers/*,
// 2026-09-17 phase 1). Behavior kept verbatim — including the shared-HttpClient
// Authorization-header wart (finding ai-plugin/provider-httpclient-auth-accumulation);
// fix at phase-4 adoption, not here, so the port stays diffable against NASTV.

/// <summary>
/// Groq provider — used for STT via Whisper.
/// </summary>
public class GroqProvider : IAiProvider
{
    public string Id => "groq";
    public string DisplayName => "Groq";
    public AiCapability Capabilities => AiCapability.Stt;
    public string GetBaseUrl() => "";

    public ProviderConfigField[] GetConfigFields() =>
    [
        new ProviderConfigField
        {
            Key = "GroqApiKey",
            Type = "password",
            Label = "API Key",
            Description = "Groq API key from https://console.groq.com/keys"
        }
    ];

    public async Task<IReadOnlyList<ModelInfo>> GetModelsAsync(string apiKey, string? baseUrl, HttpClient http, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return [];

        try
        {
            http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
            var response = await http.GetAsync("https://api.groq.com/openai/v1/models", ct);
            if (!response.IsSuccessStatusCode) return [];

            var result = await response.Content.ReadFromJsonAsync<GroqModelsResponse>(cancellationToken: ct);
            if (result?.Data is not { Length: > 0 } models) return [];

            return models
                .Where(m => m.Id != null)
                .Select(m => new ModelInfo(m.Id!, m.Id!, ToolCalling: true))
                .OrderBy(m => m.DisplayName)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private class GroqModelsResponse
    {
        public ModelEntry[]? Data { get; set; }
    }

    private class ModelEntry
    {
        public string? Id { get; set; }
    }
}

public class OpenRouterProvider : IAiProvider
{
    public string Id => "openrouter";
    public string DisplayName => "OpenRouter";
    public AiCapability Capabilities => AiCapability.Embedding | AiCapability.Tts | AiCapability.Stt | AiCapability.Llm;
    public string GetBaseUrl() => "https://openrouter.ai/api/v1/";

    public ProviderConfigField[] GetConfigFields() =>
    [
        new ProviderConfigField
        {
            Key = "OpenRouterApiKey",
            Type = "password",
            Label = "API Key",
            Description = "OpenRouter API key from https://openrouter.ai/keys"
        }
    ];

    public async Task<IReadOnlyList<ModelInfo>> GetModelsAsync(string apiKey, string? baseUrl, HttpClient http, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return [];

        try
        {
            http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
            var response = await http.GetAsync("https://openrouter.ai/api/v1/models", ct);
            if (!response.IsSuccessStatusCode) return [];

            var result = await response.Content.ReadFromJsonAsync<OpenRouterModelsResponse>(cancellationToken: ct);
            if (result?.Data is not { Length: > 0 } models) return [];

            return models
                .Select(m =>
                {
                    bool? toolCalling = null;
                    if (m.SupportedParameters is { Length: > 0 })
                        toolCalling = m.SupportedParameters.Contains("tools");
                    return new ModelInfo(m.Id ?? "", m.Name ?? m.Id ?? "", toolCalling);
                })
                .OrderBy(m => m.DisplayName)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private class OpenRouterModelsResponse
    {
        public ModelEntry[]? Data { get; set; }
    }

    private class ModelEntry
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("supported_parameters")]
        public string[]? SupportedParameters { get; set; }
    }
}

/// <summary>
/// Replicate provider — used for TTS voice cloning via xtts-v2.
/// No model listing API (models are version hashes).
/// </summary>
public class ReplicateProvider : IAiProvider
{
    public string Id => "replicate";
    public string DisplayName => "Replicate";
    public AiCapability Capabilities => AiCapability.Tts;
    public string GetBaseUrl() => "";

    public ProviderConfigField[] GetConfigFields() =>
    [
        new ProviderConfigField
        {
            Key = "ReplicateApiKey",
            Type = "password",
            Label = "API Token",
            Description = "Replicate API token from https://replicate.com/account/api-tokens"
        }
    ];

    public Task<IReadOnlyList<ModelInfo>> GetModelsAsync(string apiKey, string? baseUrl, HttpClient http, CancellationToken ct)
    {
        // Replicate has no model-listing API (models are pinned version hashes), but the
        // TTS model dropdown must offer real choices — an empty list forced manual typing,
        // which is how the version hash got lost (2026-08-20: bare "lucataco/xtts-v2" 400'd
        // until the plugin learned the versionless endpoint). The versioned id below is the
        // canonical XTTS v2 build (same hash the manifest default ships) — selecting it
        // persists the FULL versioned id, so synthesis always pins a working build.
        return Task.FromResult<IReadOnlyList<ModelInfo>>(new[]
        {
            new ModelInfo(
                "lucataco/xtts-v2:684bc3855b37866c0c65add2ff39c78f3dea3f4ff103a436465326e0f438d55e",
                "XTTS v2 — voice cloning")
        });
    }
}

/// <summary>
/// OpenAI-compatible provider — user supplies the base URL.
/// Used for self-hosted or third-party OpenAI-compatible APIs (LM Studio, vLLM, Ollama, etc.)
/// </summary>
public class OpenAiCompatibleProvider : IAiProvider
{
    public string Id => "openai-compatible";
    public string DisplayName => "OpenAI Compatible";
    public AiCapability Capabilities => AiCapability.Embedding | AiCapability.Llm | AiCapability.Tts;
    public string GetBaseUrl() => ""; // user-supplied

    public ProviderConfigField[] GetConfigFields() =>
    [
        new ProviderConfigField
        {
            Key = "OpenAiCompatUrl",
            Type = "string",
            Label = "Base URL",
            Placeholder = "https://api.openai.com/v1/",
            Description = "Base URL of the OpenAI-compatible API (e.g. https://api.openai.com/v1/)"
        },
        new ProviderConfigField
        {
            Key = "OpenAiCompatApiKey",
            Type = "password",
            Label = "API Key",
            Description = "API key for the OpenAI-compatible endpoint"
        }
    ];

    public async Task<IReadOnlyList<ModelInfo>> GetModelsAsync(string apiKey, string? baseUrl, HttpClient http, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(baseUrl)) return [];

        try
        {
            var url = baseUrl.TrimEnd('/') + "/models";
            http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
            var response = await http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return [];

            var result = await response.Content.ReadFromJsonAsync<OaiModelsResponse>(cancellationToken: ct);
            if (result?.Data is not { Length: > 0 } models) return [];

            return models
                .Where(m => m.Id != null)
                .Select(m => new ModelInfo(m.Id!, m.Id!, ToolCalling: null))
                .OrderBy(m => m.DisplayName)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private class OaiModelsResponse
    {
        public ModelEntry[]? Data { get; set; }
    }

    private class ModelEntry
    {
        public string? Id { get; set; }
    }
}
