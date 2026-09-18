namespace Benow.Conversation.Llm;

/// <summary>
/// Process-wide pacing gates — port of NASTV VoiceEndpoints' SttGate/EnforceSttPaceAsync and
/// TtsReplicateGate (2026-09-17, phase 1). These encode hard-won provider-abuse lessons;
/// keep the semantics identical when refactoring.
/// </summary>
public sealed class MinSpacingPacer
{
    private readonly int _minSpacingMs;
    private readonly object _lock = new();
    private DateTimeOffset _lastStart = DateTimeOffset.MinValue;

    public MinSpacingPacer(int minSpacingMs) => _minSpacingMs = minSpacingMs;

    /// <summary>
    /// Reserves the next start slot and waits until it arrives. The reservation happens under
    /// the lock BEFORE waiting, so concurrent callers queue with spacing preserved even if the
    /// delays overlap.
    /// </summary>
    public async Task EnforceAsync(CancellationToken ct)
    {
        long waitMs;
        lock (_lock)
        {
            var elapsed = DateTimeOffset.UtcNow - _lastStart;
            waitMs = _minSpacingMs - (long)elapsed.TotalMilliseconds;
            if (waitMs < 0) waitMs = 0;
            _lastStart = DateTimeOffset.UtcNow.AddMilliseconds(waitMs);
        }
        if (waitMs > 0) await Task.Delay((int)waitMs, ct);
    }
}

/// <summary>
/// The two gates the conversation engine needs. Both SERIALIZE (SemaphoreSlim(1,1)) on top of
/// spacing — shared process-wide, because NASTV plugins run as one process and the desktop app
/// is one process.
/// </summary>
public static class ProviderPacing
{
    /// <summary>
    /// STT gate: Groq's Cloudflare WAF IP-blocks on burst patterns. Even with the gate
    /// serializing, fire-and-forget dispatches can queue up and hit the provider back-to-back
    /// (~2 req/s under fast speech with pair corrections). The 600ms minimum spacing caps the
    /// sustained rate at ~1.67 req/s — below the ~1.8 req/s observed running clean on 2026-08-08.
    /// A flush of ~12 concurrent STT calls in 1s once took every endpoint down for ~18 min.
    /// </summary>
    public const int SttMinSpacingMs = 600;

    public static readonly SemaphoreSlim SttGate = new(1, 1);
    public static readonly MinSpacingPacer SttPacer = new(SttMinSpacingMs);

    /// <summary>
    /// TTS gate: Replicate's lucataco/xtts-v2 runs on a single serial GPU — concurrent
    /// predictions queue server-side and every chunk's latency balloons (2026-08-31: a 112-char
    /// chunk took 22.5s and a 148-char one 26.7s while queued behind bigger predictions; several
    /// chunks then blew past the 30s plugin-communication timeout and their audio was silently
    /// DROPPED — "first sentence ok, then a long silent gap, then jagged remainder").
    /// Serializing keeps each prediction at its solo cost (7-15s) while the previous chunk's
    /// audio is still playing back.
    /// </summary>
    public static readonly SemaphoreSlim TtsReplicateGate = new(1, 1);
}
