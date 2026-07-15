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
        // Adaptive chunking: start with configured max, adjust based on observed synthesis speed.
        // Target: synthesis time ≤ 80% of estimated playback time, so overlap covers the gap.
        const double TargetSynthRatio = 0.8;
        const int MinChunk = 200;
        const int MaxChunk = 3000;
        const double CharsPerSecond = 14.0; // rough TTS speech rate estimate

        var chunkSize = _settings.ClipboardTts.MaxChunkLength;
        var position = 0;
        var chunkIndex = 0;
        var synthMsPerChar = 5.0; // initial guess: 5ms per char (will adapt)
        var remainingText = text.AsSpan();

        string? nextFile = null;
        Task<string?>? nextSynthTask = null;
        string? nextChunkText = null;

        // Peek ahead: prepare first chunk
        var firstChunk = ExtractChunk(remainingText, chunkSize, ref position);
        if (firstChunk == null) return;
        var firstSw = Stopwatch.StartNew();
        var firstFile = await SynthesizeToFileAsync(firstChunk, persona, ++chunkIndex, ct);
        firstSw.Stop();
        synthMsPerChar = firstChunk.Length > 0 ? firstSw.ElapsedMilliseconds / (double)firstChunk.Length : synthMsPerChar;
        _logger.LogInformation("[ClipboardTts] Adaptive: initial synth {Ms}ms for {Len} chars ({Rate:F1}ms/char)",
            firstSw.ElapsedMilliseconds, firstChunk.Length, synthMsPerChar);

        // Start synthesizing chunk 2 while chunk 1 plays
        remainingText = text.AsSpan(position);
        nextChunkText = ExtractChunk(remainingText, chunkSize, ref position);
        if (nextChunkText != null)
            nextSynthTask = SynthesizeToFileAsync(nextChunkText, persona, ++chunkIndex, ct);
        else
            nextSynthTask = null;

        // Play chunk 1
        if (firstFile != null && !ct.IsCancellationRequested)
        {
            _logger.LogInformation("[ClipboardTts] Playing chunk {Index}", chunkIndex - 1);
            try { await _audioPlayer.PlayAsync(firstFile, cancellationToken: ct); }
            finally { try { File.Delete(firstFile); } catch { } }
        }

        // Loop: play pre-synthesized chunk, adapt size, synthesize next while playing
        while (nextChunkText != null && !ct.IsCancellationRequested)
        {
            // Wait for next chunk synthesis
            if (nextSynthTask != null)
            {
                nextFile = await nextSynthTask;
                nextSynthTask = null;
            }

            // Adapt chunk size based on synthesis speed
            AdaptChunkSize(ref chunkSize, synthMsPerChar, CharsPerSecond, TargetSynthRatio, MinChunk, MaxChunk);

            // Prepare the chunk after this one
            remainingText = text.AsSpan(position);
            var upcomingText = ExtractChunk(remainingText, chunkSize, ref position);
            if (upcomingText != null && nextFile != null)
                nextSynthTask = SynthesizeToFileAsync(upcomingText, persona, ++chunkIndex, ct);
            else
                nextSynthTask = null;

            // Play current chunk
            if (nextFile != null && !ct.IsCancellationRequested)
            {
                _logger.LogInformation("[ClipboardTts] Playing chunk {Index} ({ChunkSize} chars target)", chunkIndex - 1, chunkSize);
                try { await _audioPlayer.PlayAsync(nextFile, cancellationToken: ct); }
                finally { try { File.Delete(nextFile); } catch { } }
            }

            nextFile = null;
            nextChunkText = upcomingText;
        }

        // Clean up
        if (nextSynthTask != null)
        {
            var trailing = await nextSynthTask;
            if (trailing != null) try { File.Delete(trailing); } catch { }
        }

        _logger.LogInformation("[ClipboardTts] Playback complete ({ChunkCount} chunks, final chunk size {Size})", chunkIndex, chunkSize);
    }

    /// <summary>
    /// Extract a chunk from text, breaking at sentence boundaries within the budget.
    /// Returns null if nothing meaningful remains.
    /// </summary>
    private static string? ExtractChunk(ReadOnlySpan<char> text, int maxLen, ref int position)
    {
        if (text.Length == 0) return null;

        var budget = Math.Min(maxLen, text.Length);
        var chunk = text[..budget].ToString();

        // Try to break at the last sentence boundary within budget
        if (chunk.Length > 200)
        {
            var lastSentence = -1;
            for (var i = 0; i < chunk.Length - 1; i++)
            {
                if (chunk[i] is '.' or '!' or '?' && chunk[i + 1] is ' ' or '\n' or '\r')
                    lastSentence = i + 1;
            }
            if (lastSentence > chunk.Length / 2) // only break if we keep most of the budget
                chunk = chunk[..lastSentence];
        }

        chunk = chunk.Trim();
        if (chunk.Length < 20) return null;

        position += chunk.Length;
        // Skip whitespace between chunks
        while (position < text.Length && char.IsWhiteSpace(text[position]))
            position++;

        return chunk;
    }

    private static void AdaptChunkSize(ref int chunkSize, double synthMsPerChar, double charsPerSecond, double targetRatio, int min, int max)
    {
        // Estimated synthesis time = chunkSize * synthMsPerChar
        // Estimated playback time = chunkSize / charsPerSecond * 1000
        // We want synth ≤ targetRatio * playback
        // => chunkSize * synthMsPerChar ≤ targetRatio * chunkSize / charsPerSecond * 1000
        // => synthMsPerChar ≤ targetRatio * 1000 / charsPerSecond
        // => targetMsPerChar = targetRatio * 1000 / charsPerSecond
        var targetMsPerChar = targetRatio * 1000.0 / charsPerSecond;

        if (synthMsPerChar > targetMsPerChar)
        {
            // Synthesis is slow — shrink chunks so they fit in playback time
            var idealSize = (int)(targetMsPerChar / synthMsPerChar * chunkSize);
            chunkSize = Math.Max(min, Math.Min(max, idealSize));
        }
        else
        {
            // Synthesis is fast — grow chunks for fewer boundaries / better prosody
            var growth = Math.Min(max, (int)(chunkSize * 1.3));
            chunkSize = Math.Max(chunkSize, growth);
        }
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
