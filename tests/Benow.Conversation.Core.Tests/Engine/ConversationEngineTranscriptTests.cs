using Benow.Conversation.Audio;
using Benow.Conversation.Engine;
using Benow.Conversation.Llm;
using Benow.Conversation.Voice;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Benow.Conversation.Core.Tests.Engine;

/// <summary>
/// Regressions for the transcript-assembly rules (both caught by the live 5-minute dictation
/// run 2026-09-18):
///   1. The full-audio correction window must never TRUNCATE a long turn — a 5-minute
///      dictation was silently reduced to its first 30 seconds.
///   2. A "correction" that loses most of the content is a provider hiccup — keep the
///      assembled text.
/// </summary>
public class ConversationEngineTranscriptTests
{
    private static readonly byte[] SpeechFrame = Frame((short)20000);
    private static readonly byte[] SilenceFrame = Frame((short)20);

    private static byte[] Frame(short amplitude)
    {
        var b = new byte[640];
        for (var i = 0; i < b.Length; i += 2)
        {
            b[i] = (byte)(amplitude & 0xFF);
            b[i + 1] = (byte)((amplitude >> 8) & 0xFF);
        }
        return b;
    }

    private sealed class FakeStt : ITranscriptionService
    {
        public string SegmentText { get; set; } = "the quick brown fox jumps over the lazy dog";
        public string CorrectionText { get; set; } = "the quick brown fox jumps over the lazy dog";
        public int CorrectionCalls { get; private set; }

        public Task<string?> TranscribeSegmentAsync(byte[] pcm, int sequence, CancellationToken ct)
        {
            if (sequence == -1)
            {
                CorrectionCalls++;
                return Task.FromResult<string?>(CorrectionText);
            }
            return Task.FromResult<string?>(SegmentText);
        }
    }

    private static ConversationEngine BuildEngine(FakeStt stt, EngineOptions options)
    {
        var chat = new ChatClient(NullLogger<ChatClient>.Instance,
            new ChatOptions { SpeakerModel = "unused", ApiKey = "unused" },
            () => new HttpClient());
        var tts = new StubTts();
        var pipeline = new PcmPlaybackPipeline(NullLogger<PcmPlaybackPipeline>.Instance,
            new PcmPlaybackOptions { FfplayPath = "ffplay" });
        var queue = new SpeechQueue(tts, new PcmPlaybackAudioOut(pipeline), NullLogger<SpeechQueue>.Instance);
        return new ConversationEngine(NullLogger<ConversationEngine>.Instance, stt, chat, tts, queue, options);
    }

    private static async Task FeedSpeechAsync(ConversationEngine engine, int speechFrames)
    {
        engine.BeginSession();
        for (var i = 0; i < 25; i++) await engine.PushFrameAsync(SilenceFrame);  // calibrate
        for (var i = 0; i < speechFrames; i++) await engine.PushFrameAsync(SpeechFrame);
        for (var i = 0; i < 20; i++) await engine.PushFrameAsync(SilenceFrame);
    }

    [Fact]
    public async Task LongTurn_OverCorrectionCap_SkipsCorrection_KeepsAssembledText()
    {
        var stt = new FakeStt { CorrectionText = "SHORT" };
        // Cap effectively zero → the 3s turn is "over cap" → correction must be skipped.
        await using var engine = BuildEngine(stt, new EngineOptions { FullAudioCapSeconds = 0 });
        await FeedSpeechAsync(engine, 150);

        var transcript = await engine.EndSessionAsync();

        Assert.Equal(0, stt.CorrectionCalls);
        Assert.Contains("quick brown fox", transcript);
    }

    [Fact]
    public async Task Correction_ShorterThanEightyPercent_IsRejected()
    {
        var stt = new FakeStt
        {
            SegmentText = "the quick brown fox jumps over the lazy dog and then keeps running for a while",
            CorrectionText = "the quick"  // provider hiccup: loses most content
        };
        await using var engine = BuildEngine(stt, new EngineOptions { FullAudioCapSeconds = 30 });
        await FeedSpeechAsync(engine, 150);

        var transcript = await engine.EndSessionAsync();

        Assert.Equal(1, stt.CorrectionCalls);
        Assert.Contains("keeps running", transcript); // assembled text survived
    }

    [Fact]
    public async Task Correction_WithFullContent_IsAccepted()
    {
        var stt = new FakeStt
        {
            SegmentText = "mumble mumble mumble mumble mumble mumble mumble mumble",
            CorrectionText = "The quick brown fox jumps over the lazy dog, cleanly corrected."
        };
        await using var engine = BuildEngine(stt, new EngineOptions { FullAudioCapSeconds = 30 });
        await FeedSpeechAsync(engine, 150);

        var transcript = await engine.EndSessionAsync();

        Assert.Equal(1, stt.CorrectionCalls);
        Assert.Equal("The quick brown fox jumps over the lazy dog, cleanly corrected.", transcript);
    }

    private sealed class StubTts : ITtsService
    {
        public bool IsConfigured => false;
        public Task<TtsAudio?> SynthesizeAsync(string text, CancellationToken ct)
            => Task.FromResult<TtsAudio?>(new TtsAudio(new byte[2400], 24000));
    }
}
