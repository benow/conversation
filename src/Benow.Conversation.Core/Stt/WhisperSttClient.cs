using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text.Json;
using Benow.Conversation.Llm;
using Benow.Conversation.Voice;
using Microsoft.Extensions.Logging;

namespace Benow.Conversation.Stt;

/// <summary>Everything the STT client needs for one transcription.</summary>
public sealed record SttOptions
{
    /// <summary>Provider type id: "groq" (Whisper API), "openrouter", or "openai-compatible".</summary>
    public string ProviderTypeId { get; init; } = "groq";
    public string ApiKey { get; init; } = "";
    /// <summary>Base URL for non-Groq providers (empty for Groq — its API root is fixed).</summary>
    public string BaseUrl { get; init; } = "";
    public string Model { get; init; } = "whisper-large-v3";
    public string Language { get; init; } = "en";
}

/// <summary>
/// Whisper-compatible transcription over direct HTTPS — the port of NASTV's
/// VoiceEndpoints.HandleTranscribeAsync provider call (2026-09-17, phase 2), keeping every
/// hard-won behavior:
///   - PCM → WAV wrap (Whisper needs a container), 16kHz mono 16-bit.
///   - temperature=0 — Whisper's documented anti-hallucination setting; without it short
///     segments invent fillers ("Thank you.", "Please.") at the edges.
///   - SttGate (1 concurrent) + 600ms pacing — Groq's Cloudflare WAF IP-blocks request
///     bursts; a 12-call flush once took every endpoint down for ~18 minutes.
///   - Browser-like User-Agent — Cloudflare scores UA-less .NET clients as bots.
///   - Direct HTTPS only: a plain-HTTP relay to port 443 gets a Cloudflare 400 and yields
///     empty transcripts (the 2026-08-05 socat-relay incident).
/// </summary>
public sealed class WhisperSttClient : ITranscriptionService
{
    private readonly ILogger<WhisperSttClient> _logger;
    private readonly SttOptions _options;
    private readonly Func<HttpClient> _httpFactory;

    public WhisperSttClient(ILogger<WhisperSttClient> logger, SttOptions options, Func<HttpClient> httpFactory)
    {
        _logger = logger;
        _options = options;
        _httpFactory = httpFactory;
    }

    public async Task<string?> TranscribeSegmentAsync(byte[] pcm, int sequence, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            _logger.LogError("[stt] No API key for provider '{Provider}' (seq {Seq}). " +
                "Fix: configure the STT provider key in settings", _options.ProviderTypeId, sequence);
            return null;
        }

        try
        {
            using var http = _httpFactory();
            http.Timeout = TimeSpan.FromSeconds(30);
            var isGroq = _options.ProviderTypeId == "groq";
            http.BaseAddress = new Uri(isGroq
                ? "https://api.groq.com/"
                : string.IsNullOrWhiteSpace(_options.BaseUrl) ? "https://openrouter.ai/api/v1/" : _options.BaseUrl);

            using var content = new MultipartFormDataContent();
            using var wav = WavWrapper.Wrap(pcm, 16000, 1, 16);
            var file = new StreamContent(wav);
            file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            content.Add(file, "file", "segment.wav");
            content.Add(new StringContent(_options.Model), "model");
            content.Add(new StringContent("json"), "response_format");
            content.Add(new StringContent(_options.Language), "language");
            content.Add(new StringContent("0"), "temperature");

            http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {_options.ApiKey}");
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", LlmProtocol.BrowserUserAgent);

            _logger.LogInformation("[stt] seq {Seq}: dispatching {Bytes}B to {Provider} (model {Model})",
                sequence, pcm.Length, _options.ProviderTypeId, _options.Model);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            await ProviderPacing.SttGate.WaitAsync(ct);
            try
            {
                // 600ms minimum spacing between request STARTS — held while serialized.
                await ProviderPacing.SttPacer.EnforceAsync(ct);
                using var response = await http.PostAsync("openai/v1/audio/transcriptions", content, ct);
                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(ct);
                    var status = (int)response.StatusCode;
                    if (status == 403 && body.Contains("Access denied", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogError("[stt] seq {Seq}: 403 'Access denied' — Cloudflare IP-level block from an STT burst. " +
                            "Self-heals in ~15-18 min; further calls keep failing until then. " +
                            "Prevention already active: gate serializes to 1 concurrent + 600ms pacing", sequence);
                    }
                    else
                    {
                        _logger.LogError("[stt] seq {Seq}: provider {Status}: {Error}",
                            sequence, status, body[..Math.Min(300, body.Length)]);
                    }
                    return null;
                }

                var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
                var text = json.TryGetProperty("text", out var el) ? el.GetString()?.Trim() ?? "" : "";
                sw.Stop();
                _logger.LogInformation("[stt] seq {Seq}: \"{Text}\" in {Ms}ms", sequence, text, sw.ElapsedMilliseconds);
                return string.IsNullOrWhiteSpace(text) ? null : text;
            }
            finally
            {
                ProviderPacing.SttGate.Release();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[stt] seq {Seq}: failed — {Error}. Fix: check network and the STT provider key/model", sequence, ex.Message);
            return null;
        }
    }
}
