using Benow.Conversation.Voice;
using Xunit;

namespace Benow.Conversation.Core.Tests.Voice;

/// <summary>
/// Adaptive TTS pacing (2026-09-19): first chunk = first sentence, then each chunk's threshold
/// grows ~1.4× from the previous chunk's ACTUAL length (capped) — so chunk N+1's synthesis
/// always fits inside chunk N's playback and the audio never runs dry mid-reply. Regression
/// context: the fixed stages (first sentence → up-to-300c paragraph → 320c batches) put a ~13s
/// synthesis right after a ~4s first chunk; the gap was audible on the TV.
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
    public void SecondChunk_FiresWhenGrowthFromTheFirstChunkIsCrossed()
    {
        var pacer = new TtsChunkPacer();
        var first = Assert.Single(pacer.AddRange(new[] { S("First sentence that is long enough to cross the floor easily.") }));
        // Threshold = 61c × 1.4 ≈ 85c: an 80c sentence stays buffered...
        Assert.Empty(pacer.AddRange(new[] { S(Sentence(80)) }));
        // ...but the next sentence crosses it (161 ≥ 85) and fires as the second chunk.
        var chunk = Assert.Single(pacer.AddRange(new[] { S(Sentence(30)) }));
        Assert.True(chunk.Length >= 85);
        Assert.True(chunk.Length < 200);
    }

    [Fact]
    public void ParagraphBreak_FiresEarly_AnyStageAfterTheFirst()
    {
        var pacer = new TtsChunkPacer();
        pacer.AddRange(new[] { S("First sentence that is long enough to cross the floor easily.") });
        // Only 40c buffered, but the paragraph ends — fire early (natural TTS pause).
        var chunk = Assert.Single(pacer.AddRange(new[] { S(Sentence(40), endsParagraph: true) }));
        Assert.True(chunk.Length >= 40);
    }

    [Fact]
    public void Growth_NeverExceedsTheCap_AndStaysAboveTheFloor()
    {
        var pacer = new TtsChunkPacer(new TtsChunkPacerOptions { MaxChars = 200 });
        pacer.AddRange(new[] { S("First sentence that is long enough to cross the floor easily.") }); // 61c

        var lengths = new List<int>();
        for (var i = 0; i < 12; i++)
        {
            foreach (var chunk in pacer.AddRange(new[] { S(Sentence(100)) }))
                lengths.Add(chunk.Length);
        }

        Assert.NotEmpty(lengths);
        Assert.All(lengths, l => Assert.InRange(l, 40, 400)); // sanity: each chunk is sane
        // Monotone growth toward the cap: no emitted chunk may be smaller than half the previous
        // (the threshold grows; the crossing overshoot is bounded by one sentence).
        for (var i = 1; i < lengths.Count; i++)
            Assert.True(lengths[i] >= lengths[i - 1] / 2, $"chunk {i} collapsed: {lengths[i - 1]} → {lengths[i]}");
        Assert.True(lengths[^1] >= 150, $"should approach the cap; last={lengths[^1]}");
    }

    [Fact]
    public void GrowthStaysWithinTwofold_ThePracticalNoLongGapBound()
    {
        // Strict no-gap (next synthesis ≤ prev playback ⇒ ratio ≤ 1.5) is impossible with
        // sentence-quantized text: sentences arrive whole, so a chunk overshoots its threshold.
        // The property that matters (the TV complaint was a 60c → 300c step, ratio 5×): no
        // chunk may exceed ~2× the previous one — worst-case gap stays ~1-2s, never 10s.
        // Uniform 100c sentences ARE the worst case for overshoot (threshold 141 → fires at
        // 201 = exactly two sentences); real text varies and lands well under this.
        const double synthMsPerChar = 43, playMsPerChar = 65;
        var pacer = new TtsChunkPacer();
        pacer.AddRange(new[] { S("First sentence that is long enough to cross the floor easily.") }); // 61c

        var lengths = new List<int> { 61 };
        for (var i = 0; i < 10; i++)
        {
            foreach (var chunk in pacer.AddRange(new[] { S(Sentence(100)) }))
                lengths.Add(chunk.Length);
        }
        Assert.True(lengths.Count >= 4, $"should emit several chunks, got {lengths.Count}");
        for (var i = 1; i < lengths.Count; i++)
        {
            Assert.True(lengths[i] <= lengths[i - 1] * 2.05,
                $"chunk {i} jumped {lengths[i - 1]} → {lengths[i]} (the 5× step that silenced the TV)");
            // And the resulting gap must be small: synthesis over prev playback under ~2.5s.
            var gap = lengths[i] * synthMsPerChar - lengths[i - 1] * playMsPerChar;
            Assert.True(gap <= 2500, $"chunk {i} ({lengths[i]}c after {lengths[i - 1]}c) would gap {gap}ms");
        }
    }

    [Fact]
    public void Flush_ReturnsTrailingText_RegardlessOfSize()
    {
        var pacer = new TtsChunkPacer();
        pacer.AddRange(new[] { S("First sentence that is long enough to cross the floor easily.") });
        // A short tail below the growth threshold stays buffered (nothing fires), and the
        // end-of-stream flush releases it regardless of size.
        Assert.Empty(pacer.AddRange(new[] { S(Sentence(30)) }));
        var tail = pacer.Flush();
        Assert.NotNull(tail);
        Assert.Equal(30, tail!.Length);
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

    [Fact]
    public void PlaybackSpeed_TightensGrowth_FasterSpeechSmallerChunks()
    {
        // At rate 1.2 the speech plays 1.2× shorter, so chunk N+1's synthesis must fit in
        // LESS playback cover — the pacer compensates by tightening its growth (1.4 / 1.2).
        string[] Feed(PacerFactory f)
        {
            var pacer = f();
            var chunks = new List<string>();
            chunks.AddRange(pacer.AddRange(new[] { S("First sentence that is long enough to cross the floor easily.") }));
            for (var i = 0; i < 6; i++)
                chunks.AddRange(pacer.AddRange(new[] { S(Sentence(60)) }));
            return chunks.ToArray();
        }
        var normal = Feed(() => new TtsChunkPacer(new TtsChunkPacerOptions { PlaybackSpeed = 1.0 }));
        var faster = Feed(() => new TtsChunkPacer(new TtsChunkPacerOptions { PlaybackSpeed = 1.2 }));
        // Same stream, tighter thresholds → the fast-rate pacer never emits bigger chunks.
        for (var i = 1; i < Math.Min(normal.Length, faster.Length); i++)
            Assert.True(faster[i].Length <= normal[i].Length,
                $"speed 1.2 chunk {i} ({faster[i].Length}c) grew past speed 1.0 ({normal[i].Length}c)");
    }

    private delegate TtsChunkPacer PacerFactory();
}
