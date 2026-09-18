using System.Text;

namespace Benow.Conversation.Voice;

/// <summary>A flushed sentence plus whether a paragraph break (\n) followed it — the
/// TtsChunkPacer uses that to fire the rest-of-first-paragraph chunk.</summary>
public readonly record struct SentenceSegment(string Text, bool EndsParagraph);

/// <summary>
/// Accumulates text chunks and flushes at sentence boundaries (. ! ? \n).
/// This is the only buffering point in the pipeline — TTS needs complete
/// sentences for natural prosody. A hard cap flushes at the last clause boundary
/// (", " / space) when the model writes a long run-on with no terminator — roleplay
/// models routinely emit 400+ char "sentences", and without the cap the first TTS
/// chunk waits for the ENTIRE run-on to synthesize (2026-08-31: a 468-char first
/// sentence delayed first audio by 18s).
/// Ported from NASTV nastv-player-core/Voice/SentenceAccumulator.cs (2026-09-17, phase 1).
/// </summary>
public sealed class SentenceAccumulator
{
    private readonly StringBuilder _buf = new();
    private static readonly char[] Terminators = { '.', '!', '?', '\n', '\r' };

    /// <summary>Buffer size at which a terminator-less run-on is force-flushed at the
    /// last clause boundary. Above a single chunk's synthesis budget (~200c ≈ 8s on
    /// Replicate XTTS) but well above natural clause length.</summary>
    private const int RunOnCapChars = 200;
    /// <summary>Minimum length of a force-flushed run-on fragment — never cut this close
    /// to the head that the fragment is too tiny to synthesize well.</summary>
    private const int RunOnMinFlushChars = 40;

    public List<SentenceSegment> Add(string chunk)
    {
        var flushed = new List<SentenceSegment>();
        _buf.Append(chunk);

        while (_buf.Length > 0)
        {
            var idx = -1;
            for (var i = 0; i < _buf.Length; i++)
            {
                if (Terminators.Contains(_buf[i])) { idx = i; break; }
            }
            if (idx < 0)
            {
                // No terminator in the buffer. Normally we wait for one, but a run-on
                // past the cap must flush NOW — cut at the last clause boundary
                // (", " preferred over a bare space; never inside a number like "1,000").
                if (_buf.Length < RunOnCapChars) break;
                var window = _buf.ToString();
                var cut = window.LastIndexOf(", ", StringComparison.Ordinal);
                if (cut < RunOnMinFlushChars) cut = window.LastIndexOf(' ');
                if (cut < RunOnMinFlushChars) break; // no sane cut point — wait for a terminator
                flushed.Add(new SentenceSegment(window[..cut].Trim(), EndsParagraph: false));
                _buf.Remove(0, cut + 1); // keep the space/comma tail with the remainder
                continue;
            }

            var isParagraphBreak = _buf[idx] is '\n' or '\r';
            var sentence = _buf.ToString(0, idx + 1).Trim();
            _buf.Remove(0, idx + 1);
            if (sentence.Length >= 5)
                flushed.Add(new SentenceSegment(sentence, isParagraphBreak));
            else if (isParagraphBreak && flushed.Count > 0)
            {
                // A terminator with no sentence behind it (the \n after a "." on its own,
                // or a blank line) still ends the paragraph — mark the last real sentence.
                var last = flushed[^1];
                flushed[^1] = new SentenceSegment(last.Text, true);
            }
        }

        return flushed;
    }

    public string? FlushRemaining()
    {
        if (_buf.Length == 0) return null;
        var remaining = _buf.ToString().Trim();
        _buf.Clear();
        return remaining.Length >= 5 ? remaining : null;
    }

    public void Clear() => _buf.Clear();
}
