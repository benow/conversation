using System.Diagnostics;
using benow_conversation.Configuration;
using benow_conversation.Services.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace benow_conversation.Services.Stt;

public class ClipboardTtsRunner : IClipboardTtsRunner
{
    private readonly ClipboardTtsTrigger _trigger;
    private readonly IClipboardService _clipboard;
    private readonly ITtsProvider _ttsProvider;
    private readonly AudioFormatConverter _formatConverter;
    private readonly IPersistentAudioPipeline? _pipeline;
    private readonly IAudioPlayer _audioPlayer;
    private readonly AppSettings _settings;
    private readonly ILogger<ClipboardTtsRunner> _logger;

    private bool _isPlaying;
    private CancellationTokenSource? _playbackCts;

    public ClipboardTtsRunner(
        ClipboardTtsTrigger trigger,
        IClipboardService clipboard,
        ITtsProvider ttsProvider,
        AudioFormatConverter formatConverter,
        IPersistentAudioPipeline? pipeline,
        IAudioPlayer audioPlayer,
        IOptions<AppSettings> settings,
        ILogger<ClipboardTtsRunner> logger)
    {
        _trigger = trigger;
        _clipboard = clipboard;
        _ttsProvider = ttsProvider;
        _formatConverter = formatConverter;
        _pipeline = pipeline;
        _audioPlayer = audioPlayer;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!_settings.ClipboardTts.Enabled)
        {
            _logger.LogInformation("[ClipboardTts] Disabled by configuration");
            return;
        }

        if (!_trigger.IsAvailable)
        {
            _logger.LogWarning("[ClipboardTts] Trigger not available — keyboard device access failed");
            return;
        }

        if (!_clipboard.IsAvailable)
        {
            _logger.LogWarning("[ClipboardTts] Clipboard service not available — wl-paste not found");
            return;
        }

        _logger.LogInformation("[ClipboardTts] Ready. Press {Key} to read clipboard and speak, or press again to stop playback.",
            _settings.ClipboardTts.TriggerKey);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _trigger.WaitForTriggerAsync(cancellationToken);

                // Toggle: if playing, stop
                if (_isPlaying)
                {
                    _logger.LogInformation("[ClipboardTts] Stop requested");
                    StopPlayback();
                    continue;
                }

