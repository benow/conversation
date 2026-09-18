using System.Text;
using Benow.Conversation.Audio;
using Benow.Conversation.Llm;
using Benow.Conversation.Voice;
using Microsoft.Extensions.Logging;

namespace Benow.Conversation.Engine;

/// <summary>
/// Events a conversation session raises. NASTV phase-4 maps these onto its SSE voice events;
/// the desktop app maps them onto its UI (and, in NASTV-backed mode, onto the server's SSE).
/// </summary>
public interface IConversationEventSink
{
    void SessionStarted();
    void SessionEnded(long durationMs);
    /// <summary>Live transcript of the segment currently being transcribed (may be revised).</summary>
    void TranscriptPartial(string text);
    /// <summary>Final transcript for the turn — emitted exactly once.</summary>
    void TranscriptFinal(string text, bool corrected);
    void LlmStarted();
    void LlmChunk(string text);
    void LlmFinal(string text, long ms);
    void TtsStarted(string text);
    void TtsDone(string text, long audioMs);
    void Error(string stage, string message);
}

/// <summary>No-op sink (tests, headless runs that only care about the return values).</summary>
public sealed class NullEventSink : IConversationEventSink
{
    public static readonly NullEventSink Instance = new();
    public void SessionStarted() { }
    public void SessionEnded(long durationMs) { }
    public void TranscriptPartial(string text) { }
    public void TranscriptFinal(string text, bool corrected) { }
    public void LlmStarted() { }
    public void LlmChunk(string text) { }
    public void LlmFinal(string text, long ms) { }
    public void TtsStarted(string text) { }
    public void TtsDone(string text, long audioMs) { }
    public void Error(string stage, string message) { }
}

/// <summary>Session tuning (defaults mirror NASTV's proven values).</summary>
public sealed record EngineOptions
{
    /// <summary>Full-audio correction cap: re-transcribe at most this much audio at end-of-turn.</summary>
    public int FullAudioCapSeconds { get; init; } = 30;
    /// <summary>Run the end-of-turn full-audio correction pass (accuracy; one extra STT call).</summary>
    public bool FullAudioCorrection { get; init; } = true;
    /// <summary>Speak LLM replies through TTS (the per-conversation mute toggle flips Muted at runtime).</summary>
    public bool SpeakReplies { get; init; } = true;
    /// <summary>Warm the ffplay process at session start so the first chunk doesn't pay process start.</summary>
    public bool PrewarmPlayback { get; init; } = true;
}

/// <summary>
/// The conversation engine: frames in → VAD → STT → transcript; text → LLM → sentence-paced
/// TTS → playback. This is the Local-mode brain (plan §4.6): it talks ONLY to the configured
/// AI providers — no server, no plugin.
///
/// Engine-level mute contract (plan review 2026-09-11): when <see cref="Muted"/> is true,
/// replies are NOT synthesized at all — the TTS provider is never called. Never
/// synthesize-then-silence.
/// </summary>
public sealed class ConversationEngine : IAsyncDisposable
{
    private readonly ILogger<ConversationEngine> _logger;
    private readonly EngineOptions _options;
    private readonly ITranscriptionService _stt;
    private readonly ChatClient _chat;
    private readonly ITtsService _tts;
    private readonly SpeechQueue _speech;
    private readonly IConversationEventSink _sink;

    private VoiceVAD? _vad;
    private readonly List<byte[]> _turnSegments = new();
    private readonly StringBuilder _partialText = new();
    private readonly SemaphoreSlim _transcribeLock = new(1, 1);
    private long _sessionStartMs;
    private bool _turnActive;

    public ConversationEngine(
        ILogger<ConversationEngine> logger,
        ITranscriptionService stt,
        ChatClient chat,
        ITtsService tts,
        SpeechQueue speech,
        EngineOptions? options = null,
        IConversationEventSink? sink = null)
    {
        _logger = logger;
        _stt = stt;
        _chat = chat;
        _tts = tts;
        _speech = speech;
        _options = options ?? new EngineOptions();
        _sink = sink ?? NullEventSink.Instance;
    }

    /// <summary>
    /// Per-conversation TTS mute (plan §4.4). True = replies are never synthesized —
    /// the TTS provider is not called (no spend, no latency).
    /// </summary>
    public bool Muted { get; set; }

