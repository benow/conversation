using Benow.Conversation.Voices;
using Xunit;

namespace Benow.Conversation.Core.Tests.Voices;

/// <summary>
/// Reference-voice analysis (port of NASTV's client-side voiceReference.ts): speech-run
/// detection over 20ms RMS frames with a percentile noise floor, and best-segment selection.
/// </summary>
public class VoiceReferenceTests
{
    private const int Rate = 16000;

    private static float[] Silence(double seconds) => new float[(int)(seconds * Rate)];

    private static float[] Tone(double seconds, double amplitude)
    {
        var n = (int)(seconds * Rate);
        var buf = new float[n];
        // Alternating ±amplitude at 200 Hz — a stable, speech-like RMS level.
        for (var i = 0; i < n; i++) buf[i] = (float)(Math.Sin(2 * Math.PI * 200 * i / Rate) * amplitude);
        return buf;
    }

    private static float[] Concat(params float[][] parts)
    {
        var total = parts.Sum(p => p.Length);
        var outBuf = new float[total];
        var off = 0;
        foreach (var p in parts)
        {
            Array.Copy(p, 0, outBuf, off, p.Length);
            off += p.Length;
        }
        return outBuf;
    }

    [Fact]
    public void Analyze_DetectsSpeechRunBetweenSilences()
    {
        var samples = Concat(Silence(1.0), Tone(3.0, 0.3), Silence(1.0));
        var analysis = VoiceReference.Analyze(samples, Rate);

        Assert.Single(analysis.Runs);
        var run = analysis.Runs[0];
        Assert.InRange(run.StartSec, 0.9, 1.2);
        Assert.InRange(run.EndSec, 3.9, 4.2);
        Assert.InRange(analysis.SpeechSec, 2.8, 3.3);
    }

    [Fact]
    public void Analyze_AllSilence_NoRuns_WithWarning()
    {
        var analysis = VoiceReference.Analyze(Silence(5), Rate);
        Assert.Empty(analysis.Runs);
        Assert.Contains(analysis.Warnings, w => w.Contains("No clear speech"));
    }

    [Fact]
    public void Analyze_ShortSpeech_WarnsAboutPoorCloning()
    {
        var analysis = VoiceReference.Analyze(Concat(Silence(0.5), Tone(2.0, 0.3), Silence(0.5)), Rate);
        Assert.NotEmpty(analysis.Runs);
        Assert.Contains(analysis.Warnings, w => w.Contains("clone poorly"));
    }

    [Fact]
    public void Analyze_MultipleRuns_DetectedSeparately()
    {
        var samples = Concat(Silence(0.5), Tone(2.0, 0.3), Silence(1.0), Tone(2.0, 0.3), Silence(0.5));
        var analysis = VoiceReference.Analyze(samples, Rate);
        Assert.Equal(2, analysis.Runs.Count);
    }

    [Fact]
    public void SelectSegment_LongSingleRun_TakesTargetWindow()
    {
        var analysis = VoiceReference.Analyze(Concat(Silence(0.5), Tone(12.0, 0.3), Silence(0.5)), Rate);
        var selected = VoiceReference.SelectSegment(analysis, targetSec: 10);

        Assert.Single(selected.Ranges);
        Assert.Equal(10, selected.DurationSec, 1);
        // The window starts inside the speech run, not in the leading silence.
        Assert.True(selected.Ranges[0].StartSec >= 0.4);
    }

    [Fact]
    public void SelectSegment_ShortSpeech_ConcatenatesRuns_AndWarns()
    {
        var samples = Concat(Silence(0.4), Tone(2.0, 0.3), Silence(0.8), Tone(2.0, 0.3), Silence(0.4));
        var analysis = VoiceReference.Analyze(samples, Rate);
        var selected = VoiceReference.SelectSegment(analysis, targetSec: 10);

        Assert.Equal(2, selected.Ranges.Count);
        Assert.True(selected.DurationSec < 6);
        Assert.Contains(selected.Warnings, w => w.Contains("under 6s") || w.Contains("Only"));
    }

    [Fact]
    public void SelectSegment_NoSpeech_FallsBackToFileStart()
    {
        var analysis = VoiceReference.Analyze(Silence(20), Rate);
        var selected = VoiceReference.SelectSegment(analysis, targetSec: 8);
        Assert.Single(selected.Ranges);
        Assert.Equal(8, selected.DurationSec, 1);
        Assert.Contains(selected.Warnings, w => w.Contains("using the start"));
    }

    [Fact]
    public void SanitizeName_StripsPathCharacters()
    {
        Assert.Equal("emma_stone.wav", VoiceLibrary.SanitizeName("emma/stone") + ".wav");
        Assert.Equal("voice", VoiceLibrary.SanitizeName("   "));
        Assert.DoesNotContain("/", VoiceLibrary.SanitizeName("a/b\\c"));
    }
}
