namespace benow_conversation.Services.Stt;

public interface IClipboardTtsRunner
{
    Task RunAsync(CancellationToken cancellationToken);
}
