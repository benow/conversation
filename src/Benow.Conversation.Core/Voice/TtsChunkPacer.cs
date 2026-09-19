using System.Text;

namespace Benow.Conversation.Voice;

/// <summary>
/// Turns completed sentences into TTS synthesis chunks with ADAPTIVE pacing (2026-09-19):
/// the FIRST chunk is the first sentence (fast first audio), and every following chunk's
/// threshold grows from the PREVIOUS CHUNK'S ACTUAL LENGTH by <see cref="TtsChunkPacerOptions.GrowthFactor"/>.
///
/// Why growth (the "long pause after the first line" fix): a chunk gaps when its synthesis
/// takes longer than the previous chunk's audio. Replicate XTTS synthesizes at ~43ms/char and
/// plays at ~65ms/char, so next ≤ prev × ~1.5 always self-sustains; 1.4 adds safety. The old
/// fixed stages (first sentence → rest-of-paragraph up to 300c → 320c batches) put a ~13s
/// synthesis right after a ~4s first chunk — the audio ran dry mid-reply (heard live on the
/// TV, 2026-09-19). Growth starts small (the first chunk's size) and amortizes up to the cap.
///
/// Paragraph breaks still fire early (≥ FirstMinChars buffered) — a paragraph is a natural
/// TTS pause and prosody benefits.
/// Pure per turn — create one per LLM response. Ported from NASTV (2026-09-17, phase 1).
/// </summary>
public sealed class TtsChunkPacer
{
    private readonly TtsChunkPacerOptions _options;
    private readonly StringBuilder _pending = new();
    private int _stage;
    private int _lastEmittedChars;

    public TtsChunkPacer(TtsChunkPacerOptions? options = null) => _options = options ?? new TtsChunkPacerOptions();

    /// <summary>Feed completed sentences; returns the chunk texts that crossed their threshold
    /// (0..n per call). Feed segments in stream order.</summary>
    public List<string> AddRange(IEnumerable<SentenceSegment> segments)
    {
        var emitted = new List<string>();
        foreach (var seg in segments)
        {
            _pending.Append(seg.Text).Append(' ');
            // Stage 0: only the floor counts — the first chunk is the first sentence, so first
            // audio lands as early as quality allows. A paragraph end does NOT release a tiny
            // first chunk.
            // Stage 1+: the growth threshold, or an early fire at a paragraph break (natural pause).
            var crossed = _stage == 0
                ? _pending.Length >= _options.FirstMinChars
                : _pending.Length >= NextThreshold()
                    || (seg.EndsParagraph && _pending.Length >= _options.FirstMinChars);
            if (crossed)
            {
                emitted.Add(_pending.ToString().Trim());
                _lastEmittedChars = _pending.Length;
                _pending.Clear();
                _stage++;
            }
        }
        return emitted;
    }

    /// <summary>The next chunk's fire threshold: the previous chunk's size grown by the
    /// SPEED-ADJUSTED factor, clamped to [FirstMinChars, MaxChars]. Faster speech shortens each
    /// chunk's playback without shortening its synthesis, so the no-gap growth limit tightens:
    /// effective = GrowthFactor / PlaybackSpeed.</summary>
    private int NextThreshold()
    {
        var effective = _options.GrowthFactor / Math.Max(1.0, _options.PlaybackSpeed);
        var next = (int)(_lastEmittedChars * effective);
        return Math.Clamp(next, _options.FirstMinChars, _options.MaxChars);
    }

    /// <summary>Flush whatever is still buffered (end of the LLM stream) — the trailing
    /// chunk fires regardless of size so the tail of the reply is never dropped.</summary>
    public string? Flush()
    {
        if (_pending.Length == 0) return null;
        var text = _pending.ToString().Trim();
        _pending.Clear();
        return text.Length > 0 ? text : null;
    }
}

/// <summary>
/// Pacer tuning. Defaults: first chunk = first sentence (≥40c), then each chunk grows 1.4×
/// from the previous one up to 320c — synthesis time for chunk N+1 always fits inside chunk
/// N's playback, so playback never catches up with synthesis (the smoothness/immediacy
/// balance measured with the Lab's --bench; see the conversation repo AGENTS.md).
/// </summary>
public sealed record TtsChunkPacerOptions
{
    /// <summary>First chunk floor: a bare "OK." waits for the next sentence instead of
    /// synthesizing almost nothing (XTTS quality needs a little text).</summary>
    public int FirstMinChars { get; init; } = 40;
    /// <summary>
    /// Per-chunk growth of the fire threshold. Must stay ≤ ~1.5: a chunk gaps when its
    /// synthesis (≈43ms/char on Replicate) outlasts the previous chunk's audio (≈65ms/char),
    /// and 43 × 1.5 ≈ 65. Higher values trade smoothness for fewer provider calls.
    /// </summary>
    public double GrowthFactor { get; init; } = 1.4;
    /// <summary>Speech playback rate (1.0 = normal, 1.2 = 20% faster). Faster speech shortens
    /// each chunk's playback but NOT its synthesis, so the pacer tightens its growth by this
    /// factor to keep the no-gap property at any slider position.</summary>
    public double PlaybackSpeed { get; init; } = 1.0;
    /// <summary>Steady-state cap. 480→300 (2026-08-31, now the growth cap): huge chunks held
    /// the next chunk's audio back ~18s.</summary>
    public int MaxChars { get; init; } = 320;
}
