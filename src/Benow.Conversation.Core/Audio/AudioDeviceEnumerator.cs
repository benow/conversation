using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Benow.Conversation.Audio;

/// <summary>An input device the user can pick. <see cref="Id"/> is what CaptureOptions.Device takes.</summary>
public sealed record AudioDevice(string Id, string Name, bool IsDefault = false);

/// <summary>
/// Cross-platform input-device enumeration for the input-device menu (plan §4.7 — device
/// selection with the "System default (follow)" default). Per platform:
///   Linux   — `pactl list short sources` (PipeWire/PulseAudio source names).
///   Windows — ffmpeg dshow device list (audio section).
///   macOS   — ffmpeg avfoundation device list (audio section).
/// Best-effort: any failure returns an empty list and the UI shows only "System default".
/// </summary>
public sealed class AudioDeviceEnumerator
{
    private readonly ILogger<AudioDeviceEnumerator> _logger;
    private readonly string _ffmpegPath;

    public AudioDeviceEnumerator(ILogger<AudioDeviceEnumerator> logger, string ffmpegPath = "ffmpeg")
    {
        _logger = logger;
        _ffmpegPath = ffmpegPath;
    }

    public Task<IReadOnlyList<AudioDevice>> ListInputsAsync(CancellationToken ct)
    {
        try
        {
            if (OperatingSystem.IsWindows()) return ListViaFfmpegAsync("dshow", ct);
            if (OperatingSystem.IsMacOS()) return ListViaFfmpegAsync("avfoundation", ct);
            return ListPulseAsync(ct, "sources");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[devices] input enumeration failed — showing system default only");
            return Task.FromResult<IReadOnlyList<AudioDevice>>(Array.Empty<AudioDevice>());
        }
    }

    /// <summary>
    /// Playback devices for the output menu. The Id is an ffplay `-audiodevice` value, which on
    /// Linux is a sink name — the same pulse name space as a source, so the list comes from
    /// `pactl list short sinks`.
    /// </summary>
    public Task<IReadOnlyList<AudioDevice>> ListOutputsAsync(CancellationToken ct)
    {
        try
        {
            if (OperatingSystem.IsWindows()) return ListViaFfmpegAsync("dshow", ct);
            if (OperatingSystem.IsMacOS()) return ListViaFfmpegAsync("avfoundation", ct);
            return ListPulseAsync(ct, "sinks");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[devices] output enumeration failed — showing system default only");
            return Task.FromResult<IReadOnlyList<AudioDevice>>(Array.Empty<AudioDevice>());
        }
    }

    private async Task<IReadOnlyList<AudioDevice>> ListPulseAsync(CancellationToken ct, string kind)
    {
        var output = await RunAsync("pactl", $"list short {kind}", ct);
        var devices = new List<AudioDevice>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // "56\talsa_input.pci-...analog-stereo\tPipeWire\ts32le 2ch 48000Hz\tSUSPENDED"
            var cols = line.Split('\t');
            if (cols.Length < 2) continue;
            var name = cols[1].Trim();
            if (name.Length == 0) continue;
            // Skip monitor sources (loopback of the output, never a useful mic). Sinks have no monitors.
            if (name.Contains(".monitor", StringComparison.Ordinal)) continue;
            devices.Add(new AudioDevice(name, Beautify(name)));
        }
        _logger.LogInformation("[devices] {Count} PulseAudio {Kind}", devices.Count, kind);
        return devices;
    }

    private async Task<IReadOnlyList<AudioDevice>> ListViaFfmpegAsync(string format, CancellationToken ct)
    {
        // ffmpeg prints the device list to STDERR and exits non-zero ("dummy" input expected to fail).
        var stderr = await RunAsync(_ffmpegPath, $"-hide_banner -f {format} -list_devices true -i dummy", ct, captureStderr: true);
        var devices = new List<AudioDevice>();
        var audioSection = false;
        foreach (var line in stderr.Split('\n'))
        {
            if (line.Contains("DirectShow video devices", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("AVFoundation video devices", StringComparison.OrdinalIgnoreCase))
            {
                audioSection = false;
                continue;
            }
            if (line.Contains("DirectShow audio devices", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("AVFoundation audio devices", StringComparison.OrdinalIgnoreCase))
            {
                audioSection = true;
                continue;
            }
            if (!audioSection) continue;

            if (format == "dshow")
            {
                // [dshow @ ...]  "Microphone (USB Audio)"   (audio)
                var m = Regex.Match(line, "\"([^\"]+)\"\\s*\\(audio\\)");
                if (m.Success) devices.Add(new AudioDevice(m.Groups[1].Value, m.Groups[1].Value));
            }
            else
            {
                // [AVFoundation indev @ ...] [0] MacBook Pro Microphone
                var m = Regex.Match(line, @"\[(\d+)\]\s+(.+)$");
                if (m.Success) devices.Add(new AudioDevice($":{m.Groups[1].Value}", m.Groups[2].Value.Trim()));
            }
        }
        _logger.LogInformation("[devices] {Count} {Format} input device(s)", devices.Count, format);
        return devices;
    }

    private static string Beautify(string pulseName)
    {
        // alsa_input.pci-0000_c2_00.6.analog-stereo
        //   → "Analog Stereo (alsa_input, pci-0000 c2 00.6)"
        // The split must be on the LAST '.'-separated segment (the device kind), not the
        // third field of a 3-way split — "pci-0000_c2_00.6.analog-stereo" contains dots
        // (caught 2026-09-18: the menu showed "6.analog stereo").
        var kind = pulseName.Split('.').LastOrDefault() ?? pulseName;
        var source = pulseName.Split('.').FirstOrDefault() ?? "";
        var label = kind.Replace('-', ' ').Replace('_', ' ').Trim();
        if (label.Length == 0) label = pulseName;
        label = char.ToUpperInvariant(label[0]) + label[1..];
        return source.Length > 0 && source != kind
            ? $"{label} ({source})"
            : label;
    }

    private async Task<string> RunAsync(string fileName, string args, CancellationToken ct, bool captureStderr = false)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var proc = Process.Start(psi);
        if (proc == null) return "";
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        return captureStderr ? await stderrTask : await stdoutTask;
    }
}
