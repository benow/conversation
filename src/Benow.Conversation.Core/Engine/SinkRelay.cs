namespace Benow.Conversation.Engine;

/// <summary>
/// A swappable sink holder. The engine is constructed before a host exists (the host needs the
/// engine), so the sink it publishes to must be replaceable after construction — otherwise a UI
/// that attaches later silently never receives an event, which is invisible until a user notices
/// the overlay stays empty.
///
/// Forwarding is unconditional and allocation-free; the target is swapped by assignment.
/// </summary>
internal sealed class SinkRelay : IConversationEventSink
{
    private IConversationEventSink _target = NullEventSink.Instance;

    public IConversationEventSink Target
    {
        get => _target;
        set => _target = value ?? NullEventSink.Instance;
    }

    public void SessionStarted() => _target.SessionStarted();
    public void SessionEnded(long durationMs) => _target.SessionEnded(durationMs);
    public void TranscriptPartial(string text) => _target.TranscriptPartial(text);
    public void TranscriptFinal(string text, bool corrected) => _target.TranscriptFinal(text, corrected);
    public void LlmStarted() => _target.LlmStarted();
    public void LlmChunk(string text) => _target.LlmChunk(text);
    public void LlmFinal(string text, long ms) => _target.LlmFinal(text, ms);
    public void TtsStarted(string text) => _target.TtsStarted(text);
    public void TtsDone(string text, long audioMs) => _target.TtsDone(text, audioMs);
    public void Error(string stage, string message) => _target.Error(stage, message);
}
