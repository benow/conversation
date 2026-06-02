using System.Diagnostics;
using benow_conversation.Configuration;
using benow_conversation.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace benow_conversation.Services;

/// <summary>Converts TTS audio from provider-specific formats to the PCM pipeline target format.</summary>
public class AudioFormatConverter
{
    private readonly AppSettings _settings;
    private readonly ILogger<AudioFormatConverter> _logger;

    public AudioFormatConverter(IOptions<AppSettings> settings, ILogger<AudioFormatConverter> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public AudioFormat TargetFormat => new(
        "pcm",
        _settings.Audio.PcmSampleRate,
        _settings.Audio.PcmChannels,
        _settings.Audio.PcmBitsPerSample);

    /// <summary>Convert audio bytes from source format to the PCM target format.</summary>
    public async Task<byte[]> ConvertAsync(byte[] audio, AudioFormat sourceFormat, CancellationToken ct)
    {
        if (audio.Length == 0) return audio;

        if (sourceFormat.Codec == "pcm" && sourceFormat.IsPcmCompatibleWith(TargetFormat))
        {
            _logger.LogDebug("PCM passthrough ({Size} bytes, {Rate}Hz)", audio.Length, sourceFormat.SampleRate);
            return audio;
        }

        if (sourceFormat.Codec == "wav" && sourceFormat.IsPcmCompatibleWith(TargetFormat))
        {
            var stripped = StripWavHeader(audio);
            if (stripped != null)
            {
                _logger.LogDebug("WAV header stripped ({Size} → {Stripped} bytes, {Rate}Hz mono {Bits}bit)",
                    audio.Length, stripped.Length, sourceFormat.SampleRate, sourceFormat.BitsPerSample);
                return stripped;
            }
            _logger.LogWarning("WAV header parse failed, using ffmpeg fallback");
            return await ConvertViaFfmpegAsync(audio, sourceFormat, ct);
        }

        _logger.LogDebug("Format conversion: {Source} → {Target} ({Size} bytes)",
            FormatToString(sourceFormat), FormatToString(TargetFormat), audio.Length);
        return await ConvertViaFfmpegAsync(audio, sourceFormat, ct);
    }

    private static string FormatToString(AudioFormat f) =>
        $"{f.Codec}/{f.SampleRate}/{f.Channels}ch/{f.BitsPerSample}bit";

    /// <summary>Parse WAV header, find "data" chunk, return raw PCM bytes after it.</summary>
    public static byte[]? StripWavHeader(byte[] wav)
    {
        if (wav.Length < 44) return null;
        if (wav[0] != 'R' || wav[1] != 'I' || wav[2] != 'F' || wav[3] != 'F') return null;

        var offset = 12;
        while (offset + 8 <= wav.Length)
        {
            var chunkId = System.Text.Encoding.ASCII.GetString(wav, offset, 4);
            var chunkSize = BitConverter.ToInt32(wav, offset + 4);

            if (chunkId == "data")
            {
                var dataStart = offset + 8;
                var dataLen = wav.Length - dataStart;
                if (dataLen <= 0) return null;
                var pcm = new byte[Math.Min(dataLen, chunkSize)];
                Array.Copy(wav, dataStart, pcm, 0, pcm.Length);
                return pcm;
            }

            offset += 8 + chunkSize;
            if (offset >= wav.Length) break;
        }

        return null;
    }

    /// <summary>Parse WAV header to extract AudioFormat from "fmt " chunk.</summary>
    public static AudioFormat? ParseWavHeader(byte[] wav)
    {
        if (wav.Length < 44) return null;
        if (wav[0] != 'R' || wav[1] != 'I' || wav[2] != 'F' || wav[3] != 'F') return null;

        var offset = 12;
        while (offset + 8 <= wav.Length)
        {
            var chunkId = System.Text.Encoding.ASCII.GetString(wav, offset, 4);
            var chunkSize = BitConverter.ToInt32(wav, offset + 4);

            if (chunkId == "fmt ")
            {
                var fmtOffset = offset + 8;
                if (fmtOffset + 16 > wav.Length) return null;

                var audioFormat = BitConverter.ToInt16(wav, fmtOffset);
                var channels = BitConverter.ToInt16(wav, fmtOffset + 2);
                var sampleRate = BitConverter.ToInt32(wav, fmtOffset + 4);
                var bitsPerSample = BitConverter.ToInt16(wav, fmtOffset + 14);

                if (audioFormat != 1) return null;

                return new AudioFormat("wav", sampleRate, channels, bitsPerSample);
            }

            offset += 8 + chunkSize;
            if (offset >= wav.Length) break;
        }

        return null;
    }

    /// <summary>Detect audio format from the first bytes of audio data.</summary>
    public static AudioFormat? DetectFormat(byte[] audio, string providerHint)
    {
        if (audio.Length < 4) return null;

        if (audio[0] == 'R' && audio[1] == 'I' && audio[2] == 'F' && audio[3] == 'F')
            return ParseWavHeader(audio);

        if (providerHint.Equals("openrouter", StringComparison.OrdinalIgnoreCase))
            return AudioFormat.Mp3_24000Mono;

        if (providerHint.Equals("kokoro", StringComparison.OrdinalIgnoreCase))
            return AudioFormat.Pcm24000Mono;

        return null;
    }

    private async Task<byte[]> ConvertViaFfmpegAsync(byte[] audio, AudioFormat sourceFormat, CancellationToken ct)
    {
        if (!IsFfmpegAvailable())
            throw new InvalidOperationException("ffmpeg is not available, cannot convert audio format.");

        var args = BuildFfmpegArgs(sourceFormat, TargetFormat);

        _logger.LogDebug("Starting one-shot ffmpeg: {Args}, input={InputSize} bytes", args, audio.Length);

        var psi = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = args,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start ffmpeg for audio conversion");

        var stderrTask = Task.Run(async () =>
        {
            try
            {
                var stderr = await process.StandardError.ReadToEndAsync(ct);
                if (!string.IsNullOrWhiteSpace(stderr))
                    _logger.LogWarning("ffmpeg convert stderr (pid={Pid}): {Stderr}", process.Id, stderr.Trim());
            }
            catch { }
        }, ct);

        await process.StandardInput.BaseStream.WriteAsync(audio, ct);
        process.StandardInput.Close();

        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await process.StandardOutput.BaseStream.ReadAsync(buffer, ct)) > 0)
            await ms.WriteAsync(buffer.AsMemory(0, read), ct);

