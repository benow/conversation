using System.Collections.Concurrent;
using System.Diagnostics;
using benow_conversation.Configuration;
using benow_conversation.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace benow_conversation.Services;

/// <summary>Stores a synthesized audio segment for replay.</summary>
public record StoredAudioSegment(int SequenceIndex, byte[] AudioData);

/// <summary>Plays multiple character segments in sequence using parallel TTS synthesis for overlap.</summary>
public class ParallelTtsPlayer
{
    /// <summary>Maps modifier names to TTS instruction fragments applied when the modifier is present on a segment.</summary>
    public static readonly Dictionary<string, string> ModifierMapping = new(StringComparer.OrdinalIgnoreCase)
    {
        ["whisper"] = "speak in a hushed, breathy tone, close-mic intimacy",
        ["laughing"] = "laugh while speaking, warm and amused",
        ["thoughtful"] = "speak thoughtfully, slower, contemplative",
        ["angry"] = "speak with frustration, raised intensity",
        ["sad"] = "speak sadly, subdued, melancholic",
        ["excited"] = "speak with excitement, energetic and breathless",
        ["sigh"] = "let out a sigh before speaking, then continue",
        ["quiet"] = "speak with intimate closeness, warm and gentle",
        ["narrate"] = "speak in a detached, story-telling voice",
        ["flirtatious"] = "speak playfully, teasing, with a seductive undertone",
        ["teasing"] = "speak in a mock-serious, playful ribbing tone",
        ["sudden"] = "speak startled, quick intake of breath then speak",
        ["thirsty"] = "speak with raw sexual hunger, voice dripping with desperate need, aching and insatiable"
    };

    private readonly ITtsProvider _ttsProvider;
    private readonly IPersistentAudioPipeline? _pipeline;
    private readonly IAudioPlayer _audioPlayer;
    private readonly IPersonaAllocator _personaAllocator;
    private readonly AudioFormatConverter _converter;
    private readonly ILogger<ParallelTtsPlayer> _logger;
    private List<StoredAudioSegment>? _lastPlayback;

    public ParallelTtsPlayer(
        ITtsProvider ttsProvider,
        IPersistentAudioPipeline? pipeline,
        IAudioPlayer audioPlayer,
        IPersonaAllocator personaAllocator,
        AudioFormatConverter converter,
        ILogger<ParallelTtsPlayer> logger)
    {
        _ttsProvider = ttsProvider;
        _pipeline = pipeline;
        _audioPlayer = audioPlayer;
        _personaAllocator = personaAllocator;
        _converter = converter;
        _logger = logger;
    }

    /// <summary>Synthesizes and plays all character segments in sequence order with parallel TTS pre-fetching.</summary>
    public async Task PlaySegmentsAsync(List<CharacterSegment> segments, CancellationToken ct)
    {
        await PlaySegmentsAsync(segments, ct, null);
    }

