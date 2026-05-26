using System.Diagnostics;
using benow_conversation.Configuration;
using benow_conversation.Services.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace benow_conversation.Services.Stt;

public class PipeWireRecorder : IAudioRecorder
{
    private readonly SttSettings _settings;
    private readonly ILogger<PipeWireRecorder> _logger;
    private static bool? _available;

    public PipeWireRecorder(IOptions<AppSettings> settings, ILogger<PipeWireRecorder> logger)
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
                    Arguments = _settings.RecorderCommand,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                proc!.WaitForExit(3000);
                _available = proc.ExitCode == 0;
                if (!_available.Value)
                    _logger.LogWarning("[Recorder] {Command} not found in PATH", _settings.RecorderCommand);
                else
                    _logger.LogInformation("[Recorder] {Command} found in PATH", _settings.RecorderCommand);
                return _available.Value;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Recorder] Failed to check for {Command}: {Error}", _settings.RecorderCommand, ex.Message);
                _available = false;
                return false;
            }
        }
    }

    public async Task<string> RecordToFileAsync(string outputPath, CancellationToken ct)
    {
        if (!IsAvailable)
            throw new InvalidOperationException($"{_settings.RecorderCommand} is not available.");

        var args = $"--format s16 --rate {_settings.RecorderSampleRate} --channels {_settings.RecorderChannels} \"{outputPath}\"";

        _logger.LogInformation("[Recorder] Starting: {Command} {Args}", _settings.RecorderCommand, args);

        var psi = new ProcessStartInfo
        {
            FileName = _settings.RecorderCommand,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi)!;
        _logger.LogDebug("[Recorder] Process started (PID={Pid})", process.Id);

        using var registration = ct.Register(() =>
        {
            _logger.LogInformation("[Recorder] Cancellation requested — sending SIGTERM to PID={Pid}", process.Id);
            try
            {
                var killPsi = new ProcessStartInfo
                {
                    FileName = "kill",
                    Arguments = $"-TERM {process.Id}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var killProc = Process.Start(killPsi);
                killProc!.WaitForExit(2000);
                _logger.LogDebug("[Recorder] SIGTERM sent to PID={Pid}", process.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Recorder] Failed to send SIGTERM to PID={Pid}: {Error}", process.Id, ex.Message);
                try { process.Kill(entireProcessTree: true); }
                catch { }
            }
        });

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            _logger.LogInformation("[Recorder] Process exited with code {ExitCode}", process.ExitCode);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[Recorder] Recording cancelled — process terminated");
            try { process.Kill(entireProcessTree: true); } catch { }
        }

        if (!File.Exists(outputPath))
        {
            _logger.LogError("[Recorder] Output file not found: {Path}", outputPath);
            throw new InvalidOperationException($"Recording produced no output file: {outputPath}");
        }

        var fileSize = new FileInfo(outputPath).Length;
        _logger.LogInformation("[Recorder] Output file: {Path} ({Size} bytes)", outputPath, fileSize);

        if (fileSize == 0)
        {
            _logger.LogError("[Recorder] Output file is empty: {Path}", outputPath);
            throw new InvalidOperationException("Recording produced empty output.");
        }

        if (!ValidateWavHeader(outputPath))
        {
            _logger.LogWarning("[Recorder] WAV header invalid in {Path} — repairing with ffmpeg...", outputPath);
            var repairedPath = await RepairWavAsync(outputPath, ct);
            return repairedPath;
        }

        _logger.LogDebug("[Recorder] WAV header valid for {Path}", outputPath);
        return outputPath;
    }

    private bool ValidateWavHeader(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var reader = new BinaryReader(fs);
            var riff = reader.ReadBytes(4);
            if (System.Text.Encoding.ASCII.GetString(riff) != "RIFF")
            {
                _logger.LogDebug("[Recorder] WAV validation: missing RIFF header in {Path}", path);
                return false;
            }

            var declaredSize = reader.ReadUInt32();
            var actualSize = new FileInfo(path).Length - 8;
            var valid = Math.Abs(declaredSize - actualSize) <= 1;
            if (!valid)
                _logger.LogDebug("[Recorder] WAV validation: size mismatch — declared={Declared}, actual={Actual}", declaredSize, actualSize);
            return valid;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Recorder] WAV validation failed for {Path}: {Error}", path, ex.Message);
            return false;
        }
    }

    private async Task<string> RepairWavAsync(string inputPath, CancellationToken ct)
    {
        var ffmpegPath = _settings.FfmpegPath;
        if (string.IsNullOrWhiteSpace(ffmpegPath))
            ffmpegPath = "ffmpeg";

        var repairedPath = Path.Combine(Path.GetTempPath(), $"stt_repaired_{Guid.NewGuid():N}.wav");

        _logger.LogInformation("[Recorder] Running ffmpeg repair: {Ffmpeg} -i {Input} -> {Output}", ffmpegPath, inputPath, repairedPath);

        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = $"-y -i \"{inputPath}\" -c:a pcm_s16le -ar {_settings.RecorderSampleRate} -ac {_settings.RecorderChannels} \"{repairedPath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi)!;

        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            var error = await process.StandardError.ReadToEndAsync(ct);
            _logger.LogError("[Recorder] ffmpeg repair failed (exit {ExitCode}): {Error}", process.ExitCode, error.Length > 500 ? error[..500] + "..." : error);
            throw new InvalidOperationException($"ffmpeg WAV repair failed (exit {process.ExitCode}): {error}");
        }

        var repairedSize = new FileInfo(repairedPath).Length;
        _logger.LogInformation("[Recorder] ffmpeg repair complete: {Output} ({Size} bytes)", repairedPath, repairedSize);

        try { File.Delete(inputPath); } catch { }
        return repairedPath;
    }
}
