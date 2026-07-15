using System.Diagnostics;
using benow_conversation.Services.Abstractions;
using Microsoft.Extensions.Logging;

namespace benow_conversation.Services.Stt;

public class WaylandClipboardService : IClipboardService
{
    private readonly ILogger<WaylandClipboardService> _logger;
    private static bool? _available;

    public WaylandClipboardService(ILogger<WaylandClipboardService> logger)
    {
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
                    Arguments = "wl-copy",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                proc!.WaitForExit(3000);
                _available = proc.ExitCode == 0;
                if (!_available.Value)
                    _logger.LogWarning("[Clipboard] wl-copy not found in PATH");
                else
                    _logger.LogInformation("[Clipboard] wl-copy found in PATH");
                return _available.Value;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Clipboard] Failed to check for wl-copy: {Error}", ex.Message);
                _available = false;
                return false;
            }
        }
    }

    public async Task CopyAsync(string text, CancellationToken ct = default)
    {
        if (!IsAvailable)
            throw new InvalidOperationException("wl-copy is not available.");

        _logger.LogDebug("[Clipboard] Copying {Length} chars to clipboard via wl-copy", text.Length);

        var psi = new ProcessStartInfo
        {
            FileName = "wl-copy",
            Arguments = "",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi)!;
        await process.StandardInput.WriteAsync(text);
        process.StandardInput.Close();

        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            var error = await process.StandardError.ReadToEndAsync(ct);
            _logger.LogError("[Clipboard] wl-copy failed (exit {ExitCode}): {Error}", process.ExitCode, error);
            throw new InvalidOperationException($"wl-copy failed (exit {process.ExitCode}): {error}");
        }

        _logger.LogDebug("[Clipboard] Copy successful");
    }

    public async Task<string?> ReadAsync(CancellationToken ct = default)
    {
        if (!IsAvailable)
            return null;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "wl-paste",
                Arguments = "--no-newline",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
            {
                _logger.LogWarning("[Clipboard] wl-paste process failed to start");
                return null;
            }

            var text = await process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0)
            {
                var error = await process.StandardError.ReadToEndAsync(ct);
                _logger.LogDebug("[Clipboard] wl-paste failed (exit {ExitCode}): {Error}", process.ExitCode, error);
                return null;
            }

            if (string.IsNullOrWhiteSpace(text))
                return null;

            _logger.LogDebug("[Clipboard] Read {Length} chars from clipboard", text.Length);
            return text;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Clipboard] Failed to read clipboard: {Error}", ex.Message);
            return null;
        }
    }
}
