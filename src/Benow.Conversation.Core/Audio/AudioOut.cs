namespace Benow.Conversation.Audio;

/// <summary>
/// The last mile of speech playback. Everything before it (look-ahead synthesis, ordering,
/// pacing, cancellation, metrics) lives in <see cref="SpeechQueue"/>; only this differs per host:
/// the desktop pipes to a local player (ffplay), NASTV streams chunks to TV/phone clients over
/// SignalR. PlayAsync blocks for the duration of the audio — that backpressure is what the
/// look-ahead buffer overlaps synthesis with.
/// </summary>
public interface IAudioOut
{
    /// <summary>Plays one synthesized chunk, in queue order. Returns when the audio is consumed.</summary>
    Task PlayAsync(SpeechQueue.SynthesizedAudio chunk, CancellationToken ct);

    /// <summary>Stops whatever is currently sounding (barge-in / new turn). Safe when idle.</summary>
    Task ResetAsync();

    /// <summary>Opens the device/process early so the first chunk is instant. No-op is fine.</summary>
    Task PrewarmAsync(CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>Desktop sink: pipes PCM into the local <see cref="PcmPlaybackPipeline"/> (ffplay).</summary>
public sealed class PcmPlaybackAudioOut : IAudioOut
{
    private readonly PcmPlaybackPipeline _pipeline;

    public PcmPlaybackAudioOut(PcmPlaybackPipeline pipeline) => _pipeline = pipeline;

    public Task PlayAsync(SpeechQueue.SynthesizedAudio chunk, CancellationToken ct)
    {
        using var pcm = new MemoryStream(chunk.Pcm);
        return _pipeline.PipeAsync(pcm, ct);
    }

    public Task ResetAsync() => _pipeline.InterruptAsync();

    public Task PrewarmAsync(CancellationToken ct = default) => _pipeline.PrewarmAsync(ct);
}
