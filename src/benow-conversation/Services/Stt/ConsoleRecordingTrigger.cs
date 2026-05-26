using benow_conversation.Services.Abstractions;
using Microsoft.Extensions.Logging;

namespace benow_conversation.Services.Stt;

public class ConsoleRecordingTrigger : IRecordingTrigger
{
    private readonly ILogger<ConsoleRecordingTrigger> _logger;

    public ConsoleRecordingTrigger(ILogger<ConsoleRecordingTrigger> logger)
    {
        _logger = logger;
    }

    public bool IsAvailable => true;

    public async Task WaitForTriggerAsync(CancellationToken ct)
    {
        _logger.LogDebug("[Trigger] Waiting for Enter key press...");
        Console.WriteLine("[STT] Press Enter to continue...");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(true);
                    if (key.Key == ConsoleKey.Enter)
                    {
                        _logger.LogInformation("[Trigger] Enter key pressed");
                        return;
                    }
                }

                await Task.Delay(50, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        ct.ThrowIfCancellationRequested();
    }
}
