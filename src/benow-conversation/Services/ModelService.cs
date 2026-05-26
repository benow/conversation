using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using benow_conversation.Configuration;
using benow_conversation.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace benow_conversation.Services;

public interface IModelService
{
    Task<List<TtsModelInfo>> GetTtsModelsAsync();
    Task<List<VoiceInfo>> GetVoicesForModelAsync(string? modelId = null);
}

public class ModelService : IModelService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AppSettings _settings;
    private readonly ILogger<ModelService> _logger;

    public ModelService(
        IHttpClientFactory httpClientFactory,
        IOptions<AppSettings> settings,
        ILogger<ModelService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<List<TtsModelInfo>> GetTtsModelsAsync()
    {
        _logger.LogInformation("Fetching TTS models from OpenRouter");

        var client = _httpClientFactory.CreateClient("OpenRouter");
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _settings.OpenRouter.ApiKey);

        var response = await client.GetAsync($"{_settings.OpenRouter.BaseUrl}/models?output_modalities=speech");
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var models = new List<TtsModelInfo>();
        if (root.TryGetProperty("data", out var data))
        {
            foreach (var item in data.EnumerateArray())
            {
                var model = new TtsModelInfo
                {
                    Id = item.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "",
                    Name = item.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "",
                    Description = item.TryGetProperty("description", out var descEl) ? descEl.GetString() ?? "" : "",
                    ContextLength = item.TryGetProperty("context_length", out var ctxEl) ? ctxEl.GetInt32() : 0
                };

                if (item.TryGetProperty("pricing", out var pricing))
                {
                    if (pricing.TryGetProperty("prompt", out var promptPrice))
                    {
                        var priceStr = promptPrice.ValueKind == JsonValueKind.String
                            ? promptPrice.GetString() ?? "0"
                            : promptPrice.GetRawText();
                        model.PromptPricePerMillionChars = double.TryParse(priceStr, out var p) ? p * 1_000_000 : 0;
                    }

                    if (pricing.TryGetProperty("completion", out var completionPrice))
                    {
                        var priceStr = completionPrice.ValueKind == JsonValueKind.String
                            ? completionPrice.GetString() ?? "0"
                            : completionPrice.GetRawText();
                        model.CompletionPricePerMillionChars = double.TryParse(priceStr, out var c) ? c * 1_000_000 : 0;
                    }
                }

                if (item.TryGetProperty("supported_voices", out var voices))
                {
                    model.VoiceCount = voices.GetArrayLength();
                }

                models.Add(model);
            }
        }

        _logger.LogInformation("Found {Count} TTS models", models.Count);
        return models;
    }

    public async Task<List<VoiceInfo>> GetVoicesForModelAsync(string? modelId = null)
    {
        var model = modelId ?? _settings.OpenRouter.TtsModel;
        _logger.LogInformation("Fetching voices for model {Model}", model);

        var client = _httpClientFactory.CreateClient("OpenRouter");
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _settings.OpenRouter.ApiKey);

        var response = await client.GetAsync($"{_settings.OpenRouter.BaseUrl}/models?output_modalities=speech");
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        var voices = new List<VoiceInfo>();
        if (doc.RootElement.TryGetProperty("data", out var data))
        {
            foreach (var item in data.EnumerateArray())
            {
                var id = item.TryGetProperty("id", out var idEl) ? idEl.GetString() : "";
                if (id != model) continue;

                if (item.TryGetProperty("supported_voices", out var voicesEl))
                {
                    foreach (var voice in voicesEl.EnumerateArray())
                    {
                        var voiceId = voice.GetString() ?? "";
                        voices.Add(new VoiceInfo
                        {
                            Id = voiceId,
                            Description = InferVoiceDescription(voiceId)
                        });
                    }
                }

                break;
            }
        }

        _logger.LogInformation("Found {Count} voices for model {Model}", voices.Count, model);
        return voices;
    }

    public static string InferVoiceDescription(string voiceId)
    {
        if (string.IsNullOrEmpty(voiceId)) return "";

        if (voiceId.StartsWith("af_")) return $"American female ({voiceId})";
        if (voiceId.StartsWith("am_")) return $"American male ({voiceId})";
        if (voiceId.StartsWith("bf_")) return $"British female ({voiceId})";
        if (voiceId.StartsWith("bm_")) return $"British male ({voiceId})";
        if (voiceId.StartsWith("ef_")) return $"English female ({voiceId})";
        if (voiceId.StartsWith("em_")) return $"English male ({voiceId})";
        if (voiceId.StartsWith("ff_")) return $"French female ({voiceId})";
        if (voiceId.StartsWith("fm_")) return $"French male ({voiceId})";
        if (voiceId.StartsWith("hf_")) return $"Hindi female ({voiceId})";
        if (voiceId.StartsWith("hm_")) return $"Hindi male ({voiceId})";
        if (voiceId.StartsWith("if_")) return $"Italian female ({voiceId})";
        if (voiceId.StartsWith("im_")) return $"Italian male ({voiceId})";
        if (voiceId.StartsWith("jf_")) return $"Japanese female ({voiceId})";
        if (voiceId.StartsWith("jm_")) return $"Japanese male ({voiceId})";
        if (voiceId.StartsWith("pf_")) return $"Portuguese female ({voiceId})";
        if (voiceId.StartsWith("pm_")) return $"Portuguese male ({voiceId})";
        if (voiceId.StartsWith("zf_")) return $"Chinese female ({voiceId})";
        if (voiceId.StartsWith("zm_")) return $"Chinese male ({voiceId})";

        if (voiceId.StartsWith("en_paul_"))
            return $"English Paul, {voiceId["en_paul_".Length..]}";
        if (voiceId.StartsWith("gb_oliver_"))
            return $"British Oliver, {voiceId["gb_oliver_".Length..]}";
        if (voiceId.StartsWith("gb_jane_"))
            return $"British Jane, {voiceId["gb_jane_".Length..]}";
        if (voiceId.StartsWith("fr_marie_"))
            return $"French Marie, {voiceId["fr_marie_".Length..]}";

        if (voiceId.StartsWith("american_female")) return "American female";
        if (voiceId.StartsWith("american_male")) return "American male";
        if (voiceId.StartsWith("british_female")) return "British female";
        if (voiceId.StartsWith("british_male")) return "British male";

        if (voiceId.StartsWith("conversational_") || voiceId.StartsWith("read_speech_"))
            return voiceId.Replace('_', ' ');

        return "";
    }
}
