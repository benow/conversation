namespace Benow.Conversation.Llm;

/// <summary>
/// Prompt constants and caps — ported verbatim from NASTV VoiceEndpoints (2026-09-17, phase 1).
/// The ordering principles they encode are invariants of the core (see PromptBuilders docs).
/// </summary>
public static class PromptConstants
{
    /// <summary>
    /// W4 operating guidance — prepended FIRST (before the persona prompt) so the model keeps
    /// its tool discipline and honesty even when a strong custom system prompt pulls it into
    /// character. Kept deliberately short; the persona prompt still governs tone/verbosity.
    /// </summary>
    public const string VoiceGuidanceBlock =
        "You are a helpful, natural TV assistant. Use the available tools for anything about " +
        "schedules, what's on, channels, movies, series, or current data — never invent listings, " +
        "titles, or times. If a tool returns nothing, say so plainly. Include the links that tools " +
        "provide in your answer. Keep replies conversational and warm — you are being spoken aloud. " +
        "Do not use markdown formatting (no bold, italic, headers, or code blocks) since your " +
        "response is spoken aloud.";

    /// <summary>
    /// W14: minimal dispatcher system message for the extractor model (multi-model mode).
    /// Tool selection ONLY — the persona/system prompt must NEVER reach this model (F4: a
    /// strong persona collapses tool discipline). "none" = no tools needed this turn (pure
    /// conversation); the speaker answers instead. "capabilities" = the user asked about the
    /// assistant's own tools — inject the real inventory into the speaker's data block so it
    /// answers from facts, never a hallucinated list (2026-08-12).
    /// </summary>
    public const string ExtractorSystemPrompt =
        "You are a tool dispatcher. If the user asks about schedules, what's on, channels, " +
        "movies, series, current data, or wants an action (play, search, submit), reply with " +
        "a single tool call. For 'what's on' / 'what channels are in [category]' questions, " +
        "use get_channels (its category filter is fuzzy — 'US movie channels' matches). For " +
        "named titles, shows, or movie/series names, use search. If the user asks about YOUR " +
        "tools, capabilities, or what you can do, reply with exactly: capabilities. If the " +
        "request is purely conversational (greeting, joke, chat), reply with exactly: none";

    /// <summary>
    /// W14: guidance for the SPEAKER model in multi-model mode — no tool references (the
    /// speaker never calls tools; it only receives the compacted "Current data" block).
    /// </summary>
    public const string VoiceSpeakerGuidanceBlock =
        "If a persona system prompt follows, it defines who you are — stay fully in that " +
        "character and answer as that character, never as a generic assistant. If there is " +
        "no persona, be a helpful, natural TV assistant. Use the \"Current data\" block below if " +
        "present; never invent listings, titles, or times. If a data question has no data, " +
        "say you couldn't retrieve it. Keep replies conversational and warm — you are being " +
        "spoken aloud. Do not use markdown formatting (no bold, italic, headers, or code " +
        "blocks) since your response is spoken aloud.";

    /// <summary>
    /// Grounding anchor appended as the LAST system message in the speaker pass (multi-model)
    /// and in single-model pass 2 when tool results are present. Exploits LLM recency bias:
    /// this is the final framing instruction the model sees before generating, so it outranks
    /// earlier persona/system prompts. Tells the model: stay in whatever character was set
    /// above, but ground your answer in the data — never fabricate.
    /// </summary>
    public const string SpeakerGroundingAnchor =
        "Important: use the data provided above to answer. Stay in character, but do NOT " +
        "invent, fabricate, or guess listings, times, titles, or facts. If the data is " +
        "empty or missing for what was asked, say so honestly.";

    // Conversation caps shared by the single-model path and the extractor/speaker
    // message builders (hoisted so both stay in sync).
    public const int MaxHistoryMessages = 40;
    public const int MaxHistoryCharsPerMessage = 4000;
    public const int ExtractorHistoryLimit = 4;
    public const int CompactDataBlockCap = 4000;
}
