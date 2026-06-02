using benow_conversation.Configuration;
using benow_conversation.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace benow_conversation.Services;

public class OpenRouterTtsProvider : ITtsProvider
{
    private readonly ITtsService _ttsService;
    private readonly AppSettings _settings;
    private readonly ProviderFormatCache _formatCache;
    private readonly ILogger<OpenRouterTtsProvider> _logger;

    public OpenRouterTtsProvider(
        ITtsService ttsService,
        IOptions<AppSettings> settings,
        ProviderFormatCache formatCache,
        ILogger<OpenRouterTtsProvider> logger)
    {
        _ttsService = ttsService;
        _settings = settings.Value;
        _formatCache = formatCache;
        _logger = logger;
    }

    /// <summary>Output format is resolved from ProviderFormatCache, defaulting to MP3/24000/mono/16.</summary>
    public AudioFormat OutputFormat
    {
        get
        {
            var key = $"openrouter/{_settings.OpenRouter.TtsModel}";
            return _formatCache.Get(key) ?? AudioFormat.Mp3_24000Mono;
        }
    }

    public async Task<Stream> SynthesizeAsync(string text, string personaKey, string voice, string? instructions, double? temperature, int? seed, CancellationToken ct)
    {
        var model = _settings.OpenRouter.TtsModel;

        _logger.LogInformation("OpenRouter synthesis: persona={PersonaKey} voice={Voice} model={Model} text={Chars}c",
            personaKey, voice, model, text.Length);

        var (audioStream, format) = await _ttsService.SynthesizeToStreamAsync(text, voice, instructions, temperature, seed, model);

        return audioStream;
    }
}