    /// <summary>Conversation history for the next turn (client-owned, oldest first).</summary>
    public List<ChatHistoryMessage> History { get; } = new();

    // ---- Voice input ----

    /// <summary>Opens a listening session; feed frames with <see cref="PushFrameAsync"/>.</summary>
    public void BeginSession()
    {
        _vad ??= new VoiceVAD(Microsoft.Extensions.Logging.Abstractions.NullLogger<VoiceVAD>.Instance);
        _turnSegments.Clear();
        _partialText.Clear();
        _sessionStartMs = Environment.TickCount64;
        _turnActive = true;
        _sink.SessionStarted();
        _logger.LogInformation("[engine] session started (muted={Muted})", Muted);
    }

    /// <summary>Feeds one PCM frame (16kHz mono s16le, 640 bytes). VAD closes segments internally.</summary>
    public async Task PushFrameAsync(byte[] frame, CancellationToken ct = default)
    {
        if (_vad == null || !_turnActive) return;
        _vad.Push(frame);

        foreach (var segment in _vad.DrainClosedSegments())
        {
            _turnSegments.Add(segment);
            await TranscribeSegmentAsync(segment, ct);
        }
    }

    /// <summary>
    /// Ends the session (button release), flushes the open segment, and runs the full-audio
    /// correction pass — emitting exactly ONE final transcript (the NASTV fix for the
    /// duplicate-final bug: one final, best text).
    /// </summary>
    public async Task<string> EndSessionAsync(CancellationToken ct = default)
    {
        if (_vad == null || !_turnActive) return "";
        _turnActive = false;

        var open = _vad.FlushOpen();
        if (open is { Length: > 0 })
        {
            _turnSegments.Add(open);
            await TranscribeSegmentAsync(open, ct);
        }

        var assembled = _partialText.ToString().Trim();
        var final = assembled;
        var corrected = false;

        if (_options.FullAudioCorrection && _turnSegments.Count > 0)
        {
            var capBytes = _options.FullAudioCapSeconds * 16000 * 2;
            var total = _turnSegments.Sum(s => s.Length);

            if (total > capBytes)
            {
                // The correction pass re-transcribes a BOUNDED window — correct for a spoken
                // turn (a question), wrong for a long dictation. Never let it truncate: keep
                // the segment-assembled transcript (caught live 2026-09-18: a 5-minute
                // dictation was silently reduced to its first 30 seconds).
                _logger.LogInformation("[engine] turn is {Seconds:F0}s (> {Cap}s correction window) — keeping the assembled transcript, skipping correction",
                    total / 32000.0, _options.FullAudioCapSeconds);
            }
            else
            {
                var all = new byte[total];
                var offset = 0;
                foreach (var seg in _turnSegments)
                {
                    Array.Copy(seg, 0, all, offset, seg.Length);
                    offset += seg.Length;
                }

                var correctedText = await _stt.TranscribeSegmentAsync(all, -1, ct);
                if (!string.IsNullOrWhiteSpace(correctedText))
                {
                    var candidate = correctedText.Trim();
                    // Guardrail: a "correction" that loses a large share of the content is a
                    // provider hiccup, not a correction — keep the assembled text.
                    if (assembled.Length > 0 && candidate.Length < assembled.Length * 0.8)
                    {
                        _logger.LogWarning("[engine] correction returned {Cand}c vs assembled {Asm}c (<80%) — keeping the assembled transcript",
                            candidate.Length, assembled.Length);
                    }
                    else
                    {
                        corrected = !string.Equals(candidate, assembled, StringComparison.Ordinal);
                        final = candidate;
                        _logger.LogInformation("[engine] full-audio correction: \"{Corrected}\" (was \"{Assembled}\", changed={Changed})",
                            Truncate(final), Truncate(assembled), corrected);
                    }
                }
            }
        }

        _sink.TranscriptFinal(final, corrected);
        _sink.SessionEnded(Environment.TickCount64 - _sessionStartMs);
        _logger.LogInformation("[engine] session ended: {} segments, {} chars, muted={Muted}",
            _turnSegments.Count, final.Length, Muted);
        _vad = null;
        return final;
    }

