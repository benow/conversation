using System.Text;

namespace benow_conversation.Services;

public class ParagraphSplitter
{
    private readonly StringBuilder _buffer = new();
    private readonly int _minLength;
    private readonly int _maxChunkLength;
    private readonly Queue<string> _completed = new();
    private bool _firstSentenceEmitted;
    private bool _inCodeFence;

    public const int DefaultMaxChunkLength = 3000;

    public ParagraphSplitter(int minLength = 20, int maxChunkLength = DefaultMaxChunkLength)
    {
        _minLength = minLength;
        _maxChunkLength = maxChunkLength;
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

            // Code fence handling: only strip ``` at buffer start to avoid losing text before fences
            if (text.StartsWith("```"))
            {
                _inCodeFence = !_inCodeFence;
                _buffer.Remove(0, 3);
                text = _buffer.ToString();
                if (_inCodeFence) break;
                continue;
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

            // Max chunk enforcement: force-split long text to avoid TTS API character limits
            if (text.Length > _maxChunkLength)
            {
                var cutPoint = FindSentenceEnd(text, _maxChunkLength);
                if (cutPoint < _minLength / 2)
                {
                    cutPoint = FindLastSpace(text, _maxChunkLength);
                    if (cutPoint < _minLength / 2)
                        cutPoint = _maxChunkLength;
                }
                var chunk = text[..cutPoint].Trim();
                _buffer.Remove(0, cutPoint);
                if (!string.IsNullOrWhiteSpace(chunk) && chunk.Length >= _minLength / 2)
                {
                    _completed.Enqueue(chunk);
                    DequeuedChunks.Add(chunk);
                }
                continue;
            }

            break;
        }
    }

    private static int FindSentenceEnd(string text)
    {
        return FindSentenceEnd(text, text.Length);
    }

    private static int FindSentenceEnd(string text, int maxPos)
    {
        var limit = Math.Min(text.Length - 1, maxPos);
        for (var i = 0; i < limit; i++)
        {
            if (text[i] == '.' || text[i] == '!' || text[i] == '?')
            {
                var next = text[i + 1];
                if (next == ' ' || next == '\n' || next == '\r' || next == '\t')
                {
                    var candidate = text[..(i + 1)].Trim();
                    if (candidate.Length >= 20)
                        return i + 1;
                }
            }
        }
        return -1;
    }

    private static int FindLastSpace(string text, int maxPos)
    {
        for (var i = Math.Min(maxPos - 1, text.Length - 1); i >= 0; i--)
        {
            if (text[i] == ' ' || text[i] == '\n')
                return i + 1;
        }
        return -1;
    }
}
