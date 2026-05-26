namespace benow_conversation.Services.Abstractions;

public interface IKeyboardSimulator
{
    bool IsAvailable { get; }
    Task PasteAsync(CancellationToken ct = default);
    Task PressEnterAsync(CancellationToken ct = default);
}
