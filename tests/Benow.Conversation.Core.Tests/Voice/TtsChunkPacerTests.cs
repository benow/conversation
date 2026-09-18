using Benow.Conversation.Voice;
using Xunit;

namespace Benow.Conversation.Core.Tests.Voice;

/// <summary>
/// Three-stage TTS pacing (2026-08-29): first chunk = first sentence, second = rest of the
/// first paragraph, then steady-state batches. Regression for the "first sentence → long
/// silent gap → everything else" complaint on long replies.
/// </summary>
public class TtsChunkPacerTests
{
    private static SentenceSegment S(string text, bool endsParagraph = false) => new(text, endsParagraph);

    [Fact]
    public void FirstChunk_IsFirstSentence_OnceFloorIsMet()
    {
        var pacer = new TtsChunkPacer();
        // 13 chars — below the 40-char floor, nothing fires yet.
        Assert.Empty(pacer.AddRange(new[] { S("Hello there.") }));
        var chunk = Assert.Single(pacer.AddRange(new[] { S("The bullpen is up here in the fifth inning, and the manager is walking to the mound.") }));
        Assert.StartsWith("Hello there.", chunk);
        Assert.Contains("fifth inning", chunk);
    }

    [Fact]
    public void SecondChunk_IsRestOfFirstParagraph_AtParagraphBreak()
    {
        var pacer = new TtsChunkPacer();
        pacer.AddRange(new[] { S("First sentence that is long enough to cross the floor easily.") });
        // Sentences inside the same paragraph (no paragraph break) stay buffered even past 320 chars.
        var mid = pacer.AddRange(new[] { S(Sentence(80)), S(Sentence(80)) });
        Assert.Empty(mid);
        // Paragraph break → the rest of the paragraph fires as the second chunk.
        var chunk = Assert.Single(pacer.AddRange(new[] { S(Sentence(30), endsParagraph: true) }));
        Assert.True(chunk.Length >= 150);
    }

    [Fact]
    public void LongParagraph_CapsBeforeTheBreak()
    {
        var pacer = new TtsChunkPacer();
        pacer.AddRange(new[] { S("First sentence that is long enough to cross the floor easily.") });
        // 4 × 150-char sentences with no paragraph break → the cap fires mid-paragraph.
        var fired = pacer.AddRange(new[] { S(Sentence(150)), S(Sentence(150)), S(Sentence(150)), S(Sentence(150)) });
        Assert.NotEmpty(fired);
    }

    [Fact]
    public void SteadyState_AfterFirstParagraph_BatchesAt320()
    {
        var pacer = new TtsChunkPacer();
        pacer.AddRange(new[] { S("First sentence that is long enough to cross the floor easily.") });
        pacer.AddRange(new[] { S(Sentence(40), endsParagraph: true) }); // paragraph closed
        // Under 320 chars → buffered.
        Assert.Empty(pacer.AddRange(new[] { S(Sentence(100)) }));
        // Crossing 320 chars → fires.
        var chunk = Assert.Single(pacer.AddRange(new[] { S(Sentence(230)) }));
        Assert.True(chunk.Length >= 320);
    }

    [Fact]
    public void Flush_ReturnsTrailingText_RegardlessOfSize()
    {
        var pacer = new TtsChunkPacer();
        pacer.AddRange(new[] { S("First sentence that is long enough to cross the floor easily.") });
        pacer.AddRange(new[] { S(Sentence(40), endsParagraph: true) });
        Assert.Empty(pacer.AddRange(new[] { S(Sentence(100)) }));
        var tail = pacer.Flush();
        Assert.NotNull(tail);
        Assert.Equal(100, tail!.Length);
        Assert.Null(pacer.Flush()); // second flush is empty
    }

    [Fact]
    public void Accumulator_NewlineTerminator_MarksParagraphEnd()
    {
        var acc = new SentenceAccumulator();
        var segs = acc.Add("One two three four five. Six seven eight nine ten.\n");
        Assert.Equal(2, segs.Count);
        Assert.False(segs[0].EndsParagraph);
        Assert.True(segs[1].EndsParagraph);
    }

    [Fact]
    public void Accumulator_BlankLine_MarksPreviousSentenceAsParagraphEnd()
    {
        var acc = new SentenceAccumulator();
        var segs = acc.Add("One two three four five.\n\nSix seven eight nine ten.");
        Assert.Equal(2, segs.Count);
        Assert.True(segs[0].EndsParagraph); // the blank line after it
        Assert.False(segs[1].EndsParagraph);
    }

    [Fact]
    public void Accumulator_RunOnPastCap_FlushesAtLastClauseBoundary()
    {
        // Regression (2026-08-31): a 468-char first "sentence" (no terminator until the
        // end) made the FIRST TTS chunk 468c → 18s before any audio. The accumulator must
        // force-flush at ", " once the buffer crosses the run-on cap.
        var acc = new SentenceAccumulator();
        var clause = new string('a', 60) + ", " + new string('b', 60) + ", " +
                     new string('c', 60) + ", " + new string('d', 60); // ~250c, no terminator
        var segs = acc.Add(clause);
        Assert.NotEmpty(segs);
        Assert.All(segs, s => Assert.False(s.EndsParagraph));
        // The cut lands at the last clause boundary — the d-tail stays buffered.
        Assert.Equal(new string('d', 60), acc.FlushRemaining());
    }

    [Fact]
    public void Accumulator_UnderCap_WaitsForTerminator()
    {
        var acc = new SentenceAccumulator();
        var segs = acc.Add(new string('a', 150) + " and more text without any terminator");
        Assert.Empty(segs);
    }

    [Fact]
    public void Accumulator_RunOnCut_NeverSplitsInsideNumber()
    {
        var acc = new SentenceAccumulator();
        // "1,000" is not a clause boundary — the cut must land on the last ", " before the y-run.
        var text = new string('x', 120) + ", I counted 1,000 stars today, " + new string('y', 120);
        var segs = acc.Add(text);
        var first = Assert.Single(segs);
        Assert.Contains("1,000", first.Text); // the number survived intact
        Assert.StartsWith(new string('x', 120), first.Text);
        Assert.Contains(new string('y', 120), acc.FlushRemaining());
    }

    private static string Sentence(int chars)
    {
        // Deterministic filler sentence of exactly ~chars length ending in a period.
        var words = "word word word word word word word word".Split(' ');
        var sb = new System.Text.StringBuilder();
        while (sb.Length < chars - 1) sb.Append(words[sb.Length % words.Length]).Append(' ');
        return sb.ToString().TrimEnd().PadRight(chars - 1).Substring(0, chars - 1) + ".";
    }
}
