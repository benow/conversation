using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using benow_conversation.Configuration;
using benow_conversation.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace benow_conversation.Services;

public class ReplicateTtsProvider : ITtsProvider
{
    private readonly HttpClient _http;
    private readonly ReplicateSettings _settings;
    private readonly IPersonaAllocator _personaAllocator;
    private readonly ILogger<ReplicateTtsProvider> _logger;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly SemaphoreSlim _predictThrottle = new(1, 1);
    private DateTime _lastPrediction = DateTime.MinValue;
    private static readonly TimeSpan MinPredictionGap = TimeSpan.FromMilliseconds(600);

    /// <summary>Replicate xtts-v2 returns WAV at 24000Hz mono 16-bit.</summary>
    public AudioFormat OutputFormat => AudioFormat.Wav24000Mono;

    public ReplicateTtsProvider(IHttpClientFactory httpClientFactory, IOptions<AppSettings> options, IPersonaAllocator personaAllocator, ILogger<ReplicateTtsProvider> logger)
    {
        _http = httpClientFactory.CreateClient("Replicate");
        _settings = options.Value.Replicate ?? new ReplicateSettings();
        _personaAllocator = personaAllocator;
        _logger = logger;
    }

    public async Task<Stream> SynthesizeAsync(
        string text,
        string personaKey,
        string voice,
        string? instructions,
        double? temperature,
        int? seed,
        CancellationToken ct)
    {
        var persona = _personaAllocator.GetPersona(personaKey);
        var refPath = persona?.ReferenceAudio
            ?? throw new InvalidOperationException($"Persona '{personaKey}' has no ReferenceAudio configured. Add 'ReferenceAudio' to the persona in appsettings.json.");

        if (!File.Exists(refPath))
            throw new InvalidOperationException($"Reference audio file not found: {refPath}");

        var speakerUri = BuildDataUri(refPath);

        _logger.LogInformation("Replicate synthesis: persona={PersonaKey} ref={RefPath} text={Chars}c model={Model}",
            personaKey, refPath, text.Length, _settings.Model);

        // Parse model ID + version
        var parts = _settings.Model.Split(':', 2);
        var modelId = parts[0];
        var version = parts.Length > 1 ? parts[1] : null;

        var input = new Dictionary<string, object>
        {
            ["text"] = text,
            ["speaker"] = speakerUri,
            ["language"] = _settings.Language
        };

        var prediction = await CreatePredictionAsync(version ?? modelId, input, ct);
        var outputUrl = await PollAsync(prediction, ct);
        var audio = await DownloadAsync(outputUrl, ct);

        _logger.LogInformation("Replicate synthesis complete: {Bytes} bytes", audio.Length);
        return new MemoryStream(audio);
    }

    private static string BuildDataUri(string filePath)
    {
        var bytes = File.ReadAllBytes(filePath);
        var b64 = Convert.ToBase64String(bytes);
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        var mime = ext switch
        {
            ".wav" => "audio/wav",
            ".mp3" => "audio/mpeg",
            ".ogg" => "audio/ogg",
            ".m4a" => "audio/mp4",
            ".flv" => "video/x-flv",
            _ => "audio/wav"
        };
        return $"data:{mime};base64,{b64}";
    }

    private async Task<JsonNode> CreatePredictionAsync(string version, Dictionary<string, object> input, CancellationToken ct)
    {
        var body = new Dictionary<string, object>
        {
            ["version"] = version,
            ["input"] = input
        };
        var json = JsonSerializer.Serialize(body, JsonOpts);

        var retryDelays = new[] { 1000, 3000, 7000 };
        for (var attempt = 0; attempt <= retryDelays.Length; attempt++)
        {
            await _predictThrottle.WaitAsync(ct);
            try
            {
                var gap = DateTime.UtcNow - _lastPrediction;
                if (gap < MinPredictionGap)
                    await Task.Delay(MinPredictionGap - gap, ct);

                var request = new HttpRequestMessage(HttpMethod.Post, "https://api.replicate.com/v1/predictions")
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Token", _settings.ApiKey);

                using var response = await _http.SendAsync(request, ct);
                var responseBody = await response.Content.ReadAsStringAsync(ct);

                if (response.IsSuccessStatusCode)
                {
                    _lastPrediction = DateTime.UtcNow;
                    var node = JsonNode.Parse(responseBody)
                        ?? throw new InvalidOperationException("Replicate returned null response");

                    _logger.LogInformation("Replicate prediction created: {Id}", node["id"]?.ToString());
                    return node;
                }

                if ((int)response.StatusCode != 429 || attempt >= retryDelays.Length)
                    throw new InvalidOperationException($"Replicate API returned {(int)response.StatusCode}: {Truncate(responseBody, 200)}");

                var delayMs = retryDelays[attempt];
                if (response.Headers.TryGetValues("Retry-After", out var retryAfterValues))
                {
                    var retryAfter = retryAfterValues.FirstOrDefault();
                    if (int.TryParse(retryAfter, out var retryAfterSec))
                        delayMs = retryAfterSec * 1000;
                }

                _logger.LogWarning("Replicate API returned 429 — retrying in {DelayMs}ms (attempt {Attempt}/{Max})",
                    delayMs, attempt + 1, retryDelays.Length);
                await Task.Delay(delayMs, ct);
            }
            finally
            {
                _predictThrottle.Release();
            }
        }

        throw new InvalidOperationException("Replicate API exhausted retries");
    }

    private async Task<string> PollAsync(JsonNode prediction, CancellationToken ct)
    {
        var id = prediction["id"]?.ToString()
            ?? throw new InvalidOperationException("Replicate prediction missing ID");
        var getUrl = prediction["urls"]?["get"]?.ToString()
            ?? $"https://api.replicate.com/v1/predictions/{id}";

        using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        pollCts.CancelAfter(_settings.PollTimeoutMs);

        while (!pollCts.Token.IsCancellationRequested)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, getUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Token", _settings.ApiKey);

            using var response = await _http.SendAsync(request, pollCts.Token);
            var body = await response.Content.ReadAsStringAsync(pollCts.Token);
            var node = JsonNode.Parse(body)!;

            var status = node["status"]?.ToString();
            _logger.LogDebug("Replicate poll: {Id} status={Status}", id, status);

            if (status == "succeeded")
            {
                var output = node["output"];
                if (output == null)
                    throw new InvalidOperationException("Replicate prediction succeeded but output is null");

                var url = output.GetValueKind() == JsonValueKind.String
                    ? output.ToString()
                    : output.AsObject()?.FirstOrDefault().Value?.ToString();

                if (string.IsNullOrEmpty(url))
                    throw new InvalidOperationException("Replicate prediction succeeded but output URL is empty");

                return url;
            }

            if (status == "failed" || status == "canceled")
            {
                var error = node["error"]?.ToString() ?? "unknown error";
                throw new InvalidOperationException($"Replicate prediction {status}: {error}");
            }

            await Task.Delay(_settings.PollIntervalMs, pollCts.Token);
        }

        throw new OperationCanceledException($"Replicate prediction {id} timed out after {_settings.PollTimeoutMs}ms");
    }

    private async Task<byte[]> DownloadAsync(string url, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        return await _http.GetByteArrayAsync(url, ct);
    }

    private static string Truncate(string s, int maxLen) => s.Length <= maxLen ? s : s[..maxLen] + "...";
}
