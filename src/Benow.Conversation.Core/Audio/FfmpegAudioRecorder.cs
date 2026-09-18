using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Benow.Conversation.Audio;

/// <summary>Recorder abstraction — matches V1's IAudioRecorder so the ported trigger/pipeline code binds unchanged.</summary>
public interface IAudioRecorder
{
    bool IsAvailable { get; }
    Task<string> RecordToFileAsync(string outputPath, CancellationToken ct);
}

/// <summary>Cross-platform capture settings.</summary>
public sealed record CaptureOptions
{
    public string FfmpegPath { get; init; } = "ffmpeg";
    /// <summary>Input device name. Empty = system default. Validated device names come from AudioDeviceEnumerator.</summary>
    public string Device { get; init; } = "";
    public int SampleRate { get; init; } = 16000;
    public int Channels { get; init; } = 1;
    /// <summary>Explicit platform override (tests); null = detect from OS.</summary>
    public CapturePlatform? Platform { get; init; }
    /// <summary>
    /// Replay a file through the streaming capture at its native rate instead of opening a
    /// device (bench/regression runs). Ignores <see cref="Device"/>.
    /// </summary>
    public string InputFile { get; init; } = "";
}

public enum CapturePlatform { Linux, Windows, MacOs }

/// <summary>
/// ffmpeg-subprocess recorder that works across Linux (PulseAudio/PipeWire), Windows (dshow)
/// and macOS (avfoundation) — the cross-platform generalization of V1's PipeWireRecorder
/// (2026-09-17, phase 2). Keeps V1's finalization discipline: SIGTERM first (so ffmpeg writes
/// the container trailer), 5s grace, then force-kill; orphan sweep at startup.
/// </summary>
public sealed class FfmpegAudioRecorder : IAudioRecorder
{
    private readonly ILogger<FfmpegAudioRecorder> _logger;
    private readonly CaptureOptions _options;
    private readonly CapturePlatform _platform;
    private bool? _available;

    public FfmpegAudioRecorder(ILogger<FfmpegAudioRecorder> logger, CaptureOptions? options = null)
    {
        _logger = logger;
        _options = options ?? new CaptureOptions();
        _platform = _options.Platform ?? DetectPlatform();
        KillOrphanedProcesses();
    }

    /// <summary>Marker on our ffmpeg command lines — the orphan sweep matches ONLY these.</summary>
    public const string CaptureMarker = "conversation-capture";

    public static CapturePlatform DetectPlatform() =>
        OperatingSystem.IsWindows() ? CapturePlatform.Windows
        : OperatingSystem.IsMacOS() ? CapturePlatform.MacOs
        : CapturePlatform.Linux;

    public CapturePlatform Platform => _platform;

