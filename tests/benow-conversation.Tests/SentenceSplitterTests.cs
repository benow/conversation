using benow_conversation.Services;

namespace benow_conversation.Tests;

public class SentenceSplitterTests
{
    [Fact]
    public void SingleSentence_SplitsOnPeriodWithTrailingContent()
    {
        var splitter = new SentenceSplitter(20);
        splitter.Append("This is a sentence that is definitely long enough to split. And more.");

        Assert.True(splitter.TryDequeue(out var sentence));
        Assert.Equal("This is a sentence that is definitely long enough to split.", sentence);
    }

    [Fact]
    public void SingleSentence_RetrievedViaFlush()
    {
        var splitter = new SentenceSplitter(20);
        splitter.Append("This is a sentence that is definitely long enough to split.");

        Assert.False(splitter.TryDequeue(out _));
        var remaining = splitter.Flush();
        Assert.NotNull(remaining);
        Assert.Equal("This is a sentence that is definitely long enough to split.", remaining);
    }

    [Fact]
    public void MultipleSentences_InOneAppend()
    {
        var splitter = new SentenceSplitter(20);
        splitter.Append("First sentence that is long enough here. Second sentence also meets the length requirement. Third one follows.");

        Assert.True(splitter.TryDequeue(out var first));
        Assert.Contains("First sentence", first);

        Assert.True(splitter.TryDequeue(out var second));
        Assert.Contains("Second sentence", second);

        Assert.False(splitter.TryDequeue(out _));

        var remaining = splitter.Flush();
        Assert.NotNull(remaining);
        Assert.Contains("Third one", remaining);
    }

    [Fact]
    public void IncrementalAppend_OneWordAtATime()
    {
        var splitter = new SentenceSplitter(20);
        var words = "This is a test sentence that should be split eventually. More text follows here.";

        foreach (var ch in words)
            splitter.Append(ch.ToString());

        Assert.True(splitter.TryDequeue(out var sentence));
        Assert.True(sentence.Length >= 20);
    }

    [Fact]
    public void Abbreviations_NotSplit()
    {
        var splitter = new SentenceSplitter(20);
        splitter.Append("Dr. Smith went to the store to buy some groceries for dinner tonight.");

        Assert.False(splitter.TryDequeue(out _));
        var remaining = splitter.Flush();
        Assert.NotNull(remaining);
        Assert.Equal("Dr. Smith went to the store to buy some groceries for dinner tonight.", remaining);
    }

    [Fact]
    public void CodeFenceTracking_NoSplitInsideCodeBlock()
    {
        var splitter = new SentenceSplitter(20);
        splitter.Append("Here is some text before the code that is long enough. ");
        while (splitter.TryDequeue(out _)) { }
        splitter.Append("```var x = 1.0; var y = 2.0;``` This is after the code block and is long enough for sure. More text.");

        var sentences = new List<string>();
        while (splitter.TryDequeue(out var s))
            sentences.Add(s);

        Assert.True(sentences.Count >= 1);
        Assert.Contains("after the code block", sentences[0]);
    }

    [Fact]
    public void DoubleNewline_ParagraphBreak()
    {
        var splitter = new SentenceSplitter(20);
        splitter.Append("This is a paragraph with enough text\n\nThis is another paragraph here.");

        Assert.True(splitter.TryDequeue(out var first));
        Assert.Equal("This is a paragraph with enough text", first);

        Assert.False(splitter.TryDequeue(out _));

        var remaining = splitter.Flush();
        Assert.NotNull(remaining);
        Assert.Contains("This is another paragraph", remaining);
    }

    [Fact]
    public void Flush_EmitsRemainingText()
    {
        var splitter = new SentenceSplitter(20);
        splitter.Append("This is a complete sentence here. And a short tail");

        while (splitter.TryDequeue(out _)) { }

        var remaining = splitter.Flush();
        Assert.NotNull(remaining);
        Assert.Contains("short tail", remaining);
    }

    [Fact]
    public void Flush_ReturnsNullForShortText()
    {
        var splitter = new SentenceSplitter(20);
        splitter.Append("hi");

        var remaining = splitter.Flush();
        Assert.Null(remaining);
    }

    [Fact]
    public void Flush_ReturnsNullForEmpty()
    {
        var splitter = new SentenceSplitter(20);
        var remaining = splitter.Flush();
        Assert.Null(remaining);
    }

    [Fact]
    public void MinimumLengthThreshold_Respected()
    {
        var splitter = new SentenceSplitter(50);
        splitter.Append("Short. This is a much longer sentence that exceeds fifty characters in total length. And another one here.");

        var sentences = new List<string>();
        while (splitter.TryDequeue(out var s))
            sentences.Add(s);

        Assert.True(sentences.Count >= 1);
        Assert.True(sentences[0].Length >= 50, $"First sentence should be >= 50 chars but was {sentences[0].Length}: {sentences[0]}");
    }

    [Fact]
    public void EmptyAndWhitespace_FilteredOut()
    {
        var splitter = new SentenceSplitter(20);
        splitter.Append("");
        splitter.Append("   ");

        Assert.False(splitter.TryDequeue(out _));
        Assert.Null(splitter.Flush());
    }

    [Fact]
    public void ExclamationAndQuestion_SplitCorrectly()
    {
        var splitter = new SentenceSplitter(20);
        splitter.Append("What is the meaning of life? It is a deep philosophical question!");

        Assert.True(splitter.TryDequeue(out var first));
        Assert.EndsWith("?", first);

        Assert.False(splitter.TryDequeue(out _));

        var remaining = splitter.Flush();
        Assert.NotNull(remaining);
        Assert.EndsWith("!", remaining);
    }
}
