using Benow.Conversation.Engine;

namespace Benow.Conversation.Desktop;

/// <summary>
/// Forwards engine events to the desktop UI (streaming reply text, turn lifecycle, errors) while
/// also recording the last error so a turn can report failure honestly instead of as "no reply".
///
/// The engine takes ONE sink at construction, so this is composed with the logging sink rather
/// than replacing it (see <see cref="CompositeSink"/>).
/// </summary>
public sealed class DesktopEventSink : IConversationEventSink
{
    /// <summary>Streaming assistant text (chunks as the LLM emits them).</summary>
    public event Action<string>? Chunk;
    /// <summary>Final transcript for a turn (post-correction).</summary>
    public event Action<string, bool>? Transcript;
    /// <summary>A failure inside the engine (provider, TTS, STT).</summary>
    public event Action<string, string>? Error;
    /// <summary>Turn lifecycle: true when the first token arrives, false when audio finishes.</summary>
    public event Action<bool>? Speaking;

    /// <summary>Set when the engine reported a failure during the current turn; cleared per turn.</summary>
    public string? LastError { get; private set; }

    public void BeginTurn() => LastError = null;

    public void SessionStarted() { }
    public void SessionEnded(long durationMs) { }
    public void TranscriptPartial(string text) => Transcript?.Invoke(text, false);
    public void TranscriptFinal(string text, bool corrected) => Transcript?.Invoke(text, corrected);
    public void LlmStarted() => Speaking?.Invoke(true);
    public void LlmChunk(string text) => Chunk?.Invoke(text);
    public void LlmFinal(string text, long ms) => Speaking?.Invoke(false);
    public void TtsStarted(string text) { }
    public void TtsDone(string text, long audioMs) { }

    public void Error_(string stage, string message)
    {
        LastError = $"{stage}: {message}";
        Error?.Invoke(stage, message);
    }

    // The interface member is named Error too, so the explicit implementation disambiguates it
    // from the event above.
    void IConversationEventSink.Error(string stage, string message) => Error_(stage, message);
}
