using Microsoft.Extensions.Logging;

namespace Benow.Conversation.Voice;

/// <summary>
/// Energy-based Voice Activity Detector with auto-calibration.
/// Consumes PCM frames (16kHz, mono, 16-bit little-endian) and emits
/// speech segments separated by natural breath pauses.
/// Ported from NASTV nastv-player-core/Voice/VoiceVAD.cs (2026-09-17, phase 1).
/// </summary>
public sealed class VoiceVAD
{
    private const int FrameMs = 20;
    private const int FrameSamples = 16000 * FrameMs / 1000;   // 320 samples per frame
    private const int FrameBytes = FrameSamples * 2;            // 640 bytes per frame
    private const int InhaleGapMs = 200;         // 200ms silence to detect a pause (was 300ms)
    private const int MinSpeechBytes = 100 * 32;  // ~100ms minimum speech (drop shorter fragments, was 200ms)
    // Sentence-driven submission pacing (2026-08-19, Groq 429 collapse): the first segment
    // closes early to confirm the conversation is working, every subsequent segment needs a
    // longer minimum so Whisper gets more context (better recognition) and Groq sees ~1
    // request per sentence instead of a burst per 1.5s of speech. Segments still close at the
    // first sentence pause AFTER the minimum is reached; end-of-turn is always flushed by the
    // button release (VoiceSession.FlushOpen).
    private const int FirstSegmentMinBytes = 16000 * 2 * 5 / 2;   // 2.5s minimum for the first submission
    private const int LaterSegmentMinBytes = 16000 * 2 * 5;       // 5s minimum for every subsequent one
    private int _closedCount;
    private const int CalibrationFrames = 25;                   // 25 × 20ms = 500ms calibration window

    private readonly ILogger<VoiceVAD> _logger;
    private readonly List<byte> _currentSegment = new();
    private readonly Queue<byte[]> _closedSegments = new();

    private double _noiseFloor = 200;          // reasonable default before calibration
    private double _threshold;                 // = _noiseFloor * 4
    private int _calibrationFrameCount = 0;
    private double _calibrationSum = 0;
    private bool _calibrated = false;
    private VadState _state = VadState.Silence;
    private int _silenceMs = 0;
    private int _totalFrames = 0;

    private enum VadState { Silence, Speech }

    public VoiceVAD(ILogger<VoiceVAD> logger)
    {
        _logger = logger;
        _threshold = _noiseFloor * 4;
    }

