namespace Benow.Conversation;

/// <summary>
/// Marker for the Conversation V2 core extraction (phase 0 skeleton).
/// Phase 1 (see docs/plans/conversation-v2.md in the nastv repo) fills this package with the
/// voice pipeline (VAD, segment dispatch, progressive-TTS scheduling), the AI provider layer
/// (STT/LLM/TTS), the LLM turn strategy, cross-platform capture, and the configuration model.
/// </summary>
public static class ConversationCore
{
    public const string Version = "0.0.1";
}
