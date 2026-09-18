namespace Benow.Conversation.Voice;

/// <summary>
/// Transcribes PCM audio bytes to text. Implementations call external STT APIs.
/// Ported from NASTV nastv-player-core/Voice/ITranscriptionService.cs (2026-09-17, phase 1).
/// </summary>
public interface ITranscriptionService
{
    /// <summary>
    /// Transcribe one audio segment. Returns the text, or null if transcription failed
    /// (the caller should skip this slot in the reassembly).
    /// </summary>
    Task<string?> TranscribeSegmentAsync(byte[] pcm, int sequence, CancellationToken ct);
}

/// <summary>
/// Result of transcribing one audio segment.
/// </summary>
public sealed record TranscriptionResult(
    int Sequence,
    string Text,
    long ElapsedMs,
    string? Error = null);

public interface ITtsService
{
    bool IsConfigured { get; }
    Task<TtsAudio?> SynthesizeAsync(string text, CancellationToken ct);
}

/// <summary>
/// Synthesized speech: raw 16-bit mono PCM + its true sample rate (Hz).
/// FallbackMessage is set when the audio was produced only via a provider/model-mismatch
/// fallback — the session surfaces it as a voice.tts.fallback event (chat toast).
/// SynthMs/TextChars feed the per-turn [voice-metrics] summary (2026-08-31) — synthesis
/// latency per chunk is the number that diagnoses "long delay before speech".
/// </summary>
public sealed record TtsAudio(byte[] Pcm, int SampleRate, string? FallbackMessage = null, long SynthMs = 0, int TextChars = 0)
{
    /// <summary>Playback duration in ms (16-bit mono: 2 bytes/sample).</summary>
    public long AudioMs => Pcm.Length * 1000L / (SampleRate * 2L);
}

/// <summary>One accumulated conversation turn (past user or assistant message).</summary>
public sealed record ChatTurn(string Role, string Content);

/// <summary>
/// Sends transcribed voice text to an LLM and streams the response back via callback.
/// </summary>
public interface IVoiceLlmService
{
    /// <summary>
    /// Send <paramref name="transcript"/> to the configured LLM and return the full response.
    /// <paramref name="history"/> carries the accumulated conversation (oldest first) so the
    /// LLM sees prior context; keep the prefix stable across calls for prompt-cache hits.
    /// <paramref name="timezone"/> is the client's IANA timezone (e.g. "America/Toronto") —
    /// the engine injects a "current local time" anchor and tools return local times (W3).
    /// <paramref name="userId"/>/<paramref name="deviceId"/> travel to the tool executor so
    /// identity-dependent tools (e.g. play_channel → which TV) work.
    /// Chunks are delivered via <paramref name="onChunk"/> as they arrive (for SSE streaming).
    /// Returns null on failure.
    /// </summary>
    Task<string?> ChatAsync(string transcript, IReadOnlyList<ChatTurn>? history, string? timezone,
        string userId, string deviceId, Action<string>? onChunk, CancellationToken ct);
}