    private async Task TranscribeSegmentAsync(byte[] segment, CancellationToken ct)
    {
        await _transcribeLock.WaitAsync(ct);
        try
        {
            var seq = _turnSegments.Count;
            var text = await _stt.TranscribeSegmentAsync(segment, seq, ct);
            if (string.IsNullOrWhiteSpace(text))
            {
                _logger.LogWarning("[engine] segment {Seq} produced no text ({Bytes}B) — leaving a gap in this turn", seq, segment.Length);
                return;
            }
            if (_partialText.Length > 0) _partialText.Append(' ');
            _partialText.Append(text);
            _sink.TranscriptPartial(_partialText.ToString());
        }
        finally
        {
            _transcribeLock.Release();
        }
    }

    // ---- Turns ----

    /// <summary>
    /// Runs a full turn: LLM (streamed to the sink) → sentence accumulation → three-stage TTS
    /// pacing → speech queue. With <see cref="Muted"/> the TTS path is skipped entirely.
    /// </summary>
    public async Task<ChatResult?> ConverseAsync(string transcript, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(transcript)) return null;
        _sink.LlmStarted();

        // A new turn barges in over any audio still playing from the previous one — but the
        // chunks OF this turn must queue in sequence (a per-chunk cancel would make every
        // chunk kill its predecessor; caught live 2026-09-18 by the phase-2 e2e run).
        _speech.FlushAndCancel();

        var accumulator = new SentenceAccumulator();
        var pacer = new TtsChunkPacer();
        var spokenText = new StringBuilder();

        var result = await _chat.ChatAsync(new ChatRequest
        {
            Transcript = transcript,
            History = History.Count > 0 ? new List<ChatHistoryMessage>(History) : null
        }, chunk =>
        {
            _sink.LlmChunk(chunk);
            spokenText.Append(chunk);
            // Progressive TTS: sentence accumulator → pacer → queue, as sentences complete.
            var segments = accumulator.Add(chunk);
            foreach (var piece in pacer.AddRange(segments))
                EnqueueSpeech(piece);
        }, ct);

        // Tail: flush the accumulator + pacer so the end of the reply is never dropped.
        var remaining = accumulator.FlushRemaining();
        if (remaining != null)
        {
            foreach (var piece in pacer.AddRange(new[] { new SentenceSegment(remaining, false) }))
                EnqueueSpeech(piece);
        }
        var tail = pacer.Flush();
        if (tail != null) EnqueueSpeech(tail);

        if (result != null)
        {
            _sink.LlmFinal(result.Text, result.TotalMs);
            if (result.Text.Length > 0)
            {
                History.Add(new ChatHistoryMessage("user", transcript));
                History.Add(new ChatHistoryMessage("assistant", result.Text));
            }
            _logger.LogInformation("[engine] turn: {}ch {Ms}ms (extractor {ExMs}ms, speaker {SpMs}ms, tools={Tools})",
                result.Chunks, result.TotalMs, result.ExtractorMs, result.SpeakerMs, result.UsedTools);
        }
        return result;
    }

    private void EnqueueSpeech(string text)
    {
        if (Muted)
        {
            // Engine-level mute: never call the TTS provider (plan §4.4 — no spend, no latency).
            _logger.LogDebug("[engine] muted — skipping synthesis of {Chars}c", text.Length);
            return;
        }
        if (!_options.SpeakReplies) return;
        _sink.TtsStarted(text);
        // cancelCurrent: false — chunks of one reply play in sequence (progressive TTS).
        _speech.Enqueue(text, cancelCurrent: false);
    }

    /// <summary>Speaks text directly (replay / test voice) — honors mute.</summary>
    public void Speak(string text)
    {
        if (Muted)
        {
            _logger.LogInformation("[engine] muted — replay suppressed ({Chars}c)", text?.Length ?? 0);
            return;
        }
        if (!string.IsNullOrWhiteSpace(text)) _speech.Enqueue(text);
    }

    public void Interrupt()
    {
        _logger.LogInformation("[engine] interrupt — flushing speech queue");
        _speech.FlushAndCancel();
    }

    public Task StartAsync(CancellationToken ct) => _speech.StartAsync(ct);

    private static string Truncate(string s) => s.Length > 200 ? s[..200] + "…" : s;

    public async ValueTask DisposeAsync()
    {
        _transcribeLock.Dispose();
        await _speech.DisposeAsync();
    }
}
