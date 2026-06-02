namespace benow_conversation.Models;

/// <summary>Represents a single spoken segment attributed to one character in a multi-character script.</summary>
public record CharacterSegment
{
    /// <summary>Zero-based ordinal indicating the playback order among non-skipped segments.</summary>
    public int SequenceIndex { get; init; }

    /// <summary>The character's display name, or empty string for narrator/pre-marker text.</summary>
    public string CharacterName { get; init; } = "";

    /// <summary>Gender hint for persona selection ("F" or "M").</summary>
    public string Gender { get; init; } = "F";

    /// <summary>The text to be spoken by TTS.</summary>
    public string SpokenText { get; init; } = "";

    /// <summary>Persona key assigned by the allocator, or null if not yet allocated.</summary>
    public string? PersonaKey { get; set; }

    /// <summary>True when the text was wrapped in [thought] tags and should be rendered in a distinct style.</summary>
    public bool IsThought { get; init; }

    /// <summary>True when the text was wrapped in *...* narration markers and should use the narrator persona.</summary>
    public bool IsNarration { get; init; }

    /// <summary>Speech modifier extracted from (modifier) syntax immediately after the character marker.</summary>
    public string? Modifier { get; init; }
}
