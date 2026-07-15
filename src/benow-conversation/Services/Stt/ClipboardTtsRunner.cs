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
                    await PlayBeepAsync("beep_stop");
                    StopPlayback();
                    continue;
                }

                // Confirmation beep
                await PlayBeepAsync("beep_ready");

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

        var chunkIndex = 0;
        while (splitter.TryDequeue(out var chunk))
        {
            if (ct.IsCancellationRequested) break;

            chunkIndex++;
            _logger.LogInformation("[ClipboardTts] Chunk {Index}: {Length} chars", chunkIndex, chunk!.Length);
            await SynthesizeAndPlayAsync(chunk, persona, chunkIndex, ct);
        }

        // Flush remaining buffer
        var remaining = splitter.Flush();
        if (remaining != null && !ct.IsCancellationRequested)
        {
            chunkIndex++;
            _logger.LogInformation("[ClipboardTts] Final chunk {Index}: {Length} chars", chunkIndex, remaining.Length);
            await SynthesizeAndPlayAsync(remaining, persona, chunkIndex, ct);
        }

        _logger.LogInformation("[ClipboardTts] Playback complete ({ChunkCount} chunks)", chunkIndex);
    }

    private async Task SynthesizeAndPlayAsync(string chunk, (VoicePersona Persona, string Key) persona, int index, CancellationToken ct)
    {
        await using var audioStream = await _ttsProvider.SynthesizeAsync(
            chunk, persona.Key, persona.Persona.Voice, persona.Persona.OpenAiInstructions,
            persona.Persona.Temperature, persona.Persona.Seed, ct);

        var tempFile = Path.Combine(Path.GetTempPath(), $"cbtts_{Guid.NewGuid():N}.wav");
        try
        {
            using (var fileStream = File.Create(tempFile))
                await audioStream.CopyToAsync(fileStream, ct);

            _logger.LogInformation("[ClipboardTts] Chunk {Index}: playing {Bytes} bytes", index, new FileInfo(tempFile).Length);
            await _audioPlayer.PlayAsync(tempFile, cancellationToken: ct);
            _logger.LogInformation("[ClipboardTts] Chunk {Index}: playback finished", index);
        }
        finally
        {
            try { File.Delete(tempFile); }
            catch (Exception ex) { _logger.LogDebug(ex, "[ClipboardTts] Failed to delete temp file {File}", tempFile); }
        }
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

    private async Task PlayBeepAsync(string name)
    {
        try
        {
            var beepFile = Path.Combine(Path.GetTempPath(), $"cbtts_{name}.wav");

            if (!File.Exists(beepFile))
            {
                var resource = $"benow_conversation.Resources.{name}.wav";
                using var stream = typeof(ClipboardTtsRunner).Assembly.GetManifestResourceStream(resource);
                if (stream == null)
                {
                    _logger.LogWarning("[ClipboardTts] Beep resource {Resource} not found", resource);
                    return;
                }
                using var outFile = File.Create(beepFile);
                await stream.CopyToAsync(outFile);
            }

            var playArgs = $"-nodisp -autoexit -volume 80 \"{beepFile}\"";
            using var playProc = Process.Start(new ProcessStartInfo
            {
                FileName = "ffplay",
                Arguments = playArgs,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            await playProc!.WaitForExitAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ClipboardTts] Beep playback failed: {Error}", ex.Message);
        }
    }
}
