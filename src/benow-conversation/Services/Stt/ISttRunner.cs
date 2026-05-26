namespace benow_conversation.Services.Stt;

public interface ISttRunner
{
    Task RunAsync(CancellationToken cancellationToken);
}
