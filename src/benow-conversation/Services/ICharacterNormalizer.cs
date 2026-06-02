namespace benow_conversation.Services;

/// <summary>Normalizes prose-style multi-character text into [Name:F] bracket format using an LLM.</summary>
public interface ICharacterNormalizer
{
    /// <summary>Normalizes text that lacks character markers into proper [Name:F] format. Returns original text on failure.</summary>
    Task<string> NormalizeAsync(string text, CancellationToken ct);
}
