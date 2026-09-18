using System.Diagnostics;
using System.Threading.Channels;
using Benow.Conversation.Voice;
using Microsoft.Extensions.Logging;

namespace Benow.Conversation.Audio;

/// <summary>
/// Serial speak queue: text in, audio out. Synthesizes each item via <see cref="ITtsService"/>
/// and pipes the PCM to the playback pipeline. Cancel-on-new semantics (V1's SpeechQueue):
/// enqueuing with <c>cancelCurrent</c> stops in-flight playback so a new turn is heard
/// immediately rather than after the previous reply drains.
/// Ported and simplified from V1's SpeechQueue (2026-09-17, phase 2) — the Core version speaks
/// one provider (ITtsService) instead of V1's four backend branches.
/// </summary>
public sealed class SpeechQueue : IAsyncDisposable
{
    private readonly ITtsService _tts;
    private readonly PcmPlaybackPipeline _pipeline;
    private readonly ILogger<SpeechQueue> _logger;
    private readonly Channel<string> _channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private CancellationTokenSource? _currentItemCts;
    private Task? _processing;
    private readonly CancellationTokenSource _shutdown = new();
    // ChannelReader.Count throws NotSupportedException on this channel shape (unbounded +
    // SingleReader — caught live 2026-09-18), so track the queue depth explicitly.
    private int _queued;

    /// <summary>Raised per completed item: (text, audioMs) — drives per-turn metrics.</summary>
    public event Action<string, long>? Spoken;
    /// <summary>Raised when an item's synthesis returns: (text, synthMs) — latency instrumentation.</summary>
    public event Action<string, long>? ItemSynthesized;
    /// <summary>Raised when an item's PCM has been handed to the playback pipeline (first mark = first audio out).</summary>
    public event Action<string>? ItemPiped;

    public SpeechQueue(ITtsService tts, PcmPlaybackPipeline pipeline, ILogger<SpeechQueue> logger)
    {
        _tts = tts;
        _pipeline = pipeline;
        _logger = logger;
    }

    public int QueuedCount => Volatile.Read(ref _queued);

    /// <summary>Enqueue text to speak. <paramref name="cancelCurrent"/> interrupts playback in progress.</summary>
    public void Enqueue(string text, bool cancelCurrent = true)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        _logger.LogInformation("[speech] enqueue {Chars}c (cancel={Cancel}): {Preview}",
            text.Length, cancelCurrent, text.Length > 60 ? text[..60] + "…" : text);
        if (cancelCurrent)
        {
            try { _currentItemCts?.Cancel(); } catch (ObjectDisposedException) { }
            InterruptPlayback();
        }
        if (_channel.Writer.TryWrite(text)) Interlocked.Increment(ref _queued);
    }

    /// <summary>Drop everything queued and stop audio now (user interrupt / barge-in).</summary>
    public void FlushAndCancel()
    {
        var dropped = 0;
        while (_channel.Reader.TryRead(out _)) dropped++;
        Interlocked.Add(ref _queued, -dropped);
        _logger.LogInformation("[speech] flush + cancel (dropped {Dropped} queued)", dropped);
        FlushOnly();
    }

    /// <summary>Cancel current playback but keep the queue draining (used by provider fallbacks).</summary>
    public void FlushOnly()
    {
        try { _currentItemCts?.Cancel(); } catch (ObjectDisposedException) { }
        InterruptPlayback();
    }

    private void InterruptPlayback()
    {
        try { _pipeline.InterruptAsync().GetAwaiter().GetResult(); }
        catch (Exception ex) { _logger.LogWarning(ex, "[speech] pipeline interrupt failed"); }
    }

    public Task StartAsync(CancellationToken ct)
    {
        _processing = ProcessAsync(ct);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _channel.Writer.TryComplete();
        _shutdown.Cancel();
        if (_processing != null)
            await _processing;
    }

    private async Task ProcessAsync(CancellationToken ct)
    {
        await foreach (var text in _channel.Reader.ReadAllAsync(ct))
        {
            Interlocked.Decrement(ref _queued);
            using var itemCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
            _currentItemCts = itemCts;
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

                using var pcm = new MemoryStream(audio.Pcm);
                await _pipeline.PipeAsync(pcm, itemCts.Token);
                ItemPiped?.Invoke(text);
                Spoken?.Invoke(text, audio.AudioMs);
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
                _currentItemCts = null;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _shutdown.Dispose();
    }
}
