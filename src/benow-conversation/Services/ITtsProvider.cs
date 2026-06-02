using benow_conversation.Models;

namespace benow_conversation.Services;

public interface ITtsProvider
{
    AudioFormat OutputFormat { get; }
    Task<Stream> SynthesizeAsync(
        string text,
        string personaKey,
        string voice,
        string? instructions,
        double? temperature,
        int? seed,
        CancellationToken ct);
}
