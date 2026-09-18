using System.Net.Http.Json;
using Benow.Conversation.Llm;
using Benow.Conversation.Voice;
using Microsoft.Extensions.Logging;

namespace Benow.Conversation.Tts;

/// <summary>Options for the local Kokoro TTS server (V1's offline backend).</summary>
public sealed record KokoroOptions
{
    /// <summary>Base URL of scripts/kokoro-server.py (FastAPI: POST /v1/tts → WAV).</summary>
    public string ServerUrl { get; init; } = "http://localhost:50001";
    /// <summary>Kokoro voice id (af_heart, am_onyx, …). Unknown ids fall back to af_heart.</summary>
    public string Voice { get; init; } = "af_heart";
    public double Speed { get; init; } = 1.0;

    /// <summary>Maps V1 persona-voice slots (female-1…) to Kokoro voice ids, ported verbatim.</summary>
    public static readonly Dictionary<string, string> VoiceMap = new()
    {
        ["female-1"] = "af_heart",
        ["female-2"] = "af_nova",
        ["female-3"] = "af_bella",
        ["female-4"] = "af_sarah",
        ["female-5"] = "af_alloy",
        ["female-6"] = "af_sky",
        ["female-7"] = "af_nicole",
        ["female-8"] = "af_jessica",
        ["female-9"] = "af_kore",
        ["female-10"] = "af_river",
        ["female-11"] = "af_aoede",
        ["female-12"] = "af_alloy",
        ["female-13"] = "af_kore",
        ["male-1"] = "am_onyx",
    };
}

/// <summary>
/// Local Kokoro-82M TTS over HTTP (offline — no provider key, no per-character cost).
/// Port of V1's KokoroTtsProvider (2026-09-17, phase 2) onto the Core ITtsService seam:
/// POST {text, voice, speed} → WAV stream → decode to 16-bit mono PCM.
/// </summary>
public sealed class KokoroTtsClient : ITtsService
{
    private readonly ILogger<KokoroTtsClient> _logger;
    private readonly KokoroOptions _options;
    private readonly Func<HttpClient> _httpFactory;

    public KokoroTtsClient(ILogger<KokoroTtsClient> logger, KokoroOptions options, Func<HttpClient> httpFactory)
    {
        _logger = logger;
        _options = options;
        _httpFactory = httpFactory;
    }

    /// <summary>Configured means reachable-or-assumed: the server is local and may start later.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.ServerUrl);

    public async Task<TtsAudio?> SynthesizeAsync(string text, CancellationToken ct)
    {
        var voice = KokoroOptions.VoiceMap.TryGetValue(_options.Voice, out var mapped)
            ? mapped
            : _options.Voice; // already a raw Kokoro id (af_heart etc.)
        if (string.IsNullOrWhiteSpace(voice)) voice = "af_heart";

        try
        {
            using var http = _httpFactory();
            http.BaseAddress = new Uri(_options.ServerUrl.TrimEnd('/'));
            http.Timeout = TimeSpan.FromSeconds(120);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var response = await http.PostAsJsonAsync("/v1/tts", new { text, voice, speed = _options.Speed }, ct);
            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync(ct);
                _logger.LogError("[tts] Kokoro {Status}: {Error}. Fix: is scripts/kokoro-server.py running at {Url}?",
                    (int)response.StatusCode, err[..Math.Min(200, err.Length)], _options.ServerUrl);
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            // The server returns WAV; tolerate raw PCM too (defensive — the header check is cheap).
            DecodedWav? decoded = null;
            if (bytes.Length > 44 && bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F')
                decoded = WavDecoder.ParseWav(bytes);
            if (decoded == null)
            {
                _logger.LogWarning("[tts] Kokoro returned {Bytes}B that is not a parsable WAV — treating as s16le 24kHz PCM", bytes.Length);
                decoded = new DecodedWav(bytes, 24000, 1, 16);
            }

            sw.Stop();
            _logger.LogInformation("[tts] Kokoro {Chars}c → {Bytes}B Pcm @{Rate}Hz ({Ms}ms, voice={Voice})",
                text.Length, decoded.Pcm.Length, decoded.SampleRate, sw.ElapsedMilliseconds, voice);
            return new TtsAudio(decoded.Pcm, decoded.SampleRate, null, sw.ElapsedMilliseconds, text.Length);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[tts] Kokoro synthesis failed — {Error}. Fix: start the local server (scripts/kokoro-server.py) or switch TTS provider",
                ex.Message);
            return null;
        }
    }
}
