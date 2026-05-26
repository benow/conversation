using System.Threading.Channels;
using benow_conversation.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace benow_conversation.Services;

public interface ISpeechQueue
{
    void Enqueue(string text);
    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}

public class SpeechQueue : ISpeechQueue
{
    private readonly Channel<string> _channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
    {
        SingleReader = true
    });

    private readonly ITtsService _ttsService;
    private readonly IAudioPlayer _audioPlayer;
    private readonly AppSettings _settings;
    private readonly ILogger<SpeechQueue> _logger;
    private readonly string _ttsModel;
    private readonly string _ttsVoice;
    private readonly string? _ttsInstructions;
    private readonly double? _ttsTemperature;
    private readonly int? _ttsSeed;
    private CancellationTokenSource? _currentPlaybackCts;
    private Task? _processingTask;

    public SpeechQueue(
        ITtsService ttsService,
        IAudioPlayer audioPlayer,
        IOptions<AppSettings> settings,
        ILogger<SpeechQueue> logger)
    {
        _ttsService = ttsService;
        _audioPlayer = audioPlayer;
        _settings = settings.Value;
        _logger = logger;

        var proxy = _settings.Proxy;
        VoicePersona? persona = null;

        if (!string.IsNullOrWhiteSpace(proxy.TtsPersona) &&
            _settings.Personas.TryGetValue(proxy.TtsPersona, out var p))
        {
            persona = p;
            _logger.LogInformation("SpeechQueue using persona: {Persona}", proxy.TtsPersona);
        }

        _ttsModel = persona?.Model ?? (string.IsNullOrEmpty(proxy.TtsModel) ? _settings.OpenRouter.TtsModel : proxy.TtsModel);
        _ttsVoice = persona?.Voice ?? (string.IsNullOrEmpty(proxy.TtsVoice) ? "alloy" : proxy.TtsVoice);
        _ttsInstructions = persona?.OpenAiInstructions;
        _ttsTemperature = persona?.Temperature;
        _ttsSeed = persona?.Seed;
    }

    public void Enqueue(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        _logger.LogInformation("Enqueueing speech ({Length} chars): {Preview}...",
            text.Length, text.Length > 80 ? text[..80] : text);
        _channel.Writer.TryWrite(text);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _processingTask = ProcessQueueAsync(cancellationToken);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _channel.Writer.Complete();
        if (_processingTask != null)
            await _processingTask;
    }

    private async Task ProcessQueueAsync(CancellationToken cancellationToken)
    {
        await foreach (var text in _channel.Reader.ReadAllAsync(cancellationToken))
        {
            try
            {
                _currentPlaybackCts?.Cancel();
            }
            catch (ObjectDisposedException) { }

            var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _currentPlaybackCts = cts;

            try
            {
                _logger.LogInformation("Speaking ({Length} chars): {Preview}...",
                    text.Length, text.Length > 60 ? text[..60] : text);

                var (audioStream, format) = await _ttsService.SynthesizeToStreamAsync(
                    text, _ttsVoice, _ttsInstructions, _ttsTemperature, _ttsSeed, _ttsModel);

                using var ms = new MemoryStream();
                await audioStream.CopyToAsync(ms, cts.Token);
                ms.Position = 0;

                await _audioPlayer.PlayStreamAsync(ms, format, cancellationToken: cts.Token);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation("Speech cancelled — new speech incoming");
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "TTS playback failed: {Error}", ex.Message);
            }
            finally
            {
                cts.Dispose();
            }
        }
    }
}
