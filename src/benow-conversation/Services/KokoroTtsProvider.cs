using System.Net.Http.Json;
using benow_conversation.Configuration;
using benow_conversation.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace benow_conversation.Services;

public class KokoroTtsProvider : ITtsProvider
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<KokoroTtsProvider> _logger;

    /// <summary>Kokoro is a raw PCM backend — format is known statically.</summary>
    public AudioFormat OutputFormat => AudioFormat.Pcm24000Mono;

    private static readonly Dictionary<string, string> VoiceMap = new()
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

    public KokoroTtsProvider(IHttpClientFactory httpClientFactory, IOptions<AppSettings> settings, ILogger<KokoroTtsProvider> logger)
    {
        _httpClient = httpClientFactory.CreateClient("KokoroTts");
        _httpClient.BaseAddress = new Uri(settings.Value.Kokoro?.ServerUrl ?? "http://localhost:50001");
        _logger = logger;
    }

    public async Task<Stream> SynthesizeAsync(string text, string personaKey, string voice, string? instructions, double? temperature, int? seed, CancellationToken ct)
    {
        if (!VoiceMap.TryGetValue(personaKey, out var kokoroVoice))
            kokoroVoice = "af_heart";

        var request = new { text, voice = kokoroVoice, speed = 1.0 };

        _logger.LogInformation("Kokoro synthesis: persona={PersonaKey} voice={KokoroVoice} text={Chars}c", personaKey, kokoroVoice, text.Length);

        var response = await _httpClient.PostAsJsonAsync("/v1/tts", request, ct);
        response.EnsureSuccessStatusCode();

        var audioStream = await response.Content.ReadAsStreamAsync(ct);
        var ms = new MemoryStream();
        await audioStream.CopyToAsync(ms, ct);
        ms.Position = 0;
        return ms;
    }
}
