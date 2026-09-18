using Benow.Conversation.Llm;
using Benow.Conversation.Voice;
using Xunit;

namespace Benow.Conversation.Core.Tests.Llm;

public class LlmProtocolTests
{
    // ---- ExtractToolEnvelope (textual tool-calling fallback) ----

    [Fact]
    public void ExtractToolEnvelope_PlainEnvelope_Parses()
    {
        var r = LlmProtocol.ExtractToolEnvelope(
            "Sure!\n{\"tool\": \"search\", \"arguments\": {\"query\": \"blue jays\"}}");
        Assert.NotNull(r);
        Assert.Equal("search", r!.Value.toolName);
        Assert.Contains("blue jays", r.Value.argumentsJson);
    }

    [Fact]
    public void ExtractToolEnvelope_LeadingProseAndTrailingText_StillParses()
    {
        var r = LlmProtocol.ExtractToolEnvelope(
            "Let me check that for you. ```json\n{\"tool\": \"get_channels\", \"arguments\": {}}\n``` hope that helps");
        Assert.NotNull(r);
        Assert.Equal("get_channels", r!.Value.toolName);
    }

    [Fact]
    public void ExtractToolEnvelope_NoEnvelope_ReturnsNull()
    {
        Assert.Null(LlmProtocol.ExtractToolEnvelope("I don't need any tools for that."));
    }

    // ---- SplitForStreaming ----

    [Fact]
    public void SplitForStreaming_RespectsCapAndPrefersSentenceBoundaries()
    {
        var text = new string('a', 150) + ". " + new string('b', 150) + ". " + new string('c', 150) + ".";
        var pieces = LlmProtocol.SplitForStreaming(text);
        Assert.All(pieces, p => Assert.True(p.Length <= 200));
        // No content lost: every 'a', 'b', and 'c' char survives across the pieces.
        var joined = string.Concat(pieces);
        Assert.Equal(150, joined.Count(ch => ch == 'a'));
        Assert.Equal(150, joined.Count(ch => ch == 'b'));
        Assert.Equal(150, joined.Count(ch => ch == 'c'));
        // First cut lands after the sentence terminator inside the first window.
        Assert.EndsWith(".", pieces[0]);
    }

    [Fact]
    public void SplitForStreaming_ShortContent_SinglePiece()
    {
        var pieces = LlmProtocol.SplitForStreaming("Done.");
        Assert.Single(pieces);
        Assert.Equal("Done.", pieces[0]);
    }

    // ---- IsToolsUnsupportedError ----

    [Theory]
    [InlineData(404, "No endpoints found that support tool use", true)]
    [InlineData(400, "This model does not support tools", true)]
    [InlineData(422, "function calling is unsupported for this model", true)]
    [InlineData(401, "No endpoints found that support tool use", false)] // auth status excluded
    [InlineData(404, "model not found", false)]
    public void IsToolsUnsupportedError_Classifies(int status, string body, bool expected)
        => Assert.Equal(expected, LlmProtocol.IsToolsUnsupportedError(status, body));

    // ---- ClassifyModelProviderMismatch ----

    [Fact]
    public void ClassifyModelProviderMismatch_DetectsCrossShape()
    {
        Assert.Equal("replicate", LlmProtocol.ClassifyModelProviderMismatch("openrouter", "lucataco/xtts-v2:684bc3855b37"));
        Assert.Equal("openrouter", LlmProtocol.ClassifyModelProviderMismatch("replicate", "openai/gpt-4o-mini-tts"));
        Assert.Null(LlmProtocol.ClassifyModelProviderMismatch("replicate", "lucataco/xtts-v2:684bc3855b37"));
        Assert.Null(LlmProtocol.ClassifyModelProviderMismatch("openrouter", "openai/gpt-4o-mini-tts"));
    }

    // ---- BuildProviderRoute ----

