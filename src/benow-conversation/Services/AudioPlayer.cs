using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace benow_conversation.Services;

public interface IAudioPlayer
{
    Task PlayAsync(string filePath, double? volume = null, string? device = null, CancellationToken cancellationToken = default);
    Task PlayStreamAsync(Stream audioStream, string format = "mp3", double? volume = null, string? device = null, CancellationToken cancellationToken = default);
    bool IsAvailable { get; }
    IReadOnlyList<AudioDevice> ListDevices();
}

public class AudioDevice
{
    public string Id { get; init; } = "";
    public string Description { get; init; } = "";
}

public class AudioPlayer : IAudioPlayer
{
    private readonly ILogger<AudioPlayer> _logger;
    private static bool? _ffplayAvailable;
    private static string? _cachedFfplayPath;

    public AudioPlayer(ILogger<AudioPlayer> logger)
    {
        _logger = logger;
    }

    public bool IsAvailable
    {
        get
        {
            if (_ffplayAvailable.HasValue)
                return _ffplayAvailable.Value;

            try
            {
                var path = FindFfplay();
                if (path == null)
                {
                    _ffplayAvailable = false;
                    return false;
                }

                var psi = new ProcessStartInfo
                {
                    FileName = path,
                    Arguments = "-version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var process = Process.Start(psi);
                process!.WaitForExit(5000);
                _ffplayAvailable = process.ExitCode == 0;
                if (_ffplayAvailable.Value)
                    _cachedFfplayPath = path;
                return _ffplayAvailable.Value;
            }
            catch
            {
                _ffplayAvailable = false;
                return false;
            }
        }
    }

    public async Task PlayAsync(string filePath, double? volume = null, string? device = null, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
            throw new InvalidOperationException("ffplay is not available. Install ffmpeg (which includes ffplay) to enable audio playback.");

        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Audio file not found: {filePath}");

        var ffplayPath = _cachedFfplayPath ?? FindFfplay() ?? "ffplay";
        var args = BuildFileArgs(filePath, volume, device);

        _logger.LogInformation("Playing: {File}", Path.GetFileName(filePath));

        await RunFfplayAsync(ffplayPath, args, cancellationToken);
    }

    public async Task PlayStreamAsync(Stream audioStream, string format = "mp3", double? volume = null, string? device = null, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
            throw new InvalidOperationException("ffplay is not available. Install ffmpeg (which includes ffplay) to enable streaming playback.");

        var ffplayPath = _cachedFfplayPath ?? FindFfplay() ?? "ffplay";
        var args = BuildStreamArgs(format, volume, device);

        var audioSize = audioStream.CanSeek ? audioStream.Length : -1;
        _logger.LogInformation("Streaming audio to ffplay (format={Format}, size={Size} bytes)", format, audioSize);

        if (audioSize >= 0 && audioSize < 1024)
            _logger.LogWarning("Audio data is very small ({Size} bytes) — may be truncated or incomplete", audioSize);

        var psi = new ProcessStartInfo
        {
            FileName = ffplayPath,
            Arguments = args,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var copyTask = CopyStreamToProcessAsync(audioStream, process.StandardInput.BaseStream, cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        await copyTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
            _logger.LogWarning("ffplay exited with code {ExitCode}: {Error}", process.ExitCode, stderr.Trim());
        else if (!string.IsNullOrWhiteSpace(stderr))
            _logger.LogDebug("ffplay stderr: {Error}", stderr.Trim());

        _logger.LogInformation("ffplay finished (exit={ExitCode})", process.ExitCode);
    }

    public IReadOnlyList<AudioDevice> ListDevices()
    {
        var devices = new List<AudioDevice>();

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "aplay",
                Arguments = "-L",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) return devices;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.TrimStart();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith(' '))
                    continue;

                var parts = trimmed.Split(new[] { ':' }, 2);
                devices.Add(new AudioDevice
                {
                    Id = parts[0].Trim(),
                    Description = parts.Length > 1 ? parts[1].Trim() : parts[0].Trim()
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not list audio devices via aplay: {Error}", ex.Message);
        }

        return devices;
    }

    private static string BuildFileArgs(string filePath, double? volume, string? device)
    {
        var args = "-nodisp -autoexit -loglevel quiet";
        if (volume.HasValue)
            args += $" -volume {Math.Clamp((int)(volume.Value), 0, 100)}";
        if (!string.IsNullOrWhiteSpace(device))
            args += $" -audiodevice \"{device}\"";
        args += $" -i \"{filePath}\"";
        return args;
    }

    private static string BuildStreamArgs(string format, double? volume, string? device)
    {
        var args = "-nodisp -autoexit -loglevel quiet";

        if (format.Equals("pcm", StringComparison.OrdinalIgnoreCase))
            args += " -f s16le -ar 24000 -ac 1";
        else if (format.Equals("wav", StringComparison.OrdinalIgnoreCase))
            args += " -f wav";
        else if (format.Equals("mp3", StringComparison.OrdinalIgnoreCase))
            args += " -f mp3";

        if (volume.HasValue)
            args += $" -volume {Math.Clamp((int)(volume.Value), 0, 100)}";
        if (!string.IsNullOrWhiteSpace(device))
            args += $" -audiodevice \"{device}\"";

        args += " -i pipe:0";
        return args;
    }

    private static async Task CopyStreamToProcessAsync(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        try
        {
            await source.CopyToAsync(destination, cancellationToken);
        }
        catch (IOException) { }
        catch (OperationCanceledException) { }
        finally
        {
            try { destination.Close(); } catch { }
        }
    }

    private async Task RunFfplayAsync(string ffplayPath, string args, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffplayPath,
            Arguments = args,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        _logger.LogDebug("Running ffplay: {Args}", args);

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        var stderr = await stderrTask;

        if (process.ExitCode != 0)
            _logger.LogWarning("ffplay exited with code {ExitCode}: {Error}", process.ExitCode, stderr.Trim());
    }

    private static string? FindFfplay()
    {
        string[] candidates = ["ffplay", "/usr/bin/ffplay", "/usr/local/bin/ffplay"];
        foreach (var candidate in candidates)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "which",
                    Arguments = candidate,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                proc!.WaitForExit(2000);
                if (proc.ExitCode == 0)
                    return candidate;
            }
            catch { }
        }

        return null;
    }
}
