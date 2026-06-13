using System.Diagnostics;
using System.Text.RegularExpressions;
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

    public static void KillOrphanedProcesses(ILogger? logger = null)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "pgrep",
                Arguments = "-f \"ffmpeg.*pulse.*stt_|pw-record.*stt_\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var pgrep = Process.Start(psi);
            var output = pgrep!.StandardOutput.ReadToEnd();
            pgrep.WaitForExit(3000);

            if (pgrep.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                return;

            var pids = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => int.TryParse(s, out _))
                .ToList();

            if (pids.Count == 0)
                return;

            var msg = $"[Recorder] Found {pids.Count} orphaned recording process(es): {string.Join(", ", pids)} — killing them";
            logger?.LogWarning(msg);
            Console.WriteLine(msg);

            var killPsi = new ProcessStartInfo
            {
                FileName = "kill",
                Arguments = $"-TERM {string.Join(" ", pids)}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var killProc = Process.Start(killPsi);
            killProc!.WaitForExit(3000);

            var deadline = DateTime.UtcNow.AddSeconds(1);
            foreach (var pid in pids)
            {
                if (int.TryParse(pid, out var pidInt))
                {
                    try
                    {
                        using var proc = Process.GetProcessById(pidInt);
                        var remaining = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                        if (remaining > 0 && !proc.WaitForExit(remaining))
                            proc.Kill(entireProcessTree: true);
                    }
                    catch { }
                }
            }
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "[Recorder] Orphan cleanup failed: {Error}", ex.Message);
        }
    }

    public static void EnsureAvrcpDevice(string? triggerDeviceName, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(triggerDeviceName))
            return;

        var devices = LinuxInterop.ParseProcBusInputDevices();
        var found = devices.Any(kvp => kvp.Value.Replace(" [BT]", "").Equals(triggerDeviceName, StringComparison.OrdinalIgnoreCase)
            || kvp.Value.Equals(triggerDeviceName, StringComparison.OrdinalIgnoreCase));

        if (found)
            return;

        var msg = $"[STT] Trigger device '{triggerDeviceName}' not found — Bluetooth may be stuck in HFP";
        logger?.LogWarning(msg);
        Console.WriteLine(msg);

        var mac = FindBluetoothMac();
        if (mac == null)
        {
            Console.WriteLine("[STT] No Bluetooth audio device found — cannot auto-reconnect");
            return;
        }

        Console.WriteLine($"[STT] Reconnecting {mac} to restore A2DP/AVRCP...");
        ReconnectBluetooth(mac);

        for (var i = 0; i < 10; i++)
        {
            Thread.Sleep(1000);
            devices = LinuxInterop.ParseProcBusInputDevices();
            if (devices.Any(kvp => kvp.Value.Replace(" [BT]", "").Contains(triggerDeviceName, StringComparison.OrdinalIgnoreCase)))
            {
                Console.WriteLine($"[STT] AVRCP device restored after {(i + 1)}s");
                return;
            }
        }

        Console.WriteLine("[STT] AVRCP device still not found after reconnect — trigger may not work");
    }

    private static string? FindBluetoothMac()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "pw-cli",
                Arguments = "list-objects",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            var output = proc!.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);

            var match = Regex.Match(output, @"device\.name\s*=\s*""bluez_card\.([0-9A-Fa-f_]+)""");
            if (match.Success)
                return match.Groups[1].Value.Replace("_", ":");
            return null;
        }
        catch { return null; }
    }

    private static void ReconnectBluetooth(string mac)
    {
        try
        {
            var disconnPsi = new ProcessStartInfo
            {
                FileName = "bluetoothctl",
                Arguments = $"disconnect {mac}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var disconn = Process.Start(disconnPsi);
            disconn!.WaitForExit(10000);
        }
        catch { }

        Thread.Sleep(2000);

        try
        {
            var connPsi = new ProcessStartInfo
            {
                FileName = "bluetoothctl",
                Arguments = $"connect {mac}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var conn = Process.Start(connPsi);
            conn!.WaitForExit(10000);
        }
        catch { }
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
                    Arguments = _settings.FfmpegPath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                proc!.WaitForExit(3000);
                _available = proc.ExitCode == 0;
                if (!_available.Value)
                    _logger.LogWarning("[Recorder] {Command} not found in PATH", _settings.FfmpegPath);
                else
                    _logger.LogInformation("[Recorder] {Command} found in PATH", _settings.FfmpegPath);
                return _available.Value;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Recorder] Failed to check for {Command}: {Error}", _settings.FfmpegPath, ex.Message);
                _available = false;
                return false;
            }
        }
    }

    public async Task<string> RecordToFileAsync(string outputPath, CancellationToken ct)
    {
        var ext = Path.GetExtension(outputPath).TrimStart('.');
        if (string.IsNullOrEmpty(ext))
            ext = _settings.RecorderFormat;

        var tempOutput = ext.Equals("wav", StringComparison.OrdinalIgnoreCase)
            ? outputPath
            : Path.ChangeExtension(outputPath, "mp3");

        var args = BuildFFmpegArgs(tempOutput);

        _logger.LogInformation("[Recorder] Starting: ffmpeg {Args}", args);
        var psi = new ProcessStartInfo
        {
            FileName = _settings.FfmpegPath,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi)!;
        _logger.LogDebug("[Recorder] ffmpeg started (PID={Pid})", process.Id);
        var recordingStart = Stopwatch.StartNew();

        try
        {
            using var registration = ct.Register(() =>
            {
                _logger.LogInformation("[Recorder] cancelled after {Ms}ms — sending SIGTERM to PID={Pid}",
                    recordingStart.ElapsedMilliseconds, process.Id);
                SendTermOrKill(process);
            });

            try
            {
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
                _logger.LogInformation("[Recorder] ffmpeg exited with code {ExitCode} after {Ms}ms",
                    process.ExitCode, recordingStart.ElapsedMilliseconds);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("[Recorder] Recording stopped after {Ms}ms — waiting for ffmpeg to finalize",
                    recordingStart.ElapsedMilliseconds);

                if (!process.WaitForExit(5000))
                {
                    _logger.LogWarning("[Recorder] ffmpeg did not exit after SIGTERM — force killing");
                    try { process.Kill(entireProcessTree: true); } catch { }
                }

                _logger.LogInformation("[Recorder] ffmpeg finalized (exit code {ExitCode})", process.ExitCode);
            }

            if (!File.Exists(tempOutput))
            {
                _logger.LogError("[Recorder] No output file produced");
                throw new InvalidOperationException($"Recording produced no output file: {tempOutput}");
            }

            var fileSize = new FileInfo(tempOutput).Length;
            _logger.LogInformation("[Recorder] Output: {Path} ({Size} bytes, {Duration}ms)",
                tempOutput, fileSize, recordingStart.ElapsedMilliseconds);

            if (fileSize == 0)
            {
                _logger.LogWarning("[Recorder] Output file is empty (recording too short?)");
                throw new InvalidOperationException("Recording produced empty output.");
            }

            return tempOutput;
        }
        finally
        {
            EnsureDead(process);
        }
    }

    private void SendTermOrKill(Process process)
    {
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
        }
        catch { try { process.Kill(entireProcessTree: true); } catch { } }
    }

    private void EnsureDead(Process process)
    {
        if (process.HasExited)
            return;

        _logger.LogWarning("[Recorder] ffmpeg (PID={Pid}) still alive — force killing", process.Id);
        try { process.Kill(entireProcessTree: true); }
        catch (Exception ex) { _logger.LogDebug(ex, "[Recorder] Kill failed (already dead?)"); }

        process.WaitForExit(3000);
    }

    private string BuildFFmpegArgs(string outputPath)
    {
        var deviceArg = !string.IsNullOrWhiteSpace(_settings.RecorderDevice)
            ? $" -i \"{_settings.RecorderDevice}\""
            : " -i default";

        var codecArgs = outputPath.EndsWith(".mp3")
            ? " -acodec libmp3lame -b:a 32k"
            : " -acodec pcm_s16le";

        return $"-y -f pulse{deviceArg} -ac {_settings.RecorderChannels} -ar {_settings.RecorderSampleRate}{codecArgs} \"{outputPath}\"";
    }
}
