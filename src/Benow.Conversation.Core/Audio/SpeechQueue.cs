using System.Diagnostics;
using System.Threading.Channels;
using Benow.Conversation.Voice;
using Microsoft.Extensions.Logging;

namespace Benow.Conversation.Audio;

/// <summary>
/// Speak queue: text in, audio out. Synthesizes each item via <see cref="ITtsService"/> and hands
/// the ordered PCM chunks to an <see cref="IAudioOut"/> sink. Cancel-on-new semantics (V1's
/// SpeechQueue): enqueuing with <c>cancelCurrent</c> stops in-flight playback so a new turn is
/// heard immediately rather than after the previous reply drains.
///
/// SYNTHESIS RUNS AHEAD OF PLAYBACK (2026-09-18, found by the Lab's --bench). The original shape
/// was one serial loop — synthesize chunk, pipe it (which blocks while the sink plays it at 1x),
/// then synthesize the next. With Replicate XTTS that produced 6.6s and 10.3s of DEAD SILENCE
/// between chunks of one reply: chunk N+1's synthesis only started once chunk N's audio had
/// drained. Two stages now run concurrently — a synthesizer filling a small look-ahead buffer and
/// a player draining it — so chunk N+1 is ready before chunk N stops playing.
///
/// THE SINK IS THE SEAM (2026-09-19, "no duplicated functionality"): everything improvable here —
/// look-ahead depth, ordering, cancellation, gap metrics — lives in the package. The last mile is
/// pluggable: desktop passes <see cref="PcmPlaybackAudioOut"/> (local ffplay), NASTV passes a
/// SignalR sink that streams chunks to TV/phone clients. Same engine, improvements land everywhere.
/// </summary>
public sealed class SpeechQueue : IAsyncDisposable
{
    /// <summary>Chunks synthesized ahead of playback. Two is enough to hide provider jitter
    /// (synthesis ~43ms/char vs playback ~65ms/char) without holding audio the user may interrupt.</summary>
    private const int LookAheadChunks = 2;

    private readonly ITtsService _tts;
    private readonly IAudioOut _audioOut;
    private long _playbackMs;
    private volatile bool _inFlight;
    private readonly ILogger<SpeechQueue> _logger;
    private readonly Channel<string> _channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Channel<SynthesizedAudio> _ready =
        Channel.CreateBounded<SynthesizedAudio>(new BoundedChannelOptions(LookAheadChunks) { SingleReader = true, SingleWriter = true });
    private CancellationTokenSource? _currentItemCts;
    private Task? _processing;
    private Task? _playing;
    private readonly CancellationTokenSource _shutdown = new();
    // ChannelReader.Count throws NotSupportedException on this channel shape (unbounded +
    // SingleReader — caught live 2026-09-18), so track the queue depth explicitly.
    private int _queued;

    /// <summary>A synthesized chunk waiting for its turn to play. Public: the sink receives it.</summary>
    public sealed record SynthesizedAudio(string Text, byte[] Pcm, long AudioMs, long SynthMs);

    /// <summary>Raised per completed item: (text, audioMs) — drives per-turn metrics.</summary>
    public event Action<string, long>? Spoken;
    /// <summary>Raised when an item's synthesis returns: (text, synthMs) — latency instrumentation.</summary>
    public event Action<string, long>? ItemSynthesized;
    /// <summary>Raised when an item's PCM has been handed to the sink (first mark = first audio out).</summary>
    public event Action<string>? ItemPiped;
    /// <summary>Fired when a chunk's PCM starts being handed to the sink (before the play call blocks).</summary>
    public event Action<string, long>? PlaybackStarted;

    public SpeechQueue(ITtsService tts, IAudioOut audioOut, ILogger<SpeechQueue> logger)
    {
        _tts = tts;
        _audioOut = audioOut;
        _logger = logger;
    }

    public int QueuedCount => Volatile.Read(ref _queued);

    /// <summary>
    /// True when nothing is queued AND nothing is mid-flight (no synthesis, no pipe in progress).
    /// "QueuedCount == 0" alone is NOT idle: chunks of one reply are enqueued as the LLM streams
    /// them, so the queue passes through empty between chunks.
    /// </summary>
    public bool IsIdle => Volatile.Read(ref _queued) == 0 && !_inFlight;

    /// <summary>
    /// Starts the playback sink before any text exists, so the first chunk does not pay
    /// process start + audio-device open. Called at session start when prewarming is enabled.
    /// Sinks that have nothing to warm (e.g. streaming to clients) are no-ops.
    /// </summary>
    public Task PrewarmAsync(CancellationToken ct = default) => _audioOut.PrewarmAsync(ct);

