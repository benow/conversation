namespace benow_conversation.Services.Abstractions;

public interface ITranscriptionService
{
    Task<string> TranscribeAsync(string audioFilePath, CancellationToken ct = default);
}
