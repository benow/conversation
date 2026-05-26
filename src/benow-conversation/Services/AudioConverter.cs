using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace benow_conversation.Services;

public interface IAudioConverter
{
    bool IsFfmpegAvailable();
    Task<byte[]> ConvertPcmToMp3Async(byte[] pcmData, int sampleRate = 24000, int channels = 1);
}

public class AudioConverter : IAudioConverter
{
    private readonly ILogger<AudioConverter> _logger;
    private static bool? _ffmpegAvailable;

    public AudioConverter(ILogger<AudioConverter> logger)
    {
        _logger = logger;
    }

    public bool IsFfmpegAvailable()
    {
        if (_ffmpegAvailable.HasValue)
            return _ffmpegAvailable.Value;

        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = "-version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.Start();
            process.WaitForExit(5000);
            _ffmpegAvailable = process.ExitCode == 0;
            return _ffmpegAvailable.Value;
        }
        catch
        {
            _ffmpegAvailable = false;
            return false;
        }
    }

    public async Task<byte[]> ConvertPcmToMp3Async(byte[] pcmData, int sampleRate = 24000, int channels = 1)
    {
        if (!IsFfmpegAvailable())
            throw new InvalidOperationException("ffmpeg is not available on this system. Install ffmpeg to enable PCM to MP3 conversion.");

        var tempDir = Path.GetTempPath();
        var tempPcm = Path.Combine(tempDir, $"tts_{Guid.NewGuid():N}.wav");
        var tempMp3 = Path.Combine(tempDir, $"tts_{Guid.NewGuid():N}.mp3");

        try
        {
            WriteWavFile(tempPcm, pcmData, sampleRate, channels);

            var psi = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = $"-y -i \"{tempPcm}\" -b:a 128k \"{tempMp3}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            _logger.LogDebug("Running ffmpeg: {Args}", psi.Arguments);

            using var process = Process.Start(psi)!;
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                var error = await process.StandardError.ReadToEndAsync();
                throw new InvalidOperationException($"ffmpeg conversion failed (exit {process.ExitCode}): {error}");
            }

            return await File.ReadAllBytesAsync(tempMp3);
        }
        finally
        {
            if (File.Exists(tempPcm)) File.Delete(tempPcm);
            if (File.Exists(tempMp3)) File.Delete(tempMp3);
        }
    }

    internal static void WriteWavFile(string path, byte[] pcmData, int sampleRate, int channels)
    {
        using var fs = File.Create(path);
        using var writer = new BinaryWriter(fs);

        var bitsPerSample = 16;
        var byteRate = sampleRate * channels * bitsPerSample / 8;
        var blockAlign = channels * bitsPerSample / 8;
        var dataSize = pcmData.Length;

        writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataSize);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));

        writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write((short)blockAlign);
        writer.Write((short)bitsPerSample);

        writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        writer.Write(dataSize);
        writer.Write(pcmData);
    }
}
