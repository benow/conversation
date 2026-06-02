using benow_conversation.Configuration;

namespace benow_conversation.Services;

/// <summary>Allocates TTS voice personas to character names with rotation and staleness tracking.</summary>
public interface IPersonaAllocator
{
    /// <summary>Returns an existing persona key for the character, or allocates a new one from the matching pool.</summary>
    string? AllocateForCharacter(string name, string gender);

    /// <summary>Clears all character-to-persona mappings and usage history.</summary>
    void Reset();

    /// <summary>Returns the persona definition for the given key, or null if not found.</summary>
    VoicePersona? GetPersona(string personaKey);
}
