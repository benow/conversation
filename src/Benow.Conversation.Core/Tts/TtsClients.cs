using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Benow.Conversation.Llm;
using Benow.Conversation.Voice;
using Microsoft.Extensions.Logging;

namespace Benow.Conversation.Tts;

/// <summary>TTS selection + voice assets for one synthesis.</summary>
public sealed record TtsOptions
{
    /// <summary>"openrouter" | "openai-compatible" | "replicate".</summary>
    public string ProviderTypeId { get; init; } = "openrouter";
    public string ApiKey { get; init; } = "";
    public string BaseUrl { get; init; } = "";
    /// <summary>Model id — for Replicate the versioned "owner/name:hash" (versionless works too).</summary>
    public string Model { get; init; } = "";
    /// <summary>Voice name: a WAV filename in <see cref="VoicesDirectory"/> for Replicate, an OpenAI voice id otherwise.</summary>
    public string Voice { get; init; } = "";
    /// <summary>Reference-voice library directory (Replicate voice cloning).</summary>
    public string VoicesDirectory { get; init; } = "";
}

/// <summary>
/// OpenAI-compatible / OpenRouter speech synthesis (`POST audio/speech`, PCM out) — the port of
/// NASTV's SynthesizeOpenRouterAsync (2026-09-17, phase 2). The provider returns 24kHz mono
/// 16-bit PCM when asked for response_format=pcm.
/// </summary>
public sealed class OpenAiTtsClient : ITtsService
{
    private readonly ILogger<OpenAiTtsClient> _logger;
    private readonly TtsOptions _options;
    private readonly Func<HttpClient> _httpFactory;

    public OpenAiTtsClient(ILogger<OpenAiTtsClient> logger, TtsOptions options, Func<HttpClient> httpFactory)
    {
        _logger = logger;
        _options = options;
        _httpFactory = httpFactory;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.ApiKey) && !string.IsNullOrWhiteSpace(_options.Model);

    public async Task<TtsAudio?> SynthesizeAsync(string text, CancellationToken ct)
    {
        if (!IsConfigured)
        {
            _logger.LogError("[tts] not configured (key={HasKey}, model='{Model}'). Fix: set the TTS provider, model and voice in settings",
                !string.IsNullOrWhiteSpace(_options.ApiKey), _options.Model);
            return null;
        }

        try
        {
            using var http = _httpFactory();
            http.Timeout = TimeSpan.FromSeconds(60);
            http.BaseAddress = new Uri(string.IsNullOrWhiteSpace(_options.BaseUrl) ? "https://openrouter.ai/api/v1/" : _options.BaseUrl);
            http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {_options.ApiKey}");
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", LlmProtocol.BrowserUserAgent);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var body = new { model = _options.Model, input = text, voice = _options.Voice, response_format = "pcm" };
            using var response = await http.PostAsJsonAsync("audio/speech", body, ct);
            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync(ct);
                _logger.LogError("[tts] synthesis failed {Status}: {Error}. Fix: check the TTS model/voice names for provider '{Provider}'",
                    (int)response.StatusCode, err[..Math.Min(300, err.Length)], _options.ProviderTypeId);
                return null;
            }

            var pcm = await response.Content.ReadAsByteArrayAsync(ct);
            sw.Stop();
            _logger.LogInformation("[tts] {Chars}c → {Bytes}B PCM ({Ms}ms)", text.Length, pcm.Length, sw.ElapsedMilliseconds);
            return new TtsAudio(pcm, 24000, null, sw.ElapsedMilliseconds, text.Length);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[tts] synthesis threw — {Error}. Fix: check network and the TTS provider key", ex.Message);
            return null;
        }
    }
}

/// <summary>
/// Replicate XTTS v2 voice cloning — the port of NASTV's SynthesizeReplicateAsync /
/// CreateAndPollReplicatePredictionAsync (2026-09-17, phase 2). Kept verbatim:
///   - versioned model ids post to /v1/predictions with `version`; versionless ids post to
///     /v1/models/{id}/predictions (posting a versionless id as `version` 400s — the
///     2026-08-20 incident where the bare "lucataco/xtts-v2" was persisted by the model UI).
///   - the whole create+poll is serialized behind the TtsReplicateGate: XTTS runs on one
///     serial GPU, so concurrent predictions queue and blow past timeouts (audio dropped).
///   - empty Voice auto-picks the first WAV in the library (works out of the box).
///   - the returned WAV is fully parsed (not 44-byte stripped) → 16-bit mono PCM.
/// </summary>
public sealed class ReplicateTtsClient : ITtsService
{
    private readonly ILogger<ReplicateTtsClient> _logger;
    private readonly TtsOptions _options;
    private readonly Func<HttpClient> _httpFactory;

    public ReplicateTtsClient(ILogger<ReplicateTtsClient> logger, TtsOptions options, Func<HttpClient> httpFactory)
    {
        _logger = logger;
        _options = options;
        _httpFactory = httpFactory;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.ApiKey) && !string.IsNullOrWhiteSpace(_options.Model);

