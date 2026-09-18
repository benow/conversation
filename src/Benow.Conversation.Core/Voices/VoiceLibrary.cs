using System.Diagnostics;
using Benow.Conversation.Audio;
using Benow.Conversation.Voice;
using Microsoft.Extensions.Logging;

namespace Benow.Conversation.Voices;

/// <summary>A reference voice in the local library.</summary>
public sealed record VoiceEntry(
    string Name,
    string FilePath,
    double DurationSec,
    int SampleRate,
    long SizeBytes,
    DateTimeOffset CreatedAt,
    IReadOnlyList<string> Warnings);

/// <summary>
/// The local reference-voice library: import any audio file (mp3/m4a/ogg/wav/…), record a new
/// sample from the mic, analyze quality, and normalize everything to 16 kHz mono 16-bit WAV —
/// the format both Whisper and XTTS want. Behavior ported from NASTV's voice library
/// (voiceSampleApi.ts/voiceReference.ts + the settings VoiceLibrary UX), reimplemented
/// server-side so the desktop app and NASTV (phase 4) share one implementation.
///
/// The library directory holds user assets (cloned-voice references, possibly personal) and is
/// NEVER meant to be committed — see the repo .gitignore; the default lives under
/// ~/.config/conversation/voices, outside any checkout.
/// </summary>
public sealed class VoiceLibrary
{
    private readonly ILogger<VoiceLibrary> _logger;
    private readonly string _directory;
    private readonly string _ffmpegPath;
    private readonly FfmpegAudioRecorder? _recorder;

    public VoiceLibrary(ILogger<VoiceLibrary> logger, string directory, string ffmpegPath = "ffmpeg",
        FfmpegAudioRecorder? recorder = null)
    {
        _logger = logger;
        _directory = directory;
        _ffmpegPath = ffmpegPath;
        _recorder = recorder;
        Directory.CreateDirectory(_directory);
    }

    /// <summary>Library directory (named DirectoryPath — a property called Directory would shadow System.IO.Directory).</summary>
    public string DirectoryPath => _directory;

    /// <summary>All reference voices, newest first, with lightweight metadata (no analysis).</summary>
    public IReadOnlyList<VoiceEntry> List()
    {
        if (!System.IO.Directory.Exists(_directory)) return Array.Empty<VoiceEntry>();
        return System.IO.Directory.GetFiles(_directory, "*.wav")
            .Select(f => new VoiceEntry(
                Path.GetFileName(f),
                f,
                DurationSecOf(f),
                VoiceReference.TargetSampleRate,
                new FileInfo(f).Length,
                File.GetLastWriteTimeUtc(f),
                Array.Empty<string>()))
            .OrderByDescending(e => e.CreatedAt)
            .ToList();
    }

    /// <summary>
    /// Imports an audio file: decode → analyze → select the best speech window → normalize to
    /// a 16 kHz mono WAV in the library. Returns the entry plus the analysis warnings (a
    /// sub-6s reference is saved but flagged — matching NASTV's "cloned voice may sound robotic").
    /// </summary>
    public async Task<(VoiceEntry Entry, VoiceAnalysis Analysis)> ImportAsync(
        string sourcePath, string? name = null, double targetSec = VoiceReference.DefaultTargetSec,
        CancellationToken ct = default)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException($"voice source not found: {sourcePath}", sourcePath);

        var analysis = await AnalyzeAsync(sourcePath, ct);
        var selected = VoiceReference.SelectSegment(analysis, targetSec);

        var baseName = !string.IsNullOrWhiteSpace(name) ? name! : Path.GetFileNameWithoutExtension(sourcePath);
        var destName = SanitizeName(baseName) + ".wav";
        var destPath = Path.Combine(_directory, destName);

