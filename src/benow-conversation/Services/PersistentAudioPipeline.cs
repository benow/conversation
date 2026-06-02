using System.Diagnostics;
using benow_conversation.Configuration;
using Microsoft.Extensions.Logging;

namespace benow_conversation.Services;

public interface IPersistentAudioPipeline : IAsyncDisposable
{
    Task PipeAsync(Stream source, CancellationToken ct);
    Task InterruptAsync();
    Task StartAsync(CancellationToken ct);
}

public class PersistentAudioPipeline : IPersistentAudioPipeline
{
    private readonly ILogger<PersistentAudioPipeline> _logger;
    private readonly string _ffplayPath;
    private readonly int _pcmSampleRate;
    private readonly int _pcmChannels;
    private readonly int? _volume;
    private readonly string? _device;
    private Process? _process;
    private Stream? _stdin;
    private SemaphoreSlim _lock = new(1, 1);
    private bool _disposed;
    private DateTime _lastActivity = DateTime.MinValue;
    private static readonly TimeSpan IdleRestartThreshold = TimeSpan.FromMinutes(5);

    public PersistentAudioPipeline(
        ILogger<PersistentAudioPipeline> logger,
        AppSettings settings,
        string ffplayPath,
        int? volume = null,
        string? device = null)
    {
        _logger = logger;
        _ffplayPath = ffplayPath;
        _pcmSampleRate = settings.Audio.PcmSampleRate;
        _pcmChannels = settings.Audio.PcmChannels;
        _volume = volume;
        _device = device;
        KillOrphanedProcesses();
    }

    private void KillOrphanedProcesses()
    {
        try
        {
            foreach (var proc in Process.GetProcessesByName("ffplay"))
            {
                try
                {
                    proc.Kill(entireProcessTree: true);
                    _logger.LogWarning("Killed orphaned ffplay pid={Pid} on startup", proc.Id);
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clean up orphaned ffplay processes");
        }
    }

    public async Task StartAsync(CancellationToken ct)
    {
        await EnsureProcessAsync(ct);
    }

    public async Task PipeAsync(Stream source, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lock.WaitAsync(ct);
        try
        {
            var idle = DateTime.UtcNow - _lastActivity;
            if (_process != null && !_process.HasExited && idle > IdleRestartThreshold)
            {
                _logger.LogInformation("ffplay idle for {IdleSec:F0}s — restarting in case of sleep/resume", idle.TotalSeconds);
                KillProcess();
                _stdin = null;
                _process = null;
            }

            var freshStart = await EnsureProcessAsync(ct);
            if (freshStart)
            {
                _logger.LogDebug("Waiting for ffplay warmup after fresh start...");
                await Task.Delay(500, ct);
            }
            await source.CopyToAsync(_stdin!, ct);
            await _stdin!.FlushAsync(ct);
            _lastActivity = DateTime.UtcNow;
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Stdin pipe broken — restarting ffplay and retrying");
            await RestartProcessAsync(ct);
            await Task.Delay(200, ct);
            try
            {
                if (source.CanSeek)
                    source.Position = 0;
                await source.CopyToAsync(_stdin!, ct);
                await _stdin!.FlushAsync(ct);
                _lastActivity = DateTime.UtcNow;
                _logger.LogInformation("Pipe retry succeeded after ffplay restart");
            }
            catch (IOException retryEx)
            {
                _logger.LogError(retryEx, "Stdin pipe broken again after restart — giving up");
                throw;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error piping audio to ffplay");
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task InterruptAsync()
    {
        await _lock.WaitAsync();
        try
        {
            KillProcess();
            _stdin = null;
            _process = null;
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
        if (_process != null && !_process.HasExited) return false;
        StartProcess();
        _logger.LogInformation("Starting persistent ffplay (pcm={Rate}Hz/{Chans}ch, pid={Pid})", _pcmSampleRate, _pcmChannels, _process!.Id);
        await Task.Delay(50, ct);
        return true;
    }

    private void StartProcess()
    {
        var args = BuildArgs();
        var psi = new ProcessStartInfo
        {
            FileName = _ffplayPath,
            Arguments = args,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        _process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start ffplay");
        _stdin = _process.StandardInput.BaseStream;

        _ = Task.Run(async () =>
        {
            var stderr = await _process.StandardError.ReadToEndAsync();
            if (!string.IsNullOrWhiteSpace(stderr))
                _logger.LogWarning("ffplay stderr (pid={Pid}): {Stderr}", _process.Id, stderr);
        });
    }

    private async Task RestartProcessAsync(CancellationToken ct)
    {
        KillProcess();
        _stdin = null;
        _process = null;
        await EnsureProcessAsync(ct);
    }

    private void KillProcess()
    {
        if (_process == null || _process.HasExited) return;
        try { _stdin?.Close(); } catch { }
        try { _process.Kill(entireProcessTree: true); } catch { }
        try { _process.WaitForExit(2000); } catch { }
        _logger.LogInformation("ffplay terminated (pid={Pid})", _process.Id);
    }

    private string BuildArgs()
    {
        var args = "-nodisp -loglevel quiet";
        args += $" -f s16le -ar {_pcmSampleRate}";

        if (_volume.HasValue)
            args += $" -volume {Math.Clamp(_volume.Value, 0, 100)}";
        if (!string.IsNullOrWhiteSpace(_device))
            args += $" -audiodevice \"{_device}\"";

        args += " -i pipe:0";
        return args;
    }
}
