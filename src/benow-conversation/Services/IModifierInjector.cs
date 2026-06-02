namespace benow_conversation.Services;

/// <summary>Injects speech modifier annotations into multi-character script text using an LLM.</summary>
public interface IModifierInjector
{
    /// <summary>Sends the script text to a modifier model and returns the annotated text, or the original on failure.</summary>
    Task<string> InjectModifiersAsync(string text, CancellationToken ct);
}
