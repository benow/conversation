namespace Benow.Conversation;

/// <summary>
/// Shared conversation engine core (phase 1 complete — see docs/plans/conversation-v2.md in
/// the nastv repo): voice pipeline (VAD, sentence/pacing, WAV), AI providers (STT/LLM/TTS),
/// the LLM turn strategy (extractor/speaker, prompt invariants, pacing gates), and the V1
/// multi-character text pipeline (parser + splitters, golden-master verified).
/// Phase 2 adds the service/host; capture abstraction follows.
/// </summary>
public static class ConversationCore
{
    public const string Version = "0.3.0";
}
