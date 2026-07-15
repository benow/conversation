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

        // Collect all chunks up front
        var chunks = new List<string>();
        while (splitter.TryDequeue(out var chunk))
            chunks.Add(chunk!);
        var remaining = splitter.Flush();
        if (remaining != null)
            chunks.Add(remaining);

        if (chunks.Count == 0) return;

        _logger.LogInformation("[ClipboardTts] {ChunkCount} chunks queued", chunks.Count);

        // Synthesize + play with overlap: synthesize chunk N, then start chunk N+1 synthesis
        // while chunk N plays. Only 1 concurrent Replicate request at a time.
        string? nextFile = null;
        Task<string?>? nextSynthTask = null;

        for (var i = 0; i < chunks.Count; i++)
        {
            if (ct.IsCancellationRequested) break;

            string currentFile;

            if (i == 0)
            {
                // First chunk: synthesize immediately
                currentFile = await SynthesizeToFileAsync(chunks[0], persona, 1, ct);
            }
            else
            {
                // Use the file synthesized while previous chunk was playing
                currentFile = nextFile;
                nextFile = null;
            }

            // While this chunk plays, start synthesizing the next one (if any)
            var nextI = i + 1;
            if (nextI < chunks.Count && currentFile != null)
            {
                var synthIndex = nextI + 1;
                nextSynthTask = SynthesizeToFileAsync(chunks[nextI], persona, synthIndex, ct);
            }
            else
            {
                nextSynthTask = null;
            }

            // Play current chunk
            if (currentFile != null && !ct.IsCancellationRequested)
            {
                _logger.LogInformation("[ClipboardTts] Playing chunk {Index}/{Total}", i + 1, chunks.Count);
                try
                {
                    await _audioPlayer.PlayAsync(currentFile, cancellationToken: ct);
                }
                finally
                {
                    try { File.Delete(currentFile); } catch { }
                }
            }

            // Wait for next chunk's synthesis to finish (should be done or nearly done by now)
            if (nextSynthTask != null)
            {
                nextFile = await nextSynthTask;
                nextSynthTask = null;
            }
        }

        // Clean up if cancelled mid-synthesis
        if (nextSynthTask != null)
        {
            var trailing = await nextSynthTask;
            if (trailing != null) try { File.Delete(trailing); } catch { }
        }

        _logger.LogInformation("[ClipboardTts] Playback complete ({ChunkCount} chunks)", chunks.Count);
    }

    private async Task<string?> SynthesizeToFileAsync(string chunk, (VoicePersona Persona, string Key) persona, int index, CancellationToken ct)
    {
        try
        {
            _logger.LogInformation("[ClipboardTts] Synthesizing chunk {Index} ({Length} chars)", index, chunk.Length);
            await using var audioStream = await _ttsProvider.SynthesizeAsync(
                chunk, persona.Key, persona.Persona.Voice, persona.Persona.OpenAiInstructions,
                persona.Persona.Temperature, persona.Persona.Seed, ct);

            var tempFile = Path.Combine(Path.GetTempPath(), $"cbtts_{Guid.NewGuid():N}.wav");
            using (var fileStream = File.Create(tempFile))
                await audioStream.CopyToAsync(fileStream, ct);

            _logger.LogInformation("[ClipboardTts] Chunk {Index}: synthesized ({Bytes} bytes)", index, new FileInfo(tempFile).Length);
            return tempFile;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ClipboardTts] Chunk {Index}: synthesis failed: {Message}", index, ex.Message);
            return null;
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