    public bool IsAvailable
    {
        get
        {
            if (_available.HasValue) return _available.Value;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = _platform == CapturePlatform.Windows ? "where" : "which",
                    Arguments = _options.FfmpegPath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                proc!.WaitForExit(3000);
                _available = proc.ExitCode == 0;
                if (!_available.Value)
                    _logger.LogWarning("[capture] ffmpeg not found ({Path}) — install ffmpeg (Linux: package ffmpeg; Windows: winget install Gyan.FFmpeg) " +
                        "or set the path in settings", _options.FfmpegPath);
                return _available.Value;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[capture] availability check failed for {Path}", _options.FfmpegPath);
                _available = false;
                return false;
            }
        }
    }

    /// <summary>Records until <paramref name="ct"/> cancels, then finalizes the file and returns its path.</summary>
    public async Task<string> RecordToFileAsync(string outputPath, CancellationToken ct)
    {
        var ext = Path.GetExtension(outputPath).TrimStart('.');
        var tempOutput = ext.Equals("wav", StringComparison.OrdinalIgnoreCase)
            ? outputPath
            : Path.ChangeExtension(outputPath, "wav");

        var args = BuildArgs(tempOutput);
        _logger.LogInformation("[capture] starting: ffmpeg {Args}", args);

        var psi = new ProcessStartInfo
        {
            FileName = _options.FfmpegPath,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi)!;
        var started = Stopwatch.StartNew();
        var stderrTail = new List<string>();

        // Drain stderr continuously (ffmpeg blocks if the pipe fills) and keep the tail for diagnosis.
        var stderrTask = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await process.StandardError.ReadLineAsync()) != null)
                {
                    lock (stderrTail)
                    {
                        stderrTail.Add(line);
                        if (stderrTail.Count > 40) stderrTail.RemoveAt(0);
                    }
                }
            }
            catch { }
        });

        try
        {
            using var reg = ct.Register(() =>
            {
                _logger.LogInformation("[capture] stop requested after {Ms}ms — SIGTERM to pid={Pid}", started.ElapsedMilliseconds, process.Id);
                SendTerm(process);
            });

            try
            {
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (!process.WaitForExit(5000))
                {
                    _logger.LogWarning("[capture] ffmpeg ignored SIGTERM — force killing (the WAV trailer may be missing)");
                    try { process.Kill(entireProcessTree: true); } catch { }
                }
            }

            if (!File.Exists(tempOutput))
            {
                _logger.LogError("[capture] no output file produced. ffmpeg tail: {Tail}. " +
                    "Fix: verify the input device exists — list devices with `ffmpeg -f {Format} -list_devices true -i dummy`",
                    string.Join(" | ", Tail(stderrTail)), PlatformFormat());
                throw new InvalidOperationException($"Recording produced no output file: {tempOutput}");
            }

            var size = new FileInfo(tempOutput).Length;
            _logger.LogInformation("[capture] output {Path} ({Size}B, {Ms}ms)", tempOutput, size, started.ElapsedMilliseconds);
            if (size <= 44) // WAV header only
            {
                _logger.LogWarning("[capture] recording is empty (header only) — was the device silent or missing?");
                throw new InvalidOperationException("Recording produced empty output (no audio captured).");
            }

            return tempOutput;
        }
        finally
        {
            EnsureDead(process);
            await Task.WhenAny(stderrTask, Task.Delay(500));
        }
    }

    private static List<string> Tail(List<string> lines)
    {
        lock (lines) return lines.TakeLast(6).ToList();
    }

    private string PlatformFormat() => _platform switch
    {
        CapturePlatform.Windows => "dshow",
        CapturePlatform.MacOs => "avfoundation",
        _ => "pulse"
    };

    private string BuildArgs(string outputPath)
    {
        var device = _options.Device;
        var input = _platform switch
        {
            // PulseAudio/PipeWire: "default" or a source name (alsa_input.*, bluez_input.*).
            CapturePlatform.Linux => string.IsNullOrWhiteSpace(device) ? "-i default" : $"-i \"{device}\"",
            // DirectShow: audio="Device Name" (exact Windows device name, as enumerated).
            CapturePlatform.Windows => string.IsNullOrWhiteSpace(device) ? "-i audio=default" : $"-i audio=\"{device}\"",
            // AVFoundation: ":<index>" (video:audio) — empty video index, audio index from the enumerator.
            CapturePlatform.MacOs => string.IsNullOrWhiteSpace(device) ? "-i \":0\"" : $"-i \"{device}\"",
            _ => "-i default"
        };

        // 16kHz mono s16le WAV is exactly what the VAD/STT pipeline consumes — no resample step.
        // The metadata title doubles as our process-list marker for the orphan sweep.
        return $"-y -f {PlatformFormat()} {input} -ac {_options.Channels} -ar {_options.SampleRate} " +
               $"-metadata title={CaptureMarker} -acodec pcm_s16le \"{outputPath}\"";
    }

    private static void SendTerm(Process process)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // No SIGTERM on Windows; ffmpeg finalizes on CloseMainWindow/CTRL_BREAK — the
                // graceful path there is `q` on stdin, but our stdin is closed, so kill(entireTree)
                // is the platform reality: the WAV header is already written by ffmpeg incrementally.
                process.Kill(entireProcessTree: true);
                return;
            }
            var psi = new ProcessStartInfo
            {
                FileName = "kill",
                Arguments = $"-TERM {process.Id}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var killProc = Process.Start(psi);
            killProc!.WaitForExit(2000);
        }
        catch
        {
            try { process.Kill(entireProcessTree: true); } catch { }
        }
    }

    private void EnsureDead(Process process)
    {
        try
        {
            if (process.HasExited) return;
            _logger.LogWarning("[capture] ffmpeg pid={Pid} still alive — force killing", process.Id);
            process.Kill(entireProcessTree: true);
            process.WaitForExit(3000);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[capture] kill failed (already dead?)");
        }
    }

    private void KillOrphanedProcesses()
    {
        // V1 lesson: a crashed session leaves ffmpeg holding the mic; the next recording then
        // captures silence or fails. Sweep our own recordings at startup (marker: "capture-",
        // used by the session's temp filenames) — never other apps' ffmpeg.
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "pgrep",
                Arguments = $"-f \"ffmpeg.*{CaptureMarker}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var pgrep = Process.Start(psi);
            var output = pgrep!.StandardOutput.ReadToEnd();
            pgrep.WaitForExit(3000);
            if (pgrep.ExitCode != 0 || string.IsNullOrWhiteSpace(output)) return;

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!int.TryParse(line.Trim(), out var pid)) continue;
                try
                {
                    using var proc = Process.GetProcessById(pid);
                    proc.Kill(entireProcessTree: true);
                    _logger.LogWarning("[capture] killed orphaned recording pid={Pid} on startup", pid);
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[capture] orphan sweep failed");
        }
    }
}
