using System.Text;

namespace Benow.Conversation.Voice;

/// <summary>
/// Turns completed sentences into TTS synthesis chunks with three-stage pacing
/// (2026-08-29): the FIRST chunk is the first sentence (fast first audio), the SECOND
/// chunk is the rest of the first paragraph (fired at the paragraph break, or early if
/// the paragraph runs long), and everything after that batches at the steady-state
/// threshold. Before this, a long reply collapsed into "small first chunk → one huge
/// slow remainder", leaving a long silent gap after the opening sentence.
/// Pure and stateless per turn — create one per LLM response.
/// Ported from NASTV nastv-player-core/Voice/TtsChunkPacer.cs (2026-09-17, phase 1).
/// </summary>
public sealed class TtsChunkPacer
{
    /// <summary>First chunk floor: a bare "OK." waits for the next sentence instead of
    /// synthesizing almost nothing (XTTS quality needs a little text).</summary>
    private const int FirstMinChars = 40;
    /// <summary>Rest-of-first-paragraph cap: a wall-of-text paragraph must not delay the
    /// second chunk indefinitely. 480→300 (2026-08-31): Replicate XTTS synthesis runs
    /// ~11-16s for 300-400c, so 480c chunks held the next chunk's audio back ~18s.</summary>
    private const int ParagraphMaxChars = 300;
    /// <summary>Steady-state batching after the first paragraph (unchanged from the old
    /// LaterTtsMinChars).</summary>
    private const int LaterMinChars = 320;

    private readonly StringBuilder _pending = new();
    private int _stage; // 0 = awaiting first chunk, 1 = rest of first paragraph, 2+ = steady state

    /// <summary>Feed completed sentences; returns the chunk texts that crossed their stage
    /// threshold (0..n per call). Feed segments in stream order.</summary>
    public List<string> AddRange(IEnumerable<SentenceSegment> segments)
    {
        var emitted = new List<string>();
        foreach (var seg in segments)
        {
            _pending.Append(seg.Text).Append(' ');
            var crossed =
                _stage == 0 ? _pending.Length >= FirstMinChars :
                _stage == 1 ? (seg.EndsParagraph || _pending.Length >= ParagraphMaxChars) :
                _pending.Length >= LaterMinChars;
            if (crossed)
            {
                emitted.Add(_pending.ToString().Trim());
                _pending.Clear();
                _stage++;
            }
        }
        return emitted;
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