    /// <summary>
    /// Feed one PCM frame (640 bytes). The caller must split incoming bytes
    /// into frame-sized chunks before calling this.
    /// </summary>
    public void Push(ReadOnlySpan<byte> frame)
    {
        if (frame.Length == 0) return;

        // During calibration (first 500ms), measure ambient noise and don't emit segments.
        if (!_calibrated)
        {
            _calibrationSum += ComputeRms(frame);
            _calibrationFrameCount++;
            _currentSegment.AddRange(frame.ToArray());

            if (_calibrationFrameCount >= CalibrationFrames)
            {
                var mean = _calibrationSum / _calibrationFrameCount;
                _noiseFloor = Math.Max(50, mean);   // floor at 50 to avoid hypersensitivity
                _threshold = Math.Min(_noiseFloor * 2.5, 6000);  // cap threshold, phone mics are noisy
                _calibrated = true;
                // Discard calibration frames — they're silence and shouldn't be included in speech segments.
                _currentSegment.Clear();
                _logger.LogInformation(
                    "[vad] Calibration complete. noiseFloor={NoiseFloor:F0}, threshold={Threshold:F0}",
                    _noiseFloor, _threshold);
            }
            return;
        }

        var rms = ComputeRms(frame);
        _totalFrames++;

        // Periodic debug-level telemetry: log RMS vs threshold every ~50 frames.
        // This is the single most valuable log for diagnosing "I spoke but got nothing"
        // — if PCM arrives but amplitude is zero (silent mic), this shows rms=0.
        if (_totalFrames % 50 == 0)
        {
            _logger.LogDebug("[vad] frame {Frame} rms={Rms:F0} threshold={Threshold:F0} state={State}",
                _totalFrames, rms, _threshold, _state);
        }

        // Adaptive noise floor: update during confirmed silence so the system tracks
        // changes in ambient noise (AC spinning up, fan, room change).
        if (_state == VadState.Silence)
        {
            _noiseFloor = (0.95 * _noiseFloor) + (0.05 * rms);
            _threshold = Math.Min(_noiseFloor * 2.5, 6000);
        }

        if (rms > _threshold)
        {
            // Speech detected.
            if (_state == VadState.Silence)
            {
                _state = VadState.Speech;
                _silenceMs = 0;
            }
            _currentSegment.AddRange(frame.ToArray());
        }
        else if (_state == VadState.Speech)
        {
            // In speech but currently silent — keep appending (preserves natural pacing)
            // and count silence toward the breath-pause threshold.
            _currentSegment.AddRange(frame.ToArray());
            _silenceMs += FrameMs;
            if (_silenceMs >= InhaleGapMs)
            {
                // Only close if we've accumulated enough speech. The minimum grows after
                // the first segment (sentence pacing, see constants above); short gaps
                // before the minimum are just pauses — keep collecting.
                if (_currentSegment.Count >= MinSegmentBytesFor(_closedCount))
                {
                    CloseSegment();
                    _state = VadState.Silence;
                }
                else
                {
                    // Not enough speech yet — keep the segment open and
                    // reset the silence timer so short gaps are absorbed.
                    _silenceMs = 0;
                }
            }
        }
    }

    /// <summary>
    /// Drain any closed segments (speech ended by a breath pause). Call this
    /// after every Push to collect segments ready for dispatch.
    /// </summary>
    public List<byte[]> DrainClosedSegments()
    {
        var result = new List<byte[]>(_closedSegments.Count);
        while (_closedSegments.TryDequeue(out var seg))
            result.Add(seg);
        return result;
    }

    /// <summary>
    /// Close the currently-open segment (if any) and return it. Call this
    /// when the recording session ends (finger-up).
    /// </summary>
    public byte[]? FlushOpen()
    {
        if (_currentSegment.Count == 0) return null;
        CloseSegment();
        return _closedSegments.Count > 0 ? _closedSegments.Dequeue() : null;
    }

    private static int MinSegmentBytesFor(int closedCount) =>
        closedCount == 0 ? FirstSegmentMinBytes : LaterSegmentMinBytes;

    private void CloseSegment()
    {
        if (_currentSegment.Count < MinSpeechBytes)
        {
            // Drop segments that are too short — Whisper hallucinates on near-empty input.
            _logger.LogDebug("[vad] Dropping short segment of {Bytes} bytes (below {Min})", _currentSegment.Count, MinSpeechBytes);
            _currentSegment.Clear();
            _silenceMs = 0;
            return;
        }

        var seg = _currentSegment.ToArray();
        _currentSegment.Clear();
        _silenceMs = 0;
        _closedSegments.Enqueue(seg);
        _closedCount++;
        _logger.LogInformation("[vad] Segment closed: {Bytes} bytes ({Ms}ms)",
            seg.Length, seg.Length * FrameMs / FrameBytes);
    }

    /// <summary>
    /// Compute the RMS energy of a PCM frame. PCM is signed 16-bit little-endian,
    /// so each sample is read as a short. RMS is the square root of the mean of squares.
    /// </summary>
    private static double ComputeRms(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 2) return 0;
        var sampleCount = frame.Length / 2;
        double sumSquares = 0;
        for (var i = 0; i < sampleCount; i++)
        {
            var sample = (short)(frame[i * 2] | (frame[i * 2 + 1] << 8));
            sumSquares += (double)sample * sample;
        }
        return Math.Sqrt(sumSquares / sampleCount);
    }
}
