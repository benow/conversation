using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using benow_conversation.Configuration;
using benow_conversation.Services.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace benow_conversation.Services.Stt;

public class LlmTextTransformer : ITextTransformer
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AppSettings _settings;
    private readonly ILogger<LlmTextTransformer> _logger;

    public LlmTextTransformer(
        IHttpClientFactory httpClientFactory,
        IOptions<AppSettings> settings,
        ILogger<LlmTextTransformer> logger)
    {
        _httpClientFactory = httpClientFactory;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<string> TransformAsync(string input, CancellationToken ct = default)
    {
        var baseUrl = !string.IsNullOrWhiteSpace(_settings.TranscriptCleanup.BaseUrl)
            ? _settings.TranscriptCleanup.BaseUrl
            : _settings.OpenRouter.BaseUrl;

        _logger.LogInformation("[Transformer] Cleaning up transcript: input={InputLength} chars, model={Model}, endpoint={Url}",
            input.Length, _settings.TranscriptCleanup.Model, baseUrl);

        var client = _httpClientFactory.CreateClient("OpenRouter");

        var requestBody = new
        {
            model = _settings.TranscriptCleanup.Model,
            messages = new[]
            {
                new { role = "system", content = _settings.TranscriptCleanup.SystemPrompt },
                new { role = "user", content = input }
            }
        };

        var json = JsonSerializer.Serialize(requestBody);
        var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/chat/completions")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        if (!string.IsNullOrWhiteSpace(_settings.OpenRouter.ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.OpenRouter.ApiKey);

        var sw = Stopwatch.StartNew();

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "[Transformer] Connection to cleanup endpoint failed: {Error}", ex.Message);
            _logger.LogWarning("[Transformer] Returning raw transcript due to connection failure");
            return input;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogError("[Transformer] Cleanup request timed out");
            _logger.LogWarning("[Transformer] Returning raw transcript due to timeout");
            return input;
        }

        _logger.LogInformation("[Transformer] Cleanup response: HTTP {Status} in {ElapsedMs}ms", (int)response.StatusCode, sw.ElapsedMilliseconds);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("[Transformer] Cleanup failed (HTTP {Status}): {Error} — returning raw transcript", (int)response.StatusCode, errorBody.Length > 300 ? errorBody[..300] : errorBody);
            return input;
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct);

        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            var cleaned = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? input;

            _logger.LogInformation("[Transformer] Cleanup complete: {InputLength} → {OutputLength} chars in {ElapsedMs}ms", input.Length, cleaned.Length, sw.ElapsedMilliseconds);
            return cleaned;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Transformer] Failed to parse cleanup response — returning raw transcript");
            return input;
        }
    }
}
