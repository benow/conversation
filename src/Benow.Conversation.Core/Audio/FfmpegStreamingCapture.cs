using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Benow.Conversation.Audio;

/// <summary>
/// Streaming PCM capture: ffmpeg writes s16le frames to stdout and they are handed to the
/// callback as they arrive — VAD/STT can run WHILE the user is still speaking, which is what
/// makes time-to-transcript independent of utterance length. (The file-based recorder is fine
/// for short clips; this is the low-latency path the engine uses for live dictation.)
/// Cross-platform: Linux pulse, Windows dshow, macOS avfoundation.
/// </summary>
public sealed class FfmpegStreamingCapture
{
    private readonly ILogger<FfmpegStreamingCapture> _logger;
    private readonly CaptureOptions _options;
    private readonly CapturePlatform _platform;

    /// <summary>Frames emitted per callback (640 bytes = 20ms at 16kHz mono s16le).</summary>
    public const int FrameBytes = 640;

    public FfmpegStreamingCapture(ILogger<FfmpegStreamingCapture> logger, CaptureOptions? options = null)
    {
        _logger = logger;
        _options = options ?? new CaptureOptions();
        _platform = _options.Platform ?? FfmpegAudioRecorder.DetectPlatform();
    }

    /// <summary>
    /// Captures until <paramref name="ct"/> cancels, invoking <paramref name="onFrame"/> per
    /// 20ms frame. Returns the number of frames delivered.
    /// </summary>
    public async Task<long> CaptureAsync(Func<byte[], Task> onFrame, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _options.FfmpegPath,
            Arguments = BuildArgs(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"failed to start {_options.FfmpegPath} — is ffmpeg installed and on PATH?");

        var stderrTail = new List<string>();
        var stderrTask = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await proc.StandardError.ReadLineAsync()) != null)
                {
                    lock (stderrTail) { stderrTail.Add(line); if (stderrTail.Count > 20) stderrTail.RemoveAt(0); }
                }
            }
            catch { }
        });

        var frames = 0L;
        var buffer = new byte[FrameBytes];
        var filled = 0;
        var sw = Stopwatch.StartNew();

        try
        {
            var stdout = proc.StandardOutput.BaseStream;
            while (!ct.IsCancellationRequested)
            {
                var read = await stdout.ReadAsync(buffer.AsMemory(filled, FrameBytes - filled), ct);
                if (read == 0) break; // device closed
                filled += read;
                if (filled < FrameBytes) continue;

                await onFrame(buffer);
                frames++;
                filled = 0;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // normal stop (button release)
        }
        finally
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            try { await proc.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
            await Task.WhenAny(stderrTask, Task.Delay(300));
        }

        _logger.LogInformation("[capture] streaming capture ended: {Frames} frames ({Ms}ms) after {Elapsed}ms",
            frames, frames * 20, sw.ElapsedMilliseconds);

        if (frames == 0)
        {
            var tail = string.Join(" | ", Tail(stderrTail));
            _logger.LogWarning("[capture] no audio frames captured. ffmpeg said: {Tail}. " +
                "Fix: verify the input device (list with --devices) and that the mic is not muted", tail);
        }
        return frames;
    }

    private static List<string> Tail(List<string> lines)
    {
        lock (lines) return lines.TakeLast(4).ToList();
    }

    private string BuildArgs()
    {
        var device = _options.Device;
        var input = _platform switch
        {
            CapturePlatform.Linux => string.IsNullOrWhiteSpace(device) ? "-i default" : $"-i \"{device}\"",
            CapturePlatform.Windows => string.IsNullOrWhiteSpace(device) ? "-i audio=default" : $"-i audio=\"{device}\"",
            CapturePlatform.MacOs => string.IsNullOrWhiteSpace(device) ? "-i \":0\"" : $"-i \"{device}\"",
            _ => "-i default"
        };
        var format = _platform switch
        {
            CapturePlatform.Windows => "dshow",
            CapturePlatform.MacOs => "avfoundation",
            _ => "pulse"
        };
        // -fflags nobuffer / -flush_packets 1: emit frames immediately instead of buffering —
        // this is the difference between "transcribe while speaking" and "transcribe after".
        return $"-hide_banner -loglevel error -fflags nobuffer -flush_packets 1 -f {format} {input} " +
               $"-ac {_options.Channels} -ar {_options.SampleRate} -f s16le -";
    }
}
