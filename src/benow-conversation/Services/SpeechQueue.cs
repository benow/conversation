using System.Diagnostics;
using System.Threading.Channels;
using benow_conversation.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace benow_conversation.Services;

public interface ISpeechQueue
{
    void Enqueue(string text, bool cancelCurrent = true);
    void FlushAndCancel();
    void Flush();
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
    private readonly ITtsProvider _ttsProvider;
    private readonly IAudioPlayer _audioPlayer;
    private readonly IPersistentAudioPipeline? _pipeline;
    private readonly AudioFormatConverter _converter;
    private readonly AppSettings _settings;
    private readonly ILogger<SpeechQueue> _logger;
    private readonly string _ttsModel;
    private readonly string _ttsVoice;
    private readonly string _ttsPersona;
    private readonly string _ttsBackend;
    private readonly string? _ttsInstructions;
    private readonly double? _ttsTemperature;
    private readonly int? _ttsSeed;
    private CancellationTokenSource? _currentPlaybackCts;
    private Task? _processingTask;

    public SpeechQueue(
        ITtsService ttsService,
        ITtsProvider ttsProvider,
        IAudioPlayer audioPlayer,
        IPersistentAudioPipeline? pipeline,
        AudioFormatConverter converter,
        IOptions<AppSettings> settings,
        ILogger<SpeechQueue> logger)
    {
        _ttsService = ttsService;
        _ttsProvider = ttsProvider;
        _audioPlayer = audioPlayer;
        _pipeline = pipeline;
        _converter = converter;
        _settings = settings.Value;
        _logger = logger;
        _ttsBackend = _settings.TtsBackend;

        var proxy = _settings.Proxy;
        VoicePersona? persona = null;

        if (!string.IsNullOrWhiteSpace(proxy.TtsPersona) &&
            _settings.Personas.TryGetValue(proxy.TtsPersona, out var p))
        {
            persona = p;
            _ttsPersona = proxy.TtsPersona;
            _logger.LogInformation("SpeechQueue using persona: {Persona} (backend={Backend})", proxy.TtsPersona, _ttsBackend);
        }
        else
        {
            _ttsPersona = "";
        }

        _ttsModel = persona?.Model ?? (string.IsNullOrEmpty(proxy.TtsModel) ? _settings.OpenRouter.TtsModel : proxy.TtsModel);
        _ttsVoice = persona?.Voice ?? (string.IsNullOrEmpty(proxy.TtsVoice) ? "alloy" : proxy.TtsVoice);
        _ttsInstructions = persona?.OpenAiInstructions;
        _ttsTemperature = persona?.Temperature;
        _ttsSeed = persona?.Seed;
    }

    public void Enqueue(string text, bool cancelCurrent = true)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        _logger.LogInformation("Enqueueing speech ({Length} chars, cancel={Cancel}): {Preview}...",
            text.Length, cancelCurrent, text.Length > 80 ? text[..80] : text);
        if (cancelCurrent)
        {
            try { _currentPlaybackCts?.Cancel(); } catch (ObjectDisposedException) { }
        }
        _channel.Writer.TryWrite(text);
    }

    public void FlushAndCancel()
    {
        _logger.LogInformation("Flushing speech queue and cancelling current playback");
        while (_channel.Reader.TryRead(out _)) { }
        try { _currentPlaybackCts?.Cancel(); } catch (ObjectDisposedException) { }
        if (_pipeline != null)
        {
            try { _pipeline.InterruptAsync().GetAwaiter().GetResult(); } catch { }
        }
    }

    public void Flush()
    {
        _logger.LogInformation("Flushing speech queue (pipeline kept alive)");
        while (_channel.Reader.TryRead(out _)) { }
        try { _currentPlaybackCts?.Cancel(); } catch (ObjectDisposedException) { }
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
            var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _currentPlaybackCts = cts;

            try
            {
                _logger.LogInformation("Speaking ({Length} chars): {Preview}...",
                    text.Length, text.Length > 60 ? text[..60] : text);

                var sw = Stopwatch.StartNew();

                if (_ttsBackend != "openrouter")
                {
                    var audioStream = await _ttsProvider.SynthesizeAsync(
                        text, _ttsPersona, _ttsVoice, _ttsInstructions, _ttsTemperature, _ttsSeed, cts.Token);
                    await using var _ = audioStream;
                    _logger.LogInformation("TTS ({Backend}) received in {Ms}ms", _ttsBackend, sw.ElapsedMilliseconds);

                    using var ms = new MemoryStream();
                    await audioStream.CopyToAsync(ms, cts.Token);
                    var audioBytes = ms.ToArray();

                    audioBytes = await _converter.ConvertAsync(audioBytes, _ttsProvider.OutputFormat, cts.Token);

                    if (_pipeline != null)
                    {
                        using var pcmMs = new MemoryStream(audioBytes);
                        await _pipeline.PipeAsync(pcmMs, cts.Token);
                    }
                    else
                    {
                        using var pcmMs = new MemoryStream(audioBytes);
                        await _audioPlayer.PlayStreamAsync(pcmMs, "pcm", cancellationToken: cts.Token);
                    }

                    _logger.LogInformation("Audio completed in {Ms}ms total", sw.ElapsedMilliseconds);
                }
                else if (_pipeline != null && _settings.Proxy.StreamTtsAudio)
                {
                    var audioStream = await _ttsService.SynthesizeLiveStreamAsync(
                        text, _ttsVoice, _ttsInstructions, _ttsTemperature, _ttsSeed, _ttsModel);
                    await using var _ = audioStream;
                    _logger.LogInformation("TTS stream received in {Ms}ms, piping to ffplay", sw.ElapsedMilliseconds);

                    using var ms = new MemoryStream();
                    await audioStream.CopyToAsync(ms, cts.Token);
                    var audioBytes = ms.ToArray();

                    audioBytes = await _converter.ConvertAsync(audioBytes, _ttsProvider.OutputFormat, cts.Token);

                    using var pcmMs = new MemoryStream(audioBytes);
                    await _pipeline.PipeAsync(pcmMs, cts.Token);
                    _logger.LogInformation("Audio piped to ffplay in {Ms}ms total", sw.ElapsedMilliseconds);
                }
                else
                {
                    var (audioStream, format) = await _ttsService.SynthesizeToStreamAsync(
                        text, _ttsVoice, _ttsInstructions, _ttsTemperature, _ttsSeed, _ttsModel);
                    _logger.LogInformation("TTS buffered in {Ms}ms", sw.ElapsedMilliseconds);

                    using var ms = new MemoryStream();
                    await audioStream.CopyToAsync(ms, cts.Token);
                    var audioBytes = ms.ToArray();

                    audioBytes = await _converter.ConvertAsync(audioBytes, _ttsProvider.OutputFormat, cts.Token);

                    using var pcmMs = new MemoryStream(audioBytes);
                    await _audioPlayer.PlayStreamAsync(pcmMs, "pcm", cancellationToken: cts.Token);
                    _logger.LogInformation("Audio played in {Ms}ms total", sw.ElapsedMilliseconds);
                }
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