                // Read clipboard
                var text = await _clipboard.ReadAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(text))
                {
                    _logger.LogInformation("[ClipboardTts] Clipboard is empty — skipping");
                    continue;
                }

                _logger.LogInformation("[ClipboardTts] Read {Length} chars from clipboard", text.Length);

                // Resolve persona
                var persona = ResolvePersona();
                if (persona == null)
                {
                    _logger.LogError("[ClipboardTts] No suitable persona found — check Personas and ClipboardTts:Persona config");
                    continue;
                }

                var resolvedPersona = persona.Value;

                _isPlaying = true;
                _playbackCts = new CancellationTokenSource();

                try
                {
                    await PlayTextAsync(text, resolvedPersona, _playbackCts.Token);
                }
                catch (OperationCanceledException) when (_playbackCts?.IsCancellationRequested == true)
                {
                    _logger.LogInformation("[ClipboardTts] Playback cancelled");
                }
                finally
                {
                    _isPlaying = false;
                    _playbackCts?.Dispose();
                    _playbackCts = null;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ClipboardTts] Error: {Message}", ex.Message);
                _isPlaying = false;
                _playbackCts?.Dispose();
                _playbackCts = null;
            }
        }

        _logger.LogInformation("[ClipboardTts] Stopped");
    }

    private void StopPlayback()
    {
        _playbackCts?.Cancel();
        if (_pipeline != null)
        {
            try { _pipeline.InterruptAsync().GetAwaiter().GetResult(); }
            catch (Exception ex) { _logger.LogDebug(ex, "[ClipboardTts] Error interrupting pipeline: {Message}", ex.Message); }
        }
        _isPlaying = false;
    }

    private async Task PlayTextAsync(string text, (VoicePersona Persona, string Key) persona, CancellationToken ct)
    {
        var splitter = new ParagraphSplitter();
        splitter.Append(text);

        // Collect all chunks
        var chunks = new List<string>();
        while (splitter.TryDequeue(out var chunk))
            chunks.Add(chunk!);

        var remaining = splitter.Flush();
        if (remaining != null)
            chunks.Add(remaining);

        if (chunks.Count == 0) return;

        _logger.LogInformation("[ClipboardTts] {ChunkCount} chunks to synthesize", chunks.Count);

        // Synthesize all chunks in parallel (up to 3 concurrent), saving to temp files
        var tempFiles = new string?[chunks.Count];
        var sw = Stopwatch.StartNew();

        using var throttle = new SemaphoreSlim(3, 3);
        var synthTasks = chunks.Select(async (chunk, i) =>
        {
            await throttle.WaitAsync(ct);
            try
            {
                var tempFile = Path.Combine(Path.GetTempPath(), $"cbtts_{Guid.NewGuid():N}.wav");
                await using var audioStream = await _ttsProvider.SynthesizeAsync(
                    chunk, persona.Key, persona.Persona.Voice, persona.Persona.OpenAiInstructions,
                    persona.Persona.Temperature, persona.Persona.Seed, ct);

                using (var fileStream = File.Create(tempFile))
                    await audioStream.CopyToAsync(fileStream, ct);

                tempFiles[i] = tempFile;
                _logger.LogInformation("[ClipboardTts] Chunk {Index}/{Total}: synthesized ({Ms}ms, {Bytes} bytes)",
                    i + 1, chunks.Count, sw.ElapsedMilliseconds, new FileInfo(tempFile).Length);
            }
            finally
            {
                throttle.Release();
            }
        }).ToArray();

        await Task.WhenAll(synthTasks);
        _logger.LogInformation("[ClipboardTts] All {Count} chunks synthesized in {Ms}ms", chunks.Count, sw.ElapsedMilliseconds);

        // Play sequentially
        for (var i = 0; i < tempFiles.Length; i++)
        {
            if (ct.IsCancellationRequested) break;
            var file = tempFiles[i];
            if (file == null) continue;

            try
            {
                _logger.LogInformation("[ClipboardTts] Playing chunk {Index}/{Total}", i + 1, chunks.Count);
                await _audioPlayer.PlayAsync(file, cancellationToken: ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            finally
            {
                try { File.Delete(file); } catch { }
                tempFiles[i] = null;
            }
        }

        // Cleanup any remaining temp files
        foreach (var file in tempFiles)
            if (file != null)
                try { File.Delete(file); } catch { }

        _logger.LogInformation("[ClipboardTts] Playback complete ({ChunkCount} chunks, {TotalMs}ms)",
            chunks.Count, sw.ElapsedMilliseconds);
    }

    private (VoicePersona Persona, string Key)? ResolvePersona()
    {
        var personaKey = _settings.ClipboardTts.Persona;

        // Explicit persona key
        if (!string.IsNullOrWhiteSpace(personaKey) && _settings.Personas.TryGetValue(personaKey, out var persona))
            return (persona, personaKey);

        // Fall back to first IsDefault persona
        var defaultEntry = _settings.Personas.FirstOrDefault(p => p.Value.IsDefault);
        if (defaultEntry.Key != null)
        {
            _logger.LogInformation("[ClipboardTts] Using default persona: {Key}", defaultEntry.Key);
            return (defaultEntry.Value, defaultEntry.Key);
        }

        // Fall back to first enabled persona
        var firstEnabled = _settings.Personas.FirstOrDefault(p => p.Value.Enabled);
        if (firstEnabled.Key != null)
        {
            _logger.LogInformation("[ClipboardTts] Using first enabled persona: {Key}", firstEnabled.Key);
            return (firstEnabled.Value, firstEnabled.Key);
        }

        return null;
    }
}
