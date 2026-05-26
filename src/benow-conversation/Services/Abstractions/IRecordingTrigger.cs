namespace benow_conversation.Services.Abstractions;

public interface IRecordingTrigger
{
    bool IsAvailable { get; }
    Task WaitForTriggerAsync(CancellationToken ct);
}
