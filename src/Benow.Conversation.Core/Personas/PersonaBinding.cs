using Benow.Conversation.Config;

namespace Benow.Conversation.Personas;

/// <summary>
/// The persona→engine binding rule (plan §4.9). A persona is the BUNDLE: prompt + speaker LLM +
/// TTS engine + voice. Fields it does not define PASS THROUGH to whatever the engine is already
/// configured with — that is what makes the built-in Default persona "pure pass-through" and what
/// lets a persona that only changes the voice leave the model alone.
///
/// Deliberately NOT persona-scoped: the STT model and the extractor model. Transcription and
/// tool/routing decisions must not vary by who is answering (user decision, 2026-09-12).
/// </summary>
public static class PersonaBinding
{
    /// <summary>Applies the persona's overrides onto <paramref name="config"/> in place.</summary>
    public static void Apply(ConversationConfig config, Persona persona)
    {
        if (!string.IsNullOrWhiteSpace(persona.SystemPrompt)) config.LlmSystemPrompt = persona.SystemPrompt;
        if (!string.IsNullOrWhiteSpace(persona.SpeakerModel)) config.LlmModel = persona.SpeakerModel!;
        if (!string.IsNullOrWhiteSpace(persona.TtsProvider)) config.TtsProvider = persona.TtsProvider!;
        if (!string.IsNullOrWhiteSpace(persona.TtsModel)) config.TtsModel = persona.TtsModel!;
        if (!string.IsNullOrWhiteSpace(persona.TtsVoice)) config.TtsVoice = persona.TtsVoice!;
    }

    /// <summary>
    /// Human-readable list of what this persona overrides (for the tray tooltip / settings page).
    /// Empty for the Default persona — "if it's not different from the default, it's not a persona".
    /// </summary>
    public static IReadOnlyList<string> Describe(Persona persona)
    {
        var overrides = new List<string>();
        if (!string.IsNullOrWhiteSpace(persona.SystemPrompt)) overrides.Add($"prompt ({persona.SystemPrompt.Length}c)");
        if (!string.IsNullOrWhiteSpace(persona.SpeakerModel)) overrides.Add($"model={persona.SpeakerModel}");
        if (!string.IsNullOrWhiteSpace(persona.TtsProvider)) overrides.Add($"tts={persona.TtsProvider}");
        if (!string.IsNullOrWhiteSpace(persona.TtsModel)) overrides.Add($"tts-model={persona.TtsModel}");
        if (!string.IsNullOrWhiteSpace(persona.TtsVoice)) overrides.Add($"voice={persona.TtsVoice}");
        return overrides;
    }
}
