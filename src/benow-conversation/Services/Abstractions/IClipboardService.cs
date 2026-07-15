namespace benow_conversation.Services.Abstractions;

public interface IClipboardService
{
    bool IsAvailable { get; }
    Task CopyAsync(string text, CancellationToken ct = default);
    Task<string?> ReadAsync(CancellationToken ct = default);
}