        // Rebuild the clip from the selected ranges: decode → trim → concat → 16k mono WAV.
        var tempPcm = Path.Combine(Path.GetTempPath(), $"voice-import-{Guid.NewGuid():N}.wav");
        try
        {
            var filters = selected.Ranges.Count == 1
                ? $"-ss {selected.Ranges[0].StartSec.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)} " +
                  $"-t {selected.Ranges[0].EndSec - selected.Ranges[0].StartSec:F3}"
                : null;

            if (filters != null)
            {
                await RunFfmpegAsync($"-y -i \"{sourcePath}\" {filters} -ac 1 -ar {VoiceReference.TargetSampleRate} " +
                                     $"-acodec pcm_s16le \"{tempPcm}\"", ct);
            }
            else
            {
                // Multi-range: cut each range and concat via the filter graph.
                var parts = new List<string>();
                var graph = new System.Text.StringBuilder();
                for (var i = 0; i < selected.Ranges.Count; i++)
                {
                    var (start, end) = selected.Ranges[i];
                    graph.Append($"[0:a]atrim=start={start.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}:" +
                                 $"end={end.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)},asetpts=PTS-STARTPTS[a{i}];");
                    parts.Add($"[a{i}]");
                }
                graph.Append(string.Join("", parts)).Append($"concat=n={selected.Ranges.Count}:v=0:a=1[out]");
                await RunFfmpegAsync($"-y -i \"{sourcePath}\" -filter_complex \"{graph}\" -map \"[out]\" " +
                                     $"-ac 1 -ar {VoiceReference.TargetSampleRate} -acodec pcm_s16le \"{tempPcm}\"", ct);
            }

            File.Move(tempPcm, destPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPcm)) File.Delete(tempPcm);
        }

        var entry = new VoiceEntry(destName, destPath, DurationSecOf(destPath), VoiceReference.TargetSampleRate,
            new FileInfo(destPath).Length, DateTimeOffset.UtcNow, selected.Warnings);

        _logger.LogInformation("[voices] imported \"{Source}\" → {Dest} ({Duration:F1}s, {Speech:F1}s speech, {Runs} run(s)); warnings: {Warnings}",
            Path.GetFileName(sourcePath), destName, entry.DurationSec, selected.SpeechSec, analysis.Runs.Count,
            selected.Warnings.Count == 0 ? "none" : string.Join(" | ", selected.Warnings));
        return (entry, analysis);
    }

    /// <summary>Records a new reference from the mic (via ffmpeg capture) and imports it.</summary>
    public async Task<(VoiceEntry Entry, VoiceAnalysis Analysis)> RecordAsync(
        string name, TimeSpan duration, CancellationToken ct = default)
    {
        if (_recorder == null)
            throw new InvalidOperationException("no recorder configured — construct VoiceLibrary with an FfmpegAudioRecorder to record");

        var raw = Path.Combine(Path.GetTempPath(), $"conversation-capture-{Guid.NewGuid():N}.wav");
        try
        {
            _logger.LogInformation("[voices] recording \"{Name}\" for {Seconds:F0}s…", name, duration.TotalSeconds);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(duration);
            // The recorder runs until cancelled, then finalizes the WAV.
            try { await _recorder.RecordToFileAsync(raw, cts.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* duration elapsed — normal */ }

            return await ImportAsync(raw, name, VoiceReference.DefaultTargetSec, ct);
        }
        finally
        {
            if (File.Exists(raw)) File.Delete(raw);
        }
    }

    public bool Delete(string name)
    {
        var path = Path.Combine(_directory, SanitizeName(Path.GetFileNameWithoutExtension(name)) + ".wav");
        if (!File.Exists(path)) return false;
        File.Delete(path);
        _logger.LogInformation("[voices] deleted {Name}", Path.GetFileName(path));
        return true;
    }

    /// <summary>Decodes any supported audio file to mono 16k float samples and analyzes speech runs.</summary>
    public async Task<VoiceAnalysis> AnalyzeAsync(string path, CancellationToken ct = default)
    {
        var pcm = await DecodeToPcmAsync(path, ct);
        var samples = new float[pcm.Length / 2];
        for (var i = 0; i < samples.Length; i++)
        {
            var s = (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8));
            samples[i] = s / 32768f;
        }

        var analysis = VoiceReference.Analyze(samples, VoiceReference.TargetSampleRate);
        _logger.LogInformation("[voices] analyzed {File}: {Duration:F1}s, {Runs} speech run(s), {Speech:F1}s speech, longest {Longest:F1}s",
            Path.GetFileName(path), analysis.DurationSec, analysis.Runs.Count, analysis.SpeechSec, analysis.LongestRunSec);
        return analysis;
    }

    private async Task<byte[]> DecodeToPcmAsync(string path, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            // s16le → stdout; 16 kHz mono matches the rest of the pipeline.
            Arguments = $"-v error -i \"{path}\" -ac 1 -ar {VoiceReference.TargetSampleRate} -f s16le -",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"failed to start {_ffmpegPath}");
        using var ms = new MemoryStream();
        var copy = proc.StandardOutput.BaseStream.CopyToAsync(ms, ct);
        var err = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        await copy;
        if (proc.ExitCode != 0)
        {
            var stderr = await err;
            throw new InvalidOperationException($"ffmpeg decode failed for {Path.GetFileName(path)} (exit {proc.ExitCode}): " +
                $"{stderr[..Math.Min(300, stderr.Length)]} — is the file a supported audio format?");
        }
        return ms.ToArray();
    }

    private async Task RunFfmpegAsync(string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"failed to start {_ffmpegPath}");
        var err = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        if (proc.ExitCode != 0)
        {
            var stderr = await err;
            throw new InvalidOperationException($"ffmpeg failed (exit {proc.ExitCode}): {stderr[..Math.Min(300, stderr.Length)]}");
        }
    }

    private double DurationSecOf(string wavPath)
    {
        try
        {
            var bytes = new FileInfo(wavPath).Length;
            return PcmBytesOfWav(wavPath) / 2.0 / VoiceReference.TargetSampleRate;
        }
        catch { return 0; }
    }

    private static long PcmBytesOfWav(string path)
    {
        // Read just the header to find the data chunk size.
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs);
        var header = br.ReadBytes((int)Math.Min(4096, fs.Length));
        for (var i = 12; i + 8 <= header.Length - 8; i++)
        {
            if (header[i] == 'd' && header[i + 1] == 'a' && header[i + 2] == 't' && header[i + 3] == 'a')
                return BitConverter.ToInt32(header, i + 4);
        }
        return Math.Max(0, fs.Length - 44);
    }

    /// <summary>Filesystem-safe name (keeps the human-readable stem, strips separators).</summary>
    internal static string SanitizeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) || c == '/' || c == '\\' ? '_' : c).ToArray()).Trim();
        if (cleaned.Length == 0) cleaned = "voice";
        return cleaned[..Math.Min(cleaned.Length, 120)];
    }
}
