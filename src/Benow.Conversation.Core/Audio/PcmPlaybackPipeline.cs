using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Benow.Conversation.Audio;

/// <summary>Options for the persistent PCM playback pipeline.</summary>
public sealed record PcmPlaybackOptions
{
    /// <summary>ffplay executable (PATH lookup when bare).</summary>
    public string FfplayPath { get; init; } = "ffplay";
    public int SampleRate { get; init; } = 24000;
    public int Channels { get; init; } = 1;
    /// <summary>0-100 output volume, or null to leave ffplay's default.</summary>
    public int? Volume { get; init; }
    /// <summary>Output device name (SDL/ALSA), or null for the system default.</summary>
    public string? Device { get; init; }
    /// <summary>Restart ffplay after this much idle time (guards sleep/resume wedges).</summary>
    public TimeSpan IdleRestartThreshold { get; init; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// A long-lived `ffplay` process fed raw s16le PCM on stdin — the playback primitive for
/// progressive TTS (each chunk is piped as it synthesizes, so audio starts before the reply
/// finishes). Ported and generalized from V1's PersistentAudioPipeline (2026-09-17, phase 2),
/// keeping its hard-won behaviors: orphan cleanup at startup, idle restart (sleep/resume),
/// broken-pipe restart-and-retry, and interrupt-on-new-turn.
/// </summary>
public sealed class PcmPlaybackPipeline : IAsyncDisposable
{
    private readonly ILogger<PcmPlaybackPipeline> _logger;
    private readonly PcmPlaybackOptions _options;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private Process? _process;
    private Stream? _stdin;
    private DateTime _lastActivity = DateTime.MinValue;
    private bool _disposed;

    public PcmPlaybackPipeline(ILogger<PcmPlaybackPipeline> logger, PcmPlaybackOptions? options = null)
    {
        _logger = logger;
        _options = options ?? new PcmPlaybackOptions();
        KillOrphanedProcesses();
    }

    /// <summary>Prewarms the process so the first chunk doesn't pay process-start latency.</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try { await EnsureProcessAsync(ct); }
        finally { _lock.Release(); }
    }

    /// <summary>Pipes a PCM stream (s16le, options sample rate/channels) into the player.</summary>
    public async Task PipeAsync(Stream pcm, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lock.WaitAsync(ct);
        try
        {
            var idle = DateTime.UtcNow - _lastActivity;
            if (_process is { HasExited: false } && idle > _options.IdleRestartThreshold)
            {
                _logger.LogInformation("[playback] ffplay idle {IdleSec:F0}s — restarting in case of sleep/resume", idle.TotalSeconds);
                KillProcess();
            }

            var freshStart = await EnsureProcessAsync(ct);
            if (freshStart)
            {
                // ffplay needs a moment to initialize its audio device before it accepts PCM.
                await Task.Delay(500, ct);
            }
            await pcm.CopyToAsync(_stdin!, ct);
            await _stdin!.FlushAsync(ct);
            _lastActivity = DateTime.UtcNow;
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "[playback] stdin pipe broken — restarting ffplay and retrying once");
            RestartProcess();
            await Task.Delay(200, ct);
            try
            {
                if (pcm.CanSeek) pcm.Position = 0;
                await pcm.CopyToAsync(_stdin!, ct);
                await _stdin!.FlushAsync(ct);
                _lastActivity = DateTime.UtcNow;
                _logger.LogInformation("[playback] retry after restart succeeded");
            }
            catch (IOException retryEx)
            {
                _logger.LogError(retryEx, "[playback] stdin pipe broken again after restart — giving up on this chunk. " +
                    "Fix: check the audio device (ffplay -nodisp -f s16le -ar {Rate} -i /dev/zero) and that no other app holds it exclusively", _options.SampleRate);
                throw;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Stops current audio immediately (new turn / user interrupt).</summary>
    public async Task InterruptAsync()
    {
        await _lock.WaitAsync();
        try
        {
            KillProcess();
            _lastActivity = DateTime.MinValue;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        KillProcess();
        _lock.Dispose();
        await ValueTask.CompletedTask;
    }

    private async Task<bool> EnsureProcessAsync(CancellationToken ct)
    {
        if (_process is { HasExited: false }) return false;
        StartProcess();
        _logger.LogInformation("[playback] started persistent ffplay (pcm={Rate}Hz/{Channels}ch, pid={Pid})",
            _options.SampleRate, _options.Channels, _process!.Id);
        await Task.Delay(50, ct);
        return true;
    }

    private void StartProcess()
    {
        var psi = new ProcessStartInfo
        {
            FileName = _options.FfplayPath,
            Arguments = BuildArgs(),
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        _process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {_options.FfplayPath} — is ffmpeg (ffplay) installed and on PATH?");
        _stdin = _process.StandardInput.BaseStream;

        _ = Task.Run(async () =>
        {
            try
            {
                var stderr = await _process.StandardError.ReadToEndAsync();
                if (!string.IsNullOrWhiteSpace(stderr))
                    _logger.LogWarning("[playback] ffplay stderr: {Stderr}", stderr);
            }
            catch { /* process torn down */ }
        });
    }

    private void RestartProcess()
    {
        KillProcess();
        try
        {
            StartProcess();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[playback] failed to restart ffplay — audio will be dropped this turn. " +
                "Fix: verify ffplay is installed ({Path}) and the audio device is free", _options.FfplayPath);
            _process = null;
            _stdin = null;
        }
    }

    private void KillProcess()
    {
        if (_process == null) { _stdin = null; return; }
        try { _stdin?.Close(); } catch { }
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { }
        try { _process.WaitForExit(2000); } catch { }
        _process = null;
        _stdin = null;
    }

    /// <summary>Marker argument identifying OUR ffplay instances in the process list.</summary>
    private const string WindowTitle = "conversation-pcm";

    private void KillOrphanedProcesses()
    {
        // V1 lesson: a crashed session leaves ffplay holding the audio device; the next start
        // then fails or plays silence. Sweep ONLY our own leftovers (identified by the marker
        // argument) — the inherited blanket "kill every ffplay" also killed unrelated players
        // and made parallel test runs interfere (caught 2026-09-18).
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "pgrep",
                Arguments = $"-f \"ffplay.*{WindowTitle}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var pgrep = Process.Start(psi);
            if (pgrep == null) return;
            var output = pgrep.StandardOutput.ReadToEnd();
            pgrep.WaitForExit(3000);
            if (pgrep.ExitCode != 0 || string.IsNullOrWhiteSpace(output)) return;

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!int.TryParse(line.Trim(), out var pid)) continue;
                try
                {
                    using var proc = Process.GetProcessById(pid);
                    proc.Kill(entireProcessTree: true);
                    _logger.LogWarning("[playback] killed orphaned ffplay pid={Pid} on startup", pid);
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[playback] could not sweep orphaned ffplay processes");
        }
    }

    private string BuildArgs()
    {
        var args = $"-nodisp -loglevel quiet -window_title {WindowTitle}";
        args += $" -f s16le -ar {_options.SampleRate}";
        if (_options.Channels == 2) args += " -ch_layout stereo";
        if (_options.Volume.HasValue)
            args += $" -volume {Math.Clamp(_options.Volume.Value, 0, 100)}";
        if (!string.IsNullOrWhiteSpace(_options.Device))
            args += $" -audiodevice \"{_options.Device}\"";
        args += " -i pipe:0";
        return args;
    }
}
