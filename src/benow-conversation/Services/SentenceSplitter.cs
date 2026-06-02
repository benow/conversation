using System.Text;

namespace benow_conversation.Services;

public class SentenceSplitter
{
    private readonly StringBuilder _buffer = new();
    private readonly int _minSentenceLength;
    private readonly Queue<string> _completed = new();
    private bool _inCodeFence;

    public SentenceSplitter(int minSentenceLength = 20)
    {
        _minSentenceLength = minSentenceLength;
    }

    public void Append(string fragment)
    {
        if (string.IsNullOrEmpty(fragment)) return;
        _buffer.Append(fragment);
        Scan();
    }

    public bool TryDequeue(out string sentence)
    {
        return _completed.TryDequeue(out sentence!);
    }

    public string? Flush()
    {
        var remaining = _buffer.ToString().Trim();
        _buffer.Clear();
        if (string.IsNullOrWhiteSpace(remaining))
            return null;
        if (remaining.Length < _minSentenceLength / 2)
            return null;
        return remaining;
    }

    private void Scan()
    {
        while (true)
        {
            var text = _buffer.ToString();
            if (text.Length == 0) break;

            int fenceIdx;
            while ((fenceIdx = text.IndexOf("```", StringComparison.Ordinal)) >= 0)
            {
                _inCodeFence = !_inCodeFence;
                _buffer.Remove(0, fenceIdx + 3);
                text = _buffer.ToString();
            }

            if (_inCodeFence) break;
            if (text.Length < _minSentenceLength) break;

            var splitAt = -1;
            for (var i = 0; i < text.Length - 1; i++)
            {
                var c = text[i];
                if (c != '.' && c != '!' && c != '?') continue;
                var next = text[i + 1];
                if (next == ' ' || next == '\n' || next == '\r' || next == '\t')
                {
                    var candidate = text[..(i + 1)].Trim();
                    if (candidate.Length >= _minSentenceLength)
                    {
                        splitAt = i + 1;
                        break;
                    }
                }
            }

            if (splitAt < 0)
            {
                var dblNl = text.IndexOf("\n\n", StringComparison.Ordinal);
                if (dblNl >= 0 && text[..dblNl].Trim().Length >= _minSentenceLength)
                    splitAt = dblNl + 2;
            }

            if (splitAt < 0) break;

            var sentence = text[..splitAt].Trim();
            _buffer.Remove(0, splitAt);

            if (!string.IsNullOrWhiteSpace(sentence) && sentence.Length >= _minSentenceLength / 2)
                _completed.Enqueue(sentence);
        }
    }
}
