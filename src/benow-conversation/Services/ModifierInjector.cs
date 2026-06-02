using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using benow_conversation.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace benow_conversation.Services;

/// <summary>Injects speech modifier annotations into multi-character scripts by calling OpenRouter chat completions.</summary>
public class ModifierInjector : IModifierInjector
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AppSettings _settings;
    private readonly ILogger<ModifierInjector> _logger;

    public ModifierInjector(IHttpClientFactory httpClientFactory, IOptions<AppSettings> settings, ILogger<ModifierInjector> logger)
    {
        _httpClientFactory = httpClientFactory;
        _settings = settings.Value;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<string> InjectModifiersAsync(string text, CancellationToken ct)
    {
        if (!_settings.MultiCharacter.AutoInjectModifiers)
        {
            _logger.LogInformation("Modifier injection skipped (disabled)");
            return text;
        }

        var modelId = _settings.MultiCharacter.ModifierModel;
        if (string.IsNullOrEmpty(modelId))
        {
            _logger.LogWarning("No modifier model configured");
            return text;
        }

        var timeout = TimeSpan.FromMilliseconds(_settings.MultiCharacter.ModifierTimeoutMs);
        var inputMarkerCount = CountMarkers(text);

        return await TryModelAsync(modelId, text, inputMarkerCount, timeout, ct);
    }

    private async Task<string> TryModelAsync(string modelId, string text, int inputMarkerCount, TimeSpan timeout, CancellationToken ct)
    {
        HttpClient client;
        try
        {
            client = _httpClientFactory.CreateClient("ModifierInjector");
        }
        catch
        {
            client = _httpClientFactory.CreateClient("OpenRouter");
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            var sw = Stopwatch.StartNew();
            var requestBody = new JsonObject
            {
                ["model"] = modelId,
                ["messages"] = new JsonArray
                {
                    new JsonObject { ["role"] = "system", ["content"] = _settings.MultiCharacter.ModifierSystemPrompt },
                    new JsonObject { ["role"] = "user", ["content"] = text }
                }
            };

            var request = new HttpRequestMessage(HttpMethod.Post, _settings.OpenRouter.BaseUrl + "/chat/completions")
            {
                Content = new StringContent(requestBody.ToJsonString(), Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.OpenRouter.ApiKey);

            using var response = await client.SendAsync(request, cts.Token);
            response.EnsureSuccessStatusCode();

            var responseBody = await response.Content.ReadAsStringAsync(cts.Token);
            var responseNode = JsonNode.Parse(responseBody);
            var content = responseNode?["choices"]?[0]?["message"]?["content"]?.ToString();

            if (string.IsNullOrEmpty(content))
            {
                _logger.LogWarning("Modifier model {Model} returned empty content", modelId);
                return text;
            }

            if (CountMarkers(content) != inputMarkerCount)
            {
                _logger.LogWarning("Modifier model {Model} altered character markers ({Expected}→{Actual}), skipping", modelId, inputMarkerCount, CountMarkers(content));
                return text;
            }

            if (!ValidateTextIntegrity(text, content, modelId, "modifier"))
            {
                _logger.LogWarning("Modifier model {Model} response failed text integrity check — text may be truncated or modified", modelId);
                return text;
            }

            _logger.LogInformation("Modifier injection succeeded with model {Model} ({Ms}ms)", modelId, sw.ElapsedMilliseconds);
            return content;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Modifier model {Model} failed: {Error}", modelId, ex.Message);
            return text;
        }
    }

    private static bool ValidateTextIntegrity(string original, string result, string modelId, string stage)
    {
        if (string.IsNullOrEmpty(original) || string.IsNullOrEmpty(result))
            return false;

        var resultStripped = System.Text.RegularExpressions.Regex.Replace(result, @"\([^)]*\)", "");
        var originalAlpha = original.Count(char.IsLetterOrDigit);
        var resultAlpha = resultStripped.Count(char.IsLetterOrDigit);
        if (originalAlpha == 0) return false;

        var ratio = (double)resultAlpha / originalAlpha;
        if (ratio < 0.60)
            return false;

        if (ratio > 2.5)
            return false;

        var originalLen = original.Count(c => !char.IsWhiteSpace(c));
        var resultLen = resultStripped.Count(c => !char.IsWhiteSpace(c));
        if (originalLen == 0) return false;
        var lenRatio = (double)resultLen / originalLen;
        if (lenRatio < 0.50)
            return false;

        return true;
    }

    private static int CountMarkers(string text)
    {
        int count = 0;
        int idx = 0;
        while ((idx = text.IndexOf('[', idx)) >= 0)
        {
            var end = text.IndexOf(']', idx + 1);
            if (end < 0) break;
            count++;
            idx = end + 1;
        }
        return count;
    }
}
