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
}
