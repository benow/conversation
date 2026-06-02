using System.Text;

namespace benow_conversation.Services;

public class ParagraphSplitter
{
    private readonly StringBuilder _buffer = new();
    private readonly int _minLength;
    private readonly Queue<string> _completed = new();
    private bool _firstSentenceEmitted;
    private bool _inCodeFence;

    public ParagraphSplitter(int minLength = 20)
    {
        _minLength = minLength;
    }

    public void Append(string fragment)
    {
        if (string.IsNullOrEmpty(fragment)) return;
        _buffer.Append(fragment);
        Scan();
    }

    public bool TryDequeue(out string chunk)
    {
        return _completed.TryDequeue(out chunk!);
    }

    public string? Flush()
    {
        var remaining = _buffer.ToString().Trim();
        _buffer.Clear();
        if (string.IsNullOrWhiteSpace(remaining))
            return null;
        if (remaining.Length < _minLength / 2)
            return null;
        return remaining;
    }

    /// <summary>Returns true if the chunk contains text that was part of a previous dequeue — needed for multi-character reconstruction.</summary>
    public List<string> DequeuedChunks { get; } = new();

    private void Scan()
    {
        while (true)
        {
            var text = _buffer.ToString();
            if (text.Length == 0) break;

            // Track code fences
            int fenceIdx;
            while ((fenceIdx = text.IndexOf("```", StringComparison.Ordinal)) >= 0)
            {
                _inCodeFence = !_inCodeFence;
                _buffer.Remove(0, fenceIdx + 3);
                text = _buffer.ToString();
            }

            if (_inCodeFence) break;
            if (text.Length < _minLength) break;

            // Phase 1: emit first sentence eagerly for fast time-to-first-audio
            if (!_firstSentenceEmitted)
            {
                var sentenceEnd = FindSentenceEnd(text);
                if (sentenceEnd >= 0)
                {
                    var sentence = text[..sentenceEnd].Trim();
                    _buffer.Remove(0, sentenceEnd);
                    if (!string.IsNullOrWhiteSpace(sentence) && sentence.Length >= _minLength / 2)
                    {
                        _completed.Enqueue(sentence);
                        DequeuedChunks.Add(sentence);
                        _firstSentenceEmitted = true;
                    }
                    continue;
                }
            }

            // Phase 2: emit by paragraph (\n\n)
            var paraEnd = text.IndexOf("\n\n", StringComparison.Ordinal);
            if (paraEnd >= 0 && text[..paraEnd].Trim().Length >= _minLength / 2)
            {
                var para = text[..(paraEnd + 2)].Trim();
                _buffer.Remove(0, paraEnd + 2);
                if (!string.IsNullOrWhiteSpace(para))
                {
                    _completed.Enqueue(para);
                    DequeuedChunks.Add(para);
                }
                continue;
            }

            // Fallback: also split on single newline if paragraph is large enough
            if (_firstSentenceEmitted && paraEnd < 0)
            {
                var nlEnd = text.IndexOf('\n', StringComparison.Ordinal);
                var candidate = nlEnd >= 0 ? text[..nlEnd].Trim() : "";
                if (candidate.Length >= 120)
                {
                    _buffer.Remove(0, nlEnd + 1);
                    _completed.Enqueue(candidate);
                    DequeuedChunks.Add(candidate);
                    continue;
                }
            }

            break;
        }
    }

    private static int FindSentenceEnd(string text)
    {
        for (var i = 0; i < text.Length - 1; i++)
        {
            if (text[i] == '.' || text[i] == '!' || text[i] == '?')
            {
                var next = text[i + 1];
                if (next == ' ' || next == '\n' || next == '\r' || next == '\t')
                {
                    var candidate = text[..(i + 1)].Trim();
                    if (candidate.Length >= 20) // sentence must be reasonable
                        return i + 1;
                }
            }
        }
        return -1;
    }
}