        await process.WaitForExitAsync(ct);
        await stderrTask;

        if (process.ExitCode != 0)
            _logger.LogWarning("ffmpeg convert exited with code {ExitCode}", process.ExitCode);

        var result = ms.ToArray();
        _logger.LogDebug("ffmpeg conversion complete: {InputSize} → {OutputSize} bytes", audio.Length, result.Length);
        return result;
    }

    private static string BuildFfmpegArgs(AudioFormat source, AudioFormat target)
    {
        var args = "-loglevel error";

        args += source.Codec switch
        {
            "mp3" => " -f mp3",
            "wav" => " -f wav",
            _ => $" -f s16le -ar {source.SampleRate} -ac {source.Channels}"
        };

        args += " -i pipe:0";

        if (source.Codec == "pcm")
            args += $" -ar {source.SampleRate} -ac {source.Channels} -f s16le";

        args += $" -f s16le -ar {target.SampleRate} -ac {target.Channels} pipe:1";

        return args;
    }

    private static bool? _ffmpegAvailable;
    private static bool IsFfmpegAvailable()
    {
        if (_ffmpegAvailable.HasValue) return _ffmpegAvailable.Value;
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = "-version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process == null) { _ffmpegAvailable = false; return false; }
            process.WaitForExit(5000);
            _ffmpegAvailable = process.ExitCode == 0;
        }
        catch
        {
            _ffmpegAvailable = false;
        }
        return _ffmpegAvailable.Value;
    }
}
