using Benow.Conversation.Engine;

namespace Benow.Conversation.Composition;

/// <summary>
/// Fans one engine event out to several sinks. The engine takes a single sink, but a host usually
/// wants two things at once (log everything + drive the UI), so it passes this.
/// </summary>
public sealed class CompositeSink : IConversationEventSink
{
    private readonly IReadOnlyList<IConversationEventSink> _sinks;

    public CompositeSink(params IConversationEventSink[] sinks) => _sinks = sinks;

    /// <summary>True when <paramref name="sink"/> is part of this tree (directly or nested).</summary>
    public bool Contains(IConversationEventSink sink) =>
        _sinks.Any(s => ReferenceEquals(s, sink) || (s is CompositeSink c && c.Contains(sink)));

    public void SessionStarted() { foreach (var s in _sinks) s.SessionStarted(); }
    public void SessionEnded(long durationMs) { foreach (var s in _sinks) s.SessionEnded(durationMs); }
    public void TranscriptPartial(string text) { foreach (var s in _sinks) s.TranscriptPartial(text); }
    public void TranscriptFinal(string text, bool corrected) { foreach (var s in _sinks) s.TranscriptFinal(text, corrected); }
    public void LlmStarted() { foreach (var s in _sinks) s.LlmStarted(); }
    public void LlmChunk(string text) { foreach (var s in _sinks) s.LlmChunk(text); }
    public void LlmFinal(string text, long ms) { foreach (var s in _sinks) s.LlmFinal(text, ms); }
    public void TtsStarted(string text) { foreach (var s in _sinks) s.TtsStarted(text); }
    public void TtsDone(string text, long audioMs) { foreach (var s in _sinks) s.TtsDone(text, audioMs); }
    public void Error(string stage, string message) { foreach (var s in _sinks) s.Error(stage, message); }
}
