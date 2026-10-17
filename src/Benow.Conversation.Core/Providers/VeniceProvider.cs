using System.Net.Http.Headers;
using System.Text.Json;

namespace Benow.Conversation.Providers;

/// <summary>
/// Venice AI (OpenAI-compatible) — Llm capability only for now. Venice's native API is
/// private by default for the uncensored models and supports vision + function calling.
/// The model catalog is public; chat requires an Inference Only key.
/// </summary>
public class VeniceProvider : IAiProvider
{
    public string Id => "venice";
    public string DisplayName => "Venice AI";
    public AiCapability Capabilities => AiCapability.Llm;
    public string GetBaseUrl() => "https://api.venice.ai/api/v1/";

    public ProviderConfigField[] GetConfigFields() =>
    [
        new ProviderConfigField
        {
            Key = "VeniceApiKey",
            Type = "password",
            Label = "API Key",
            Description = "Venice AI Inference API key from https://venice.ai/settings/api"
        }
    ];

    public async Task<IReadOnlyList<ModelInfo>> GetModelsAsync(string apiKey, string? baseUrl, HttpClient http, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return [];

        var root = string.IsNullOrWhiteSpace(baseUrl) ? GetBaseUrl() : baseUrl;
        var url = root.TrimEnd('/') + "/models";

        try
        {
            // Per-request Authorization avoids the shared-HttpClient header accumulation
            // documented as ai-plugin/provider-httpclient-auth-accumulation.
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return [];

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return [];

            var models = new List<ModelInfo>();
            foreach (var model in data.EnumerateArray())
            {
                // The catalog mixes text, image, audio and embedding models. The Llm
                // dropdown must only see text models.
                if (model.TryGetProperty("type", out var type) && type.GetString() != "text")
                    continue;

                var id = model.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
                if (string.IsNullOrWhiteSpace(id)) continue;

                string? name = null;
                bool? toolCalling = null;
                var uncensored = false;

                if (model.TryGetProperty("model_spec", out var spec))
                {
                    if (spec.TryGetProperty("name", out var nameElement))
                        name = nameElement.GetString();

                    if (spec.TryGetProperty("uncensored", out var uncensoredElement) &&
                        uncensoredElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        uncensored = uncensoredElement.GetBoolean();

                    if (spec.TryGetProperty("capabilities", out var capabilities) &&
                        capabilities.TryGetProperty("supportsFunctionCalling", out var functionCalling) &&
                        functionCalling.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        toolCalling = functionCalling.GetBoolean();
                }

                var displayName = string.IsNullOrWhiteSpace(name) ? id! : name!;
                if (uncensored) displayName += " — uncensored";
                models.Add(new ModelInfo(id!, displayName, toolCalling));
            }

            return models
                .OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return [];
        }
    }
}
