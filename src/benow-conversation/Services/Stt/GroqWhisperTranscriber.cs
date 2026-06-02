using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using benow_conversation.Configuration;
using benow_conversation.Services.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace benow_conversation.Services.Stt;

public class GroqWhisperTranscriber : ITranscriptionService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly GroqSettings _settings;
    private readonly ILogger<GroqWhisperTranscriber> _logger;

    public GroqWhisperTranscriber(
        IHttpClientFactory httpClientFactory,
        IOptions<AppSettings> settings,
        ILogger<GroqWhisperTranscriber> logger)
    {
        _httpClientFactory = httpClientFactory;
        _settings = settings.Value.Groq;
        _logger = logger;
    }

    public async Task<string> TranscribeAsync(string audioFilePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.ApiKey))
        {
            _logger.LogError("[Transcriber] Groq API key is not configured");
            throw new InvalidOperationException("Groq API key is not configured. Set 'Groq:ApiKey' in appsettings.Development.json.");
        }

        if (!File.Exists(audioFilePath))
        {
            _logger.LogError("[Transcriber] Audio file not found: {Path}", audioFilePath);
            throw new FileNotFoundException($"Audio file not found: {audioFilePath}");
        }

        var fileSize = new FileInfo(audioFilePath).Length;
        _logger.LogInformation("[Transcriber] Starting transcription: file={File} ({Size} bytes), model={Model}, endpoint={Url}",
            Path.GetFileName(audioFilePath), fileSize, _settings.Model, _settings.BaseUrl);

        var sw = Stopwatch.StartNew();
        var client = _httpClientFactory.CreateClient("Groq");

        using var content = new MultipartFormDataContent();
        await using var fileStream = File.OpenRead(audioFilePath);
        var fileContent = new StreamContent(fileStream);
        var ext = Path.GetExtension(audioFilePath).TrimStart('.').ToLowerInvariant();
        var contentType = ext switch
        {
            "mp3" => "audio/mpeg",
            "mp4" or "m4a" => "audio/mp4",
            "webm" => "audio/webm",
            _ => "audio/wav"
        };
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Add(fileContent, "file", Path.GetFileName(audioFilePath));
        content.Add(new StringContent(_settings.Model), "model");
        content.Add(new StringContent("json"), "response_format");
        content.Add(new StringContent("en"), "language");

        var url = $"{_settings.BaseUrl}/audio/transcriptions";

        HttpResponseMessage response;
        try
        {
            response = await client.PostAsync(url, content, ct);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "[Transcriber] Connection to Groq failed: {Error}", ex.Message);
            throw new InvalidOperationException($"Unable to connect to Groq at {_settings.BaseUrl}: {ex.Message}", ex);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogError("[Transcriber] Groq request timed out (timeout={Timeout})", client.Timeout);
            throw new InvalidOperationException($"Groq transcription timed out after {client.Timeout.TotalSeconds:F0}s.");
        }

        _logger.LogInformation("[Transcriber] Groq response: HTTP {Status} in {ElapsedMs}ms", (int)response.StatusCode, sw.ElapsedMilliseconds);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("[Transcriber] Groq transcription failed: HTTP {Status} — {Error}", (int)response.StatusCode, errorBody.Length > 500 ? errorBody[..500] : errorBody);

            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                throw new InvalidOperationException($"Groq rate limit exceeded (HTTP 429). Wait and retry.");

            throw new InvalidOperationException($"Groq transcription failed (HTTP {(int)response.StatusCode}): {errorBody}");
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct);
        string text;
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            text = doc.RootElement.GetProperty("text").GetString() ?? "";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Transcriber] Failed to parse Groq response: {Body}", responseBody.Length > 500 ? responseBody[..500] : responseBody);
            throw new InvalidOperationException($"Failed to parse Groq transcription response: {ex.Message}", ex);
        }

        _logger.LogInformation("[Transcriber] Transcription complete: {Length} chars in {ElapsedMs}ms", text.Length, sw.ElapsedMilliseconds);
        return text;
    }
}
