namespace benow_conversation.Services.Abstractions;

public interface IAudioRecorder
{
    bool IsAvailable { get; }
    Task<string> RecordToFileAsync(string outputPath, CancellationToken ct);
}