    /// <summary>Enqueue text to speak. <paramref name="cancelCurrent"/> interrupts playback in progress.</summary>
    public void Enqueue(string text, bool cancelCurrent = true)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        _logger.LogInformation("[speech] enqueue {Chars}c (cancel={Cancel}): {Preview}",
            text.Length, cancelCurrent, text.Length > 60 ? text[..60] + "…" : text);
        if (cancelCurrent)
        {
            try { _currentItemCts?.Cancel(); } catch (ObjectDisposedException) { }
            ResetSink();
        }
        if (_channel.Writer.TryWrite(text)) Interlocked.Increment(ref _queued);
    }

    /// <summary>Drop everything queued and stop audio now (user interrupt / barge-in).</summary>
    public void FlushAndCancel()
    {
        var dropped = 0;
        while (_channel.Reader.TryRead(out _)) dropped++;
        // Also drop anything synthesized ahead that has not started playing yet — otherwise the
        // cancelled turn's audio would still be spoken after the interrupt.
        while (_ready.Reader.TryRead(out _)) dropped++;
        Interlocked.Add(ref _queued, -dropped);
        _logger.LogInformation("[speech] flush + cancel (dropped {Dropped} queued)", dropped);
        FlushOnly();
    }

    /// <summary>Cancel current playback but keep the queue draining (used by provider fallbacks).</summary>
    public void FlushOnly()
    {
        try { _currentItemCts?.Cancel(); } catch (ObjectDisposedException) { }
        ResetSink();
    }

    private void ResetSink()
    {
        try { _audioOut.ResetAsync().GetAwaiter().GetResult(); }
        catch (Exception ex) { _logger.LogWarning(ex, "[speech] sink reset failed"); }
    }

    public Task StartAsync(CancellationToken ct)
    {
        _processing = ProcessAsync(ct);
        _playing = PlayAsync(ct);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _channel.Writer.TryComplete();
        _ready.Writer.TryComplete();
        _shutdown.Cancel();
        foreach (var task in new[] { _processing, _playing })
            if (task != null) await task;
    }

    /// <summary>Stage 1: synthesize as fast as the provider allows, ahead of playback.</summary>
    private async Task ProcessAsync(CancellationToken ct)
    {
        await foreach (var text in _channel.Reader.ReadAllAsync(ct))
        {
            Interlocked.Decrement(ref _queued);
            using var itemCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
            _currentItemCts = itemCts;
            _inFlight = true;
            try
            {
                if (!_tts.IsConfigured)
                {
                    _logger.LogWarning("[speech] TTS not configured — dropping {Chars}c. Fix: configure a TTS provider + model in settings", text.Length);
                    continue;
                }

                var sw = Stopwatch.StartNew();
                var audio = await _tts.SynthesizeAsync(text, itemCts.Token);
                if (audio == null)
                {
                    _logger.LogWarning("[speech] synthesis returned no audio for {Chars}c — skipping", text.Length);
                    continue;
                }

                _logger.LogInformation("[speech] synthesized {Chars}c → {Bytes}B @{Rate}Hz in {Ms}ms ({AudioMs}ms audio)",
                    text.Length, audio.Pcm.Length, audio.SampleRate, sw.ElapsedMilliseconds, audio.AudioMs);
                ItemSynthesized?.Invoke(text, sw.ElapsedMilliseconds);

                // Bounded: at most LookAheadChunks wait here, so a cancelled turn cannot leave a
                // long tail of stale synthesized audio behind (FlushAndCancel empties this).
                await _ready.Writer.WriteAsync(new SynthesizedAudio(text, audio.Pcm, audio.AudioMs, sw.ElapsedMilliseconds), itemCts.Token);
            }
            catch (OperationCanceledException) when (itemCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                _logger.LogInformation("[speech] item cancelled — newer speech incoming");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[speech] item failed ({Chars}c): {Error}. " +
                    "Fix: check TTS provider config (model/voice/key) in settings", text.Length, ex.Message);
            }
            finally
            {
                _inFlight = false;
                _currentItemCts = null;
            }
        }
    }

    /// <summary>Stage 2: drain synthesized chunks into the player, in order.</summary>
    private async Task PlayAsync(CancellationToken ct)
    {
        await foreach (var item in _ready.Reader.ReadAllAsync(ct))
        {
            try
            {
                using var pcm = new MemoryStream(item.Pcm);
                // PlaybackStarted fires BEFORE the play call: the sink blocks while the audio is
                // consumed at real-time rate, so firing after it reported "first audio" up to a
                // second and a half late (and made chunk-gap maths nonsense — Lab --bench, 2026-09-18).
                var playbackSw = Stopwatch.StartNew();
                PlaybackStarted?.Invoke(item.Text, item.AudioMs);
                await _audioOut.PlayAsync(item, ct);
                _playbackMs += playbackSw.ElapsedMilliseconds;
                ItemPiped?.Invoke(item.Text);
                Spoken?.Invoke(item.Text, item.AudioMs);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[speech] playback failed ({Chars}c): {Error}", item.Text.Length, ex.Message);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _shutdown.Dispose();
    }
}