    public async Task<TtsAudio?> SynthesizeAsync(string text, CancellationToken ct)
    {
        if (!IsConfigured)
        {
            _logger.LogError("[tts] Replicate not configured (key={HasKey}, model='{Model}')", !string.IsNullOrWhiteSpace(_options.ApiKey), _options.Model);
            return null;
        }

        var refPath = ResolveReferenceVoice();
        if (refPath == null) return null;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await ProviderPacing.TtsReplicateGate.WaitAsync(ct);
        try
        {
            var decoded = await CreateAndPollAsync(text, refPath, ct);
            if (decoded == null) return null;
            sw.Stop();
            _logger.LogInformation("[tts] {Chars}c → {Bytes}B PCM @{Rate}Hz ({Ms}ms, gate-serialized)",
                text.Length, decoded.Pcm.Length, decoded.SampleRate, sw.ElapsedMilliseconds);
            return new TtsAudio(decoded.Pcm, decoded.SampleRate, null, sw.ElapsedMilliseconds, text.Length);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[tts] Replicate synthesis threw — {Error}. Fix: check the Replicate token and model version", ex.Message);
            return null;
        }
        finally
        {
            ProviderPacing.TtsReplicateGate.Release();
        }
    }

    private string? ResolveReferenceVoice()
    {
        var dir = _options.VoicesDirectory;
        var voice = _options.Voice;

        if (!string.IsNullOrWhiteSpace(voice))
        {
            var path = Path.Combine(dir, voice);
            if (File.Exists(path)) return path;
            _logger.LogError("[tts] voice file not found: {Path}. Fix: add the WAV to the voice library or correct the Voice setting", path);
            return null;
        }

        // Empty voice → first WAV in the library, so synthesis works unconfigured.
        var candidates = Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*.wav").Select(Path.GetFileName).OrderBy(f => f).ToArray()
            : Array.Empty<string>();
        if (candidates.Length == 0)
        {
            _logger.LogError("[tts] voice is empty and '{Dir}' has no WAV files. Fix: record or import a reference voice", dir);
            return null;
        }
        _logger.LogInformation("[tts] voice not set — using first library voice '{Voice}'", candidates[0]);
        return Path.Combine(dir, candidates[0]!);
    }

    private async Task<DecodedWav?> CreateAndPollAsync(string text, string refPath, CancellationToken ct)
    {
        using var http = _httpFactory();
        http.Timeout = TimeSpan.FromSeconds(120);

        var speakerUri = WavDecoder.BuildDataUri(refPath);
        var parts = _options.Model.Split(':', 2);
        var version = parts.Length > 1 ? parts[1] : null;

        var input = new Dictionary<string, object> { ["text"] = text, ["speaker"] = speakerUri, ["language"] = "en" };
        object createBody = version != null ? new { version, input } : (object)new { input };
        var createUrl = version != null
            ? "https://api.replicate.com/v1/predictions"
            : $"https://api.replicate.com/v1/models/{_options.Model}/predictions";

        using var createReq = new HttpRequestMessage(HttpMethod.Post, createUrl)
        {
            Content = new StringContent(JsonSerializer.Serialize(createBody), Encoding.UTF8, "application/json")
        };
        createReq.Headers.Authorization = new AuthenticationHeaderValue("Token", _options.ApiKey);
        createReq.Headers.TryAddWithoutValidation("User-Agent", LlmProtocol.BrowserUserAgent);

        using var createResp = await http.SendAsync(createReq, ct);
        if (!createResp.IsSuccessStatusCode)
        {
            var err = await createResp.Content.ReadAsStringAsync(ct);
            _logger.LogError("[tts] Replicate create failed {Status}: {Error}. Fix: use the versioned model id (owner/name:hash) — " +
                "a versionless id must be a real model path", (int)createResp.StatusCode, err[..Math.Min(300, err.Length)]);
            return null;
        }

        var createJson = JsonNode.Parse(await createResp.Content.ReadAsStringAsync(ct))!;
        var getUrl = createJson["urls"]?["get"]?.ToString() ?? $"https://api.replicate.com/v1/predictions/{createJson["id"]}";

        using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        pollCts.CancelAfter(TimeSpan.FromSeconds(60));

        while (!pollCts.Token.IsCancellationRequested)
        {
            using var pollReq = new HttpRequestMessage(HttpMethod.Get, getUrl);
            pollReq.Headers.Authorization = new AuthenticationHeaderValue("Token", _options.ApiKey);
            using var pollResp = await http.SendAsync(pollReq, pollCts.Token);
            var pollJson = JsonNode.Parse(await pollResp.Content.ReadAsStringAsync(pollCts.Token))!;
            var status = pollJson["status"]?.ToString();

            if (status == "succeeded")
            {
                var output = pollJson["output"]!;
                var url = output.GetValueKind() == JsonValueKind.String
                    ? output.ToString()
                    : output.AsObject().First().Value!.ToString();
                var wav = await http.GetByteArrayAsync(url, ct);
                var parsed = WavDecoder.ParseWav(wav);
                if (parsed == null)
                    _logger.LogError("[tts] Replicate output WAV unparseable ({Bytes}B)", wav.Length);
                return parsed;
            }

            if (status is "failed" or "canceled")
            {
                _logger.LogError("[tts] Replicate prediction {Status}: {Error}", status, pollJson["error"]);
                return null;
            }

            await Task.Delay(600, pollCts.Token);
        }

        _logger.LogError("[tts] Replicate poll timed out after 60s — the gate keeps predictions serialized; " +
            "a timeout here usually means provider-side queueing. Consider a smaller chunk (lower ParagraphMaxChars)");
        return null;
    }
}