    /// <summary>Synthesizes and plays segments, invoking <paramref name="onSegmentStart"/> before each segment plays.</summary>
    public async Task PlaySegmentsAsync(List<CharacterSegment> segments, CancellationToken ct, Func<CharacterSegment, Task>? onSegmentStart)
    {
        if (segments.Count == 0) return;

        var pipelineSw = Stopwatch.StartNew();
        var storedSegments = new StoredAudioSegment[segments.Count];

        var tcss = new TaskCompletionSource<(Stream? Stream, long SynthMs, int Seq)>[segments.Count];
        for (int i = 0; i < segments.Count; i++)
            tcss[i] = new TaskCompletionSource<(Stream? Stream, long SynthMs, int Seq)>();
        var synthesizeStarted = new long[segments.Count];

        const int MaxParallelSynths = 3;
        using var throttle = new SemaphoreSlim(MaxParallelSynths, MaxParallelSynths);

        _ = Task.Run(async () =>
        {
            var producerWrappers = new Task[segments.Count];
            for (int i = 0; i < segments.Count; i++)
            {
                var idx = i;
                producerWrappers[i] = Task.Run(async () =>
                {
                    await throttle.WaitAsync(CancellationToken.None);
                    try
                    {
                        var seg = segments[idx];
                        var tcs = tcss[idx];
                        synthesizeStarted[idx] = Stopwatch.GetTimestamp();
                        await ProduceAsync(seg, idx, tcs, ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Producer task error: {Error}", ex.Message);
                        tcss[idx].TrySetResult((null, 0, idx));
                    }
                    finally
                    {
                        throttle.Release();
                    }
                });
            }
            try
            {
                await Task.WhenAll(producerWrappers);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Producer wrapper error: {Error}", ex.Message);
            }
        });

        var lastPlayEnd = Stopwatch.GetTimestamp();
        string? lastGender = null;
        for (int i = 0; i < segments.Count; i++)
        {
            if (ct.IsCancellationRequested) break;

            var waitSw = Stopwatch.StartNew();
            var (audioStream, synthMs, seq) = await tcss[i].Task;
            waitSw.Stop();

            if (audioStream == null) continue;

            var seg = segments[seq];

            if (onSegmentStart != null)
                await onSegmentStart(seg);

            var gapMs = (Stopwatch.GetTimestamp() - lastPlayEnd) / (Stopwatch.Frequency / 1000);
            var wasStalled = !tcss[i].Task.IsCompleted || waitSw.ElapsedMilliseconds > synthMs;

            _logger.LogInformation(
                "Playback #{Seq}: wait={WaitMs}ms, synth={SynthMs}ms, gap={GapMs}ms, stalled={Stalled}",
                seq, waitSw.ElapsedMilliseconds, synthMs, gapMs, wasStalled);

            var segGender = _personaAllocator.GetPersona(seg.PersonaKey!)?.Gender;
            if (_pipeline != null && lastGender != null && segGender != null && segGender != lastGender)
            {
                _logger.LogInformation(
                    "Playback #{Seq}: gender changed {LastGender} → {CurrentGender}, restarting ffplay",
                    seq, lastGender, segGender);
                await _pipeline.InterruptAsync();
                await Task.Delay(100, ct);
            }

            await using var _ = audioStream;

            using var buffer = new MemoryStream();
            await audioStream.CopyToAsync(buffer, ct);
            var audioBytes = buffer.ToArray();

            var sourceFormat = _ttsProvider.OutputFormat;
            audioBytes = await _converter.ConvertAsync(audioBytes, sourceFormat, ct);

            storedSegments[i] = new StoredAudioSegment(seq, audioBytes);
            lastGender = segGender;

            var playSw = Stopwatch.StartNew();

            if (_pipeline != null)
            {
                using var playMs = new MemoryStream(audioBytes);
                await _pipeline.PipeAsync(playMs, ct);
            }
            else
            {
                using var playMs = new MemoryStream(audioBytes);
                await _audioPlayer.PlayStreamAsync(playMs, "pcm", cancellationToken: ct);
            }

            playSw.Stop();
            lastPlayEnd = Stopwatch.GetTimestamp();

            _logger.LogInformation(
                "Playback #{Seq}: played={PlayMs}ms, size={Size} bytes", seq, playSw.ElapsedMilliseconds, audioBytes.Length);

            if (audioBytes.Length < 1024)
            {
                _logger.LogWarning("Playback #{Seq}: suspiciously small audio ({Size} bytes) — possible TTS artifact", seq, audioBytes.Length);
            }
        }

        _lastPlayback = [..storedSegments];
        pipelineSw.Stop();
        _logger.LogInformation("Pipeline total: {TotalMs}ms ({SegmentCount} segments)", pipelineSw.ElapsedMilliseconds, segments.Count);
    }

    /// <summary>Replays the last multi-character audio sequence from memory.</summary>
    public async Task ReplayLastAsync(CancellationToken ct)
    {
        if (_lastPlayback == null || _lastPlayback.Count == 0)
        {
            _logger.LogInformation("Replay requested but no previous playback stored");
            return;
        }

        _logger.LogInformation("Replaying {SegmentCount} stored segments", _lastPlayback.Count);

        var pipelineSw = Stopwatch.StartNew();
        foreach (var seg in _lastPlayback)
        {
            if (ct.IsCancellationRequested) break;

            var playSw = Stopwatch.StartNew();

            if (_pipeline != null)
            {
                using var ms = new MemoryStream(seg.AudioData);
                await _pipeline.PipeAsync(ms, ct);
            }
            else
            {
                using var ms = new MemoryStream(seg.AudioData);
                await _audioPlayer.PlayStreamAsync(ms, "pcm", cancellationToken: ct);
            }

            playSw.Stop();
            _logger.LogInformation("Replay #{Seq}: played={PlayMs}ms, size={Size} bytes",
                seg.SequenceIndex, playSw.ElapsedMilliseconds, seg.AudioData.Length);
        }

        pipelineSw.Stop();
        _logger.LogInformation("Replay total: {TotalMs}ms ({SegmentCount} segments)", pipelineSw.ElapsedMilliseconds, _lastPlayback.Count);
    }

    private const int MaxTtsRetries = 2;

    private async Task ProduceAsync(CharacterSegment segment, int seqIndex, TaskCompletionSource<(Stream? Stream, long SynthMs, int Seq)> tcs, CancellationToken ct)
    {
        var synthSw = Stopwatch.StartNew();
        Exception? lastEx = null;

        var personaKey = segment.PersonaKey
            ?? _personaAllocator.AllocateForCharacter(segment.CharacterName, segment.Gender);
        var persona = personaKey != null ? _personaAllocator.GetPersona(personaKey) : null;

        if (persona == null)
        {
            _logger.LogWarning("No persona for key '{Key}', skipping segment #{Seq} for '{Character}'", personaKey, seqIndex, segment.CharacterName);
            tcs.TrySetResult((null, 0, seqIndex));
            return;
        }

        var instructions = ComposeInstructions(segment, persona);
        var baseTemp = persona.Temperature ?? 0.65;
        var temp = baseTemp + Random.Shared.NextDouble() * 0.1 - 0.05;
        if (segment.IsThought) temp += 0.05;
        temp = Math.Clamp(temp, 0.0, 1.0);

        for (int attempt = 0; attempt <= MaxTtsRetries; attempt++)
        {
            if (ct.IsCancellationRequested)
            {
                tcs.TrySetCanceled();
                return;
            }

            try
            {
                if (attempt > 0)
                {
                    var delay = Math.Min(500 * (1 << (attempt - 1)), 2000);
                    _logger.LogInformation("Synth #{Seq}: retry attempt {Attempt}/{MaxRetries} after {Delay}ms", seqIndex, attempt, MaxTtsRetries, delay);
                    await Task.Delay(delay, ct);
                }

                var attemptSw = Stopwatch.StartNew();
                _logger.LogInformation(
                    "Synth #{Seq}: char={Character}, persona={Persona} (voice={Voice}), temp={Temp:F3}, thought={IsThought}, modifier={Modifier}, attempt={Attempt}, text=\"{TextPreview}\"",
                    seqIndex, segment.CharacterName, personaKey, persona.Voice,
                    temp, segment.IsThought, segment.Modifier ?? "(none)", attempt,
                    segment.SpokenText.Length > 60 ? segment.SpokenText[..60] + "..." : segment.SpokenText);

                var audioStream = await _ttsProvider.SynthesizeAsync(
                    segment.SpokenText, personaKey!, persona.Voice, instructions, temp, null, ct);

                attemptSw.Stop();
                synthSw.Stop();

                synthSw.Stop();
                _logger.LogInformation("Synth #{Seq}: completed in {Ms}ms (attempt {Attempt})", seqIndex, attemptSw.ElapsedMilliseconds, attempt);
                tcs.TrySetResult((audioStream, synthSw.ElapsedMilliseconds, seqIndex));
                return;
            }
            catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
            {
                tcs.TrySetCanceled(ex.CancellationToken);
                return;
            }
            catch (Exception ex)
            {
                lastEx = ex;
                _logger.LogWarning(ex, "Synth #{Seq} failed for '{Character}' (attempt {Attempt}/{MaxRetries}): {Error}", seqIndex, segment.CharacterName, attempt, MaxTtsRetries, ex.Message);
            }
        }

        _logger.LogError(lastEx, "Synth #{Seq}: all {MaxRetries} retries exhausted for '{Character}' — segment SKIPPED", seqIndex, MaxTtsRetries + 1, segment.CharacterName);
        tcs.TrySetResult((null, 0, seqIndex));
    }

    private static string ComposeInstructions(CharacterSegment segment, VoicePersona persona)
    {
        var instructions = persona.OpenAiInstructions ?? "";

        if (!string.IsNullOrEmpty(segment.Modifier) &&
            ModifierMapping.TryGetValue(segment.Modifier, out var modifierText))
        {
            instructions += ". " + modifierText;
        }

        if (segment.IsThought && !string.IsNullOrEmpty(persona.ThoughtInstructions))
        {
            instructions += ". " + persona.ThoughtInstructions;
        }

        if (persona.Gender == "F")
            instructions += ". Speak in a distinctly feminine, higher-pitched vocal register.";

        return instructions;
    }
}
