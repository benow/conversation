using System.Diagnostics;
using benow_conversation.Configuration;
using benow_conversation.Services.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace benow_conversation.Services.Stt;

public class YdotoolKeyboardSimulator : IKeyboardSimulator
{
    private readonly SttSettings _settings;
    private readonly ILogger<YdotoolKeyboardSimulator> _logger;
    private static bool? _available;

    private const int KEY_ENTER = 28;
    private const int KEY_LEFTCTRL = 29;
    private const int KEY_V = 47;

    public YdotoolKeyboardSimulator(IOptions<AppSettings> settings, ILogger<YdotoolKeyboardSimulator> logger)
    {
        _settings = settings.Value.Stt;
        _logger = logger;
    }

    public bool IsAvailable
    {
        get
        {
            if (_available.HasValue)
                return _available.Value;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "which",
                    Arguments = "ydotool",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                proc!.WaitForExit(3000);
                if (proc.ExitCode != 0)
                {
                    _logger.LogWarning("[Keyboard] ydotool not found in PATH");
                    _available = false;
                    return false;
                }

                _logger.LogInformation("[Keyboard] ydotool found in PATH (socket={Socket})", _settings.YdotoolSocketPath);
                _available = true;
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Keyboard] Failed to check for ydotool: {Error}", ex.Message);
                _available = false;
                return false;
            }
        }
    }

    public async Task PasteAsync(CancellationToken ct = default)
    {
        if (!IsAvailable)
            throw new InvalidOperationException("ydotool is not available.");

        _logger.LogDebug("[Keyboard] Simulating Ctrl+V (paste)");
        await RunYdotoolAsync($"key {KEY_LEFTCTRL}:1 {KEY_V}:1 {KEY_V}:0 {KEY_LEFTCTRL}:0", ct);
    }

    public async Task PressEnterAsync(CancellationToken ct = default)
    {
        if (!IsAvailable)
            throw new InvalidOperationException("ydotool is not available.");

        _logger.LogDebug("[Keyboard] Simulating Enter key");
        await RunYdotoolAsync($"key {KEY_ENTER}:1 {KEY_ENTER}:0", ct);
    }

    private async Task RunYdotoolAsync(string args, CancellationToken ct)
    {
        _logger.LogDebug("[Keyboard] Running: ydotool {Args} (YDOTOOL_SOCKET={Socket})", args, _settings.YdotoolSocketPath);

        var psi = new ProcessStartInfo
        {
            FileName = "ydotool",
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.Environment["YDOTOOL_SOCKET"] = _settings.YdotoolSocketPath;

        try
        {
            using var process = Process.Start(psi)!;
            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0)
            {
                var error = await process.StandardError.ReadToEndAsync(ct);
                _logger.LogWarning("[Keyboard] ydotool failed (exit {ExitCode}): {Error}", process.ExitCode, error.Trim());
            }
            else
            {
                _logger.LogDebug("[Keyboard] ydotool command completed successfully");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Keyboard] Failed to run ydotool: {Error}", ex.Message);
            throw;
        }
    }
}
