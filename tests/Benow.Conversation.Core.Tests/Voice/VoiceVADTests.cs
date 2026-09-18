using Benow.Conversation.Voice;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Benow.Conversation.Core.Tests.Voice;

public class VoiceVADTests
{
    private static readonly byte[] SilenceFrame =
        GenerateFrame((short)20);  // very low amplitude

    private static readonly byte[] SpeechFrame =
        GenerateFrame((short)20000);  // high amplitude

    /// <summary>
    /// Helper: generate a 640-byte PCM frame at a given amplitude.
    /// Amplitude is the absolute short value written to each sample.
    /// </summary>
    private static byte[] GenerateFrame(short amplitude)
    {
        var bytes = new byte[640];
        for (var i = 0; i < bytes.Length; i += 2)
        {
            bytes[i] = (byte)(amplitude & 0xFF);
            bytes[i + 1] = (byte)((amplitude >> 8) & 0xFF);
        }
        return bytes;
    }

    [Fact]
    public void Push_SilenceDuringCalibration_DoesNotEmitSegment()
    {
        var vad = new VoiceVAD(NullLogger<VoiceVAD>.Instance);

        // Push 500ms of silence (25 frames × 20ms).
        for (var i = 0; i < 25; i++)
            vad.Push(SilenceFrame);

        Assert.Empty(vad.DrainClosedSegments());
    }

    [Fact]
    public void Push_SpeechFollowedByPause_EmitsOneSegment()
    {
        var vad = new VoiceVAD(NullLogger<VoiceVAD>.Instance);

        // Calibrate on silence (25 frames).
        for (var i = 0; i < 25; i++)
            vad.Push(SilenceFrame);
        vad.DrainClosedSegments();  // clear any stragglers

        // Speak: 150 frames of speech (3000ms) — crosses the FIRST segment minimum (2.5s).
        for (var i = 0; i < 150; i++)
            vad.Push(SpeechFrame);

        // Breath: 15 frames of silence (300ms = inhale gap).
        for (var i = 0; i < 15; i++)
            vad.Push(SilenceFrame);

        var segments = vad.DrainClosedSegments();
        Assert.Single(segments);
        // Segment should contain speech + trailing silence (150 speech + ~10 silence frames).
        Assert.True(segments[0].Length > 640 * 150);  // at least the speech frames
        Assert.True(segments[0].Length < 640 * 170);  // but not absurdly long
    }

    [Fact]
    public void Push_TwoSecondBurstWithPause_DoesNotCloseFirstSegment()
    {
        // Sentence pacing: the FIRST segment needs 2.5s of speech before it closes at a
        // pause. A 2s burst + pause is a mid-sentence breath — keep collecting.
        var vad = new VoiceVAD(NullLogger<VoiceVAD>.Instance);

        for (var i = 0; i < 25; i++) vad.Push(SilenceFrame);
        vad.DrainClosedSegments();

        for (var i = 0; i < 100; i++) vad.Push(SpeechFrame);   // 2000ms — under 2.5s
        for (var i = 0; i < 15; i++) vad.Push(SilenceFrame);   // pause

        Assert.Empty(vad.DrainClosedSegments());
        // The open segment survives the pause (still collecting) — FlushOpen returns it.
        Assert.NotNull(vad.FlushOpen());
    }

    [Fact]
    public void Push_FirstSegmentClosesAtTwoPointFiveSeconds_LaterNeedsFiveSeconds()
    {
        var vad = new VoiceVAD(NullLogger<VoiceVAD>.Instance);

        for (var i = 0; i < 25; i++) vad.Push(SilenceFrame);
        vad.DrainClosedSegments();

        // Segment 1: 140 frames (2800ms ≥ 2.5s first minimum) + pause → closes.
        for (var i = 0; i < 140; i++) vad.Push(SpeechFrame);
        for (var i = 0; i < 15; i++) vad.Push(SilenceFrame);
        Assert.Single(vad.DrainClosedSegments());

        // Segment 2: 200 frames (4000ms < 5s later minimum) + pause → NOT closed yet.
        for (var i = 0; i < 200; i++) vad.Push(SpeechFrame);
        for (var i = 0; i < 15; i++) vad.Push(SilenceFrame);
        Assert.Empty(vad.DrainClosedSegments());

        // Keep speaking past 5s → closes.
        for (var i = 0; i < 60; i++) vad.Push(SpeechFrame);     // +1200ms → 5200ms total
        for (var i = 0; i < 15; i++) vad.Push(SilenceFrame);
        Assert.Single(vad.DrainClosedSegments());
    }

    [Fact]
    public void Push_ShortBurstBetweenPauses_DropsShortSegment()
    {
        var vad = new VoiceVAD(NullLogger<VoiceVAD>.Instance);

        // Calibrate on silence.
        for (var i = 0; i < 25; i++)
            vad.Push(SilenceFrame);

        // Very tiny burst of speech: 2 frames (40ms).
        vad.Push(SpeechFrame);
        vad.Push(SpeechFrame);

        // Immediately stop — no trailing silence. Flush directly.
        var final = vad.FlushOpen();

        // The 2-frame segment (1280 bytes) is well below MinSpeechBytes (6400),
        // so it should be dropped.
        Assert.Null(final);
    }

    [Fact]
    public void Push_MultipleSpeechPauses_EmitsMultipleSegments()
    {
        var vad = new VoiceVAD(NullLogger<VoiceVAD>.Instance);

        // Calibrate on silence.
        for (var i = 0; i < 25; i++)
            vad.Push(SilenceFrame);

        // Segment 1: 140 frames speech (2.8s, crosses the 2.5s first minimum) + pause.
        for (var i = 0; i < 140; i++)
            vad.Push(SpeechFrame);
        for (var i = 0; i < 15; i++)
            vad.Push(SilenceFrame);

        // Segment 2: 260 frames speech (5.2s, crosses the 5s later minimum) + pause.
        for (var i = 0; i < 260; i++)
            vad.Push(SpeechFrame);
        for (var i = 0; i < 15; i++)
            vad.Push(SilenceFrame);

        var segments = vad.DrainClosedSegments();
        Assert.Equal(2, segments.Count);
    }

    [Fact]
    public void FlushOpen_AfterSpeech_ReturnsRemainingSegment()
    {
        var vad = new VoiceVAD(NullLogger<VoiceVAD>.Instance);

        // Calibrate on silence.
        for (var i = 0; i < 25; i++)
            vad.Push(SilenceFrame);

        // Speak but don't pause (user is still talking when finger lifts).
        for (var i = 0; i < 30; i++)
            vad.Push(SpeechFrame);

        Assert.Empty(vad.DrainClosedSegments());  // nothing closed yet
        var final = vad.FlushOpen();
        Assert.NotNull(final);
        Assert.True(final!.Length > 640 * 25);
    }

    [Fact]
    public void Push_AdaptiveNoiseFloor_TracksUpwardDuringSilence()
    {
        // After calibration, sustained louder silence should raise the noise floor,
        // preventing false speech detections from background noise.
        var vad = new VoiceVAD(NullLogger<VoiceVAD>.Instance);

        // Calibrate on near-silence.
        for (var i = 0; i < 25; i++)
            vad.Push(SilenceFrame);

        // Now feed louder "silence" (e.g. AC kicked in). Should NOT trigger speech.
        var louderSilence = GenerateFrame((short)100);
        for (var i = 0; i < 50; i++)
            vad.Push(louderSilence);

        Assert.Empty(vad.DrainClosedSegments());
    }
}
