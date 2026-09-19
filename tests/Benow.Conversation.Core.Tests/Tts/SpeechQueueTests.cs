using Benow.Conversation.Audio;
using Benow.Conversation.Voice;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Benow.Conversation.Core.Tests.Tts;

/// <summary>
/// Regression (2026-09-18, caught by the phase-2 live run): progressive TTS enqueues several
/// chunks per reply — with cancelCurrent they killed each other and only the tail was spoken.
/// Chunks must queue in sequence; a new TURN cancels.
/// </summary>
public class SpeechQueueTests
{
    private sealed class FakeTts : ITtsService
    {
        public List<string> Calls { get; } = new();
        public bool IsConfigured => true;
        public Task<TtsAudio?> SynthesizeAsync(string text, CancellationToken ct)
        {
            lock (Calls) Calls.Add(text);
            return Task.FromResult<TtsAudio?>(new TtsAudio(new byte[2400], 24000));
        }
    }

    [Fact]
    public async Task Enqueue_WithCancelCurrentFalse_KeepsAllChunksInSequence()
    {
        var tts = new FakeTts();
        // Playback pipeline needs ffplay; use a pipe that discards. PcmPlaybackPipeline is
        // concrete, so this test covers the queue semantics with a real pipeline instance.
        await using var pipeline = new PcmPlaybackPipeline(NullLogger<PcmPlaybackPipeline>.Instance,
            new PcmPlaybackOptions { FfplayPath = "ffplay", SampleRate = 24000 });
        await using var queue = new SpeechQueue(tts, new PcmPlaybackAudioOut(pipeline), NullLogger<SpeechQueue>.Instance);
        await queue.StartAsync(CancellationToken.None);

        queue.Enqueue("chunk one.", cancelCurrent: false);
        queue.Enqueue("chunk two.", cancelCurrent: false);
        queue.Enqueue("chunk three.", cancelCurrent: false);

        // Both chunks must be synthesized (neither cancels the other).
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            lock (tts.Calls) { if (tts.Calls.Count >= 3) break; }
            await Task.Delay(100);
        }
        lock (tts.Calls)
        {
            Assert.Equal(new[] { "chunk one.", "chunk two.", "chunk three." }, tts.Calls.ToArray());
        }
        await queue.StopAsync();
    }

    /// <summary>A sink that does nothing instantly (no ffplay) — for lifecycle assertions.</summary>
    private sealed class NullAudioOut : IAudioOut
    {
        public List<SpeechQueue.SynthesizedAudio> Played { get; } = new();
        public Task PlayAsync(SpeechQueue.SynthesizedAudio chunk, CancellationToken ct)
        {
            lock (Played) Played.Add(chunk);
            return Task.CompletedTask;
        }
        public Task ResetAsync() => Task.CompletedTask;
    }

    private sealed class NullTts : ITtsService
    {
        public bool IsConfigured => true;
        public Task<TtsAudio?> SynthesizeAsync(string text, CancellationToken ct) =>
            Task.FromResult<TtsAudio?>(new TtsAudio(new byte[4800], 24000));
    }

    private sealed class FailingTts : ITtsService
    {
        public bool IsConfigured => true;
        public Task<TtsAudio?> SynthesizeAsync(string text, CancellationToken ct) => Task.FromResult<TtsAudio?>(null);
    }

    [Fact]
    public async Task ItemDropped_FiresForEachFailedSynthesis_AndDrainedStillFires()
    {
        var dropped = new List<string>();
        var drained = new List<DateTime>();
        await using var queue = new SpeechQueue(new FailingTts(), new NullAudioOut(), NullLogger<SpeechQueue>.Instance);
        queue.ItemDropped += t => { lock (dropped) dropped.Add(t); };
        queue.Drained += () => { lock (drained) drained.Add(DateTime.UtcNow); };
        await queue.StartAsync(CancellationToken.None);

        queue.Enqueue("a.", cancelCurrent: false);
        queue.Enqueue("b.", cancelCurrent: false);

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            lock (dropped) if (dropped.Count >= 2) break;
            await Task.Delay(50);
        }
        lock (dropped) Assert.Equal(new[] { "a.", "b." }, dropped);
        lock (drained) Assert.Single(drained);
        await queue.StopAsync();
    }

    [Fact]
    public async Task SynthesizedAudio_CarriesTheTrueSampleRate()
    {
        var tts = new RateTts(16000);
        var outSink = new NullAudioOut();
        await using var queue = new SpeechQueue(tts, outSink, NullLogger<SpeechQueue>.Instance);
        await queue.StartAsync(CancellationToken.None);
        queue.Enqueue("rate check.", cancelCurrent: false);

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            lock (outSink.Played) if (outSink.Played.Count > 0) break;
            await Task.Delay(50);
        }
        lock (outSink.Played) Assert.Equal(16000, outSink.Played[0].SampleRate);
        await queue.StopAsync();
    }

    private sealed class RateTts : ITtsService
    {
        private readonly int _rate;
        public RateTts(int rate) => _rate = rate;
        public bool IsConfigured => true;
        public Task<TtsAudio?> SynthesizeAsync(string text, CancellationToken ct) =>
            Task.FromResult<TtsAudio?>(new TtsAudio(new byte[3200], _rate));
    }

    [Fact]
    public async Task Drained_FiresAfterTheLastChunkPlays_ExactlyOnce()
    {
        var outSink = new NullAudioOut();
        var drained = new List<DateTime>();
        await using var queue = new SpeechQueue(new NullTts(), outSink, NullLogger<SpeechQueue>.Instance);
        queue.Drained += () => { lock (drained) drained.Add(DateTime.UtcNow); };
        await queue.StartAsync(CancellationToken.None);

        queue.Enqueue("a.", cancelCurrent: false);
        queue.Enqueue("b.", cancelCurrent: false);

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            lock (drained) if (drained.Count > 0) break;
            await Task.Delay(50);
        }

        lock (outSink.Played) Assert.Equal(2, outSink.Played.Count);
        lock (drained) Assert.Single(drained);   // one turn, one drained signal
        await queue.StopAsync();
    }

    [Fact]
    public async Task Drained_FiresEvenWhenSynthesisDropsEveryChunk()
    {
        // A turn whose chunks ALL fail to synthesize must still emit Drained — a host waiting
        // for it (client audio-session release) must never hang on a fully-dropped turn.
        var drained = new List<DateTime>();
        await using var queue = new SpeechQueue(new FailingTts(), new NullAudioOut(), NullLogger<SpeechQueue>.Instance);
        queue.Drained += () => { lock (drained) drained.Add(DateTime.UtcNow); };
        await queue.StartAsync(CancellationToken.None);

        queue.Enqueue("nothing will synthesize.", cancelCurrent: false);

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            lock (drained) if (drained.Count > 0) break;
            await Task.Delay(50);
        }
        lock (drained) Assert.Single(drained);
        await queue.StopAsync();
    }

    [Fact]
    public async Task FallbackRaised_SurfacesTheTtsFallbackMessage()
    {
        var fallbacks = new List<string>();
        var tts = new NullTtsWithFallback();
        await using var queue = new SpeechQueue(tts, new NullAudioOut(), NullLogger<SpeechQueue>.Instance);
        queue.FallbackRaised += f => { lock (fallbacks) fallbacks.Add(f); };
        await queue.StartAsync(CancellationToken.None);

        queue.Enqueue("hello.", cancelCurrent: false);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            lock (fallbacks) if (fallbacks.Count > 0) break;
            await Task.Delay(50);
        }
        lock (fallbacks) Assert.Equal(new[] { "fell back" }, fallbacks);
        await queue.StopAsync();
    }

    private sealed class NullTtsWithFallback : ITtsService
    {
        public bool IsConfigured => true;
        public Task<TtsAudio?> SynthesizeAsync(string text, CancellationToken ct) =>
            Task.FromResult<TtsAudio?>(new TtsAudio(new byte[4800], 24000, FallbackMessage: "fell back"));
    }
}