    [Fact]
    public void BuildProviderRoute_PinProducesOrderedRoute()
    {
        var route = PromptBuilders.BuildProviderRoute("""{"meta-llama/llama-3.3-70b-instruct":"deepinfra"}""", "meta-llama/llama-3.3-70b-instruct");
        Assert.NotNull(route);
        var json = System.Text.Json.JsonSerializer.Serialize(route);
        Assert.Contains("deepinfra", json);
        Assert.Contains("false", json); // allow_fallbacks: false
    }

    [Fact]
    public void BuildProviderRoute_NoConfigOrNull_Unrouted()
    {
        Assert.Null(PromptBuilders.BuildProviderRoute(null, "any/model"));
        Assert.Null(PromptBuilders.BuildProviderRoute("{}", "any/model"));
        Assert.Null(PromptBuilders.BuildProviderRoute("""{"other/model":"x"}""", "any/model"));
    }

    // ---- LooksLikeCapabilityQuestion ----

    [Theory]
    [InlineData("what tools do you have", true)]
    [InlineData("What can you do?", true)]
    [InlineData("list your capabilities", true)]
    [InlineData("tell me a joke", false)]
    [InlineData("", false)]
    public void LooksLikeCapabilityQuestion_Classifies(string transcript, bool expected)
        => Assert.Equal(expected, PromptBuilders.LooksLikeCapabilityQuestion(transcript));

    // ---- BuildTemporalContext ----

    [Fact]
    public void BuildTemporalContext_ContainsLocalTimeAndTz()
    {
        var anchor = PromptBuilders.BuildTemporalContext("America/Edmonton");
        Assert.Contains("Current local time:", anchor);
        Assert.Contains("America/Edmonton", anchor);
        Assert.Contains("never convert or guess times", anchor);
    }

    [Fact]
    public void BuildTemporalContext_UnknownTz_FallsBackToUtc()
    {
        var anchor = PromptBuilders.BuildTemporalContext("Not/AZone");
        Assert.Contains("UTC", anchor);
    }

    // ---- ParseWav round-trip with WavWrapper (unifies the two halves of the core) ----

    [Fact]
    public void ParseWav_RoundTripsWavWrapperOutput()
    {
        // 50ms of 16kHz mono 16-bit PCM with a known pattern.
        var pcm = new byte[1600];
        for (var i = 0; i < pcm.Length; i += 2)
        {
            pcm[i] = (byte)(i % 256);
            pcm[i + 1] = (byte)((i / 256) & 0x7F);
        }
        using var wav = WavWrapper.Wrap(pcm, 16000, 1, 16);
        using var ms = new MemoryStream();
        wav.CopyTo(ms);
        var decoded = WavDecoder.ParseWav(ms.ToArray());
        Assert.NotNull(decoded);
        Assert.Equal(16000, decoded!.SampleRate);
        Assert.Equal(1, decoded.Channels);
        Assert.Equal(16, decoded.BitsPerSample);
        Assert.Equal(pcm.Length, decoded.Pcm.Length);
        Assert.Equal(pcm, decoded.Pcm);
    }

    [Fact]
    public void ParseWav_RejectsNonWav()
    {
        Assert.Null(WavDecoder.ParseWav(new byte[] { 1, 2, 3 }));
        Assert.Null(WavDecoder.ParseWav("not a wav file at all, just bytes"u8.ToArray()));
    }

    // ---- Pacing gate semantics ----

    [Fact]
    public async Task MinSpacingPacer_ConcurrentCalls_SpaceAtLeastMinimum()
    {
        var pacer = new MinSpacingPacer(150);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var t1 = pacer.EnforceAsync(CancellationToken.None);
        var t2 = pacer.EnforceAsync(CancellationToken.None);
        await Task.WhenAll(t1, t2);
        sw.Stop();
        // The second reservation must wait ≥150ms (allow scheduling slop below the +50ms bound).
        Assert.True(sw.ElapsedMilliseconds >= 145, $"second call started after only {sw.ElapsedMilliseconds}ms");
    }
}
