namespace Benow.Conversation.Voices;

/// <summary>A detected speech run in a reference clip.</summary>
public readonly record struct SpeechRun(double StartSec, double EndSec, double MeanRms, double StdRms, double Stability, double Score)
{
    public double DurationSec => EndSec - StartSec;
}

/// <summary>Batch analysis of a decoded reference clip (mono float samples).</summary>
public sealed record VoiceAnalysis(
    double DurationSec,
    double SpeechSec,
    IReadOnlyList<SpeechRun> Runs,
    IReadOnlyList<string> Warnings)
{
    public double LongestRunSec => Runs.Count == 0 ? 0 : Runs.Max(r => r.DurationSec);
}

/// <summary>A chosen reference window: the source ranges, in chronological order.</summary>
public sealed record SelectedSegment(
    IReadOnlyList<(double StartSec, double EndSec)> Ranges,
    double DurationSec,
    double SpeechSec,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Reference-voice import analysis — the server-side port of NASTV's client-side
/// voiceReference.ts (2026-09-17, phase 2 review). Same philosophy as the live STT VAD
/// (20ms RMS frames, adaptive noise floor, 200ms pause gaps) but run in batch over a whole
/// file: decode → mono → frame-RMS analysis → speech-run detection → best-segment selection.
/// Works on -1..1 float samples (matching the TS implementation's math).
/// </summary>
public static class VoiceReference
{
    public const int TargetSampleRate = 16000;
    private const int FrameMs = 20;
    private const int GapMs = 200;
    private const double MinRunSec = 0.8;
    /// <summary>XTTS v2 needs ≥6s of reference for a stable speaker embedding.</summary>
    public const double MinGoodReferenceSec = 6;

    /// <summary>Default target clip length for an imported reference.</summary>
    public const double DefaultTargetSec = 10;

    public static VoiceAnalysis Analyze(float[] samples, int sampleRate)
    {
        var rms = FrameRms(samples, sampleRate);
        var threshold = ComputeThreshold(rms);
        var runs = DetectRuns(rms, threshold);
        var speechSec = runs.Sum(r => r.DurationSec);
        var warnings = new List<string>();
        if (runs.Count == 0)
            warnings.Add("No clear speech detected — a clean voice sample clones much better.");
        else if (speechSec < MinGoodReferenceSec)
            warnings.Add($"Only {speechSec:F1}s of speech found — references under {MinGoodReferenceSec:F0}s clone poorly (robotic).");

        return new VoiceAnalysis(samples.Length / (double)sampleRate, speechSec, runs, warnings);
    }

    /// <summary>Chooses the best reference window(s): one long stable run when possible.</summary>
    public static SelectedSegment SelectSegment(VoiceAnalysis analysis, double targetSec)
    {
        var warnings = analysis.Warnings.ToList();
        var runs = analysis.Runs.Where(r => r.DurationSec >= MinRunSec).ToList();

        if (runs.Count == 0)
        {
            warnings.Add("No clear speech detected — using the start of the file.");
            var endSec = Math.Min(targetSec, analysis.DurationSec);
            return new SelectedSegment(new[] { (0.0, endSec) }, endSec, endSec, warnings);
        }

        // Greedy pick best-scoring runs until the target duration is covered.
        var picked = new List<SpeechRun>();
        var total = 0.0;
        foreach (var r in runs.OrderByDescending(r => r.Score))
        {
            picked.Add(r);
            total += r.DurationSec;
            if (total >= targetSec) break;
        }
        picked = picked.OrderBy(r => r.StartSec).ToList();

        // Case 1: one run alone is long enough — take its most stable target-length window.
        if (picked.Count == 1 && picked[0].DurationSec >= targetSec)
        {
            var win = BestStableWindow(analysis, picked[0], targetSec);
            return new SelectedSegment(new[] { win }, targetSec, targetSec, warnings);
        }

        // Case 2: concatenate the picked runs, trimming to fit.
        var totalWanted = Math.Min(total, targetSec);
        if (totalWanted < targetSec)
            warnings.Add($"Only {totalWanted:F1}s of clear speech found (wanted {targetSec:F0}s) — consider a longer file.");
        if (totalWanted < MinGoodReferenceSec)
            warnings.Add($"Reference is under {MinGoodReferenceSec:F0}s — cloned voice may sound robotic.");

        var ranges = new List<(double, double)>();
        var remaining = totalWanted;
        foreach (var r in picked)
        {
            if (remaining <= 0) break;
            var take = Math.Min(r.DurationSec, remaining);
            ranges.Add((r.StartSec, r.StartSec + take));
            remaining -= take;
        }
        return new SelectedSegment(ranges, totalWanted, totalWanted, warnings);
    }

    private static (double StartSec, double EndSec) BestStableWindow(VoiceAnalysis analysis, SpeechRun run, double targetSec)
        => (run.StartSec, Math.Min(run.StartSec + targetSec, run.EndSec));

    private static float[] FrameRms(float[] samples, int sampleRate)
    {
        var frameLen = Math.Max(1, (int)Math.Round(sampleRate * FrameMs / 1000.0));
        var frames = samples.Length / frameLen;
        var rms = new float[frames];
        for (var f = 0; f < frames; f++)
        {
            var baseIdx = f * frameLen;
            double sum = 0;
            for (var i = 0; i < frameLen; i++)
            {
                var s = samples[baseIdx + i];
                sum += s * s;
            }
            rms[f] = (float)Math.Sqrt(sum / frameLen);
        }
        return rms;
    }

    private static double Percentile(float[] values, double p)
    {
        if (values.Length == 0) return 0;
        var sorted = values.OrderBy(v => v).ToArray();
        var idx = Math.Min(sorted.Length - 1, (int)Math.Floor(p / 100.0 * sorted.Length));
        return sorted[idx];
    }

    private static double ComputeThreshold(float[] rms)
    {
        // Adaptive noise floor: the 10th percentile of frame energies is a robust silence
        // estimate — EXCEPT when the file is almost entirely speech (the ideal reference!),
        // where the percentile lands inside the speech and noiseFloor*4 exceeds the speech
        // level itself, so nothing is detected (found 2026-09-18 while porting: a 12s tone in
        // a 13s file produced ZERO runs). Cap the threshold at half the typical LOUD level
        // (90th percentile) so speech-dense files stay detectable while silence-only files
        // still yield no runs.
        var noiseFloor = Math.Max(Percentile(rms, 10), 1e-4);
        var loudLevel = Percentile(rms, 90);
        var adaptive = noiseFloor * 4;
        var cap = Math.Max(loudLevel * 0.5, 0.02);
        return Math.Max(0.01, Math.Min(adaptive, cap));
    }

    private static List<SpeechRun> DetectRuns(float[] rms, double threshold)
    {
        var gapFrames = Math.Max(1, (int)Math.Round(GapMs / (double)FrameMs));
        var minRunFrames = Math.Max(1, (int)Math.Round(MinRunSec * 1000 / FrameMs));

        var runs = new List<SpeechRun>();
        var start = -1;
        var lastSpeech = -1;
        for (var f = 0; f <= rms.Length; f++)
        {
            var isSpeech = f < rms.Length && rms[f] >= threshold;
            if (isSpeech)
            {
                if (start == -1) start = f;
                lastSpeech = f;
            }
            else if (start != -1 && f - lastSpeech > gapFrames)
            {
                if (lastSpeech - start + 1 >= minRunFrames) runs.Add(ScoreRun(rms, start, lastSpeech));
                start = -1;
            }
        }
        if (start != -1 && lastSpeech - start + 1 >= minRunFrames) runs.Add(ScoreRun(rms, start, lastSpeech));
        return runs;
    }

    private static SpeechRun ScoreRun(float[] rms, int startFrame, int endFrame)
    {
        var startSec = startFrame * FrameMs / 1000.0;
        var endSec = (endFrame + 1) * FrameMs / 1000.0;
        double sum = 0, sumSq = 0;
        var len = endFrame - startFrame + 1;
        for (var f = startFrame; f <= endFrame; f++)
        {
            sum += rms[f];
            sumSq += rms[f] * rms[f];
        }
        var meanRms = sum / len;
        var stdRms = Math.Sqrt(Math.Max(0, sumSq / len - meanRms * meanRms));
        var stability = 1 - Math.Min(stdRms / Math.Max(meanRms, 1e-6), 1);
        // Length-biased score so one continuous run beats several short ones (fewer seams).
        var score = Math.Pow(endSec - startSec, 1.5) * (0.2 + stability);
        return new SpeechRun(startSec, endSec, meanRms, stdRms, stability, score);
    }
}
