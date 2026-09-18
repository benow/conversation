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
    // Context kept before a detected onset and prepended to the segment that follows. Without
    // this the calibration window (first 500ms) was DISCARDED and the ~1 frame before each onset
    // crossing was dropped, so "What is a good way…" arrived at Whisper as "good way…" — the
    // opening words of every turn were silently lost whenever speech started immediately
    // (root-caused 2026-09-18 with the Lab's --bench, which caught the missing words).
    private const int PreRollFrames = CalibrationFrames;        // 500ms, matches the calibration window

    private readonly ILogger<VoiceVAD> _logger;
    private readonly List<byte> _currentSegment = new();
    private readonly Queue<byte[]> _closedSegments = new();
    private readonly Queue<byte[]> _preRoll = new();
    /// <summary>RMS of each calibration frame — used to count speech that happened during calibration.</summary>
    private readonly List<double> _calibrationRms = new();
    /// <summary>
    /// Bytes of ACTUAL speech in the open segment. The close/drop thresholds compare against this
    /// rather than the segment length: the pre-roll adds context frames, and counting them as
    /// speech made 2.3s bursts close as if they were 2.6s and let sub-100ms clicks survive the
    /// short-segment guard.
    /// </summary>
    private int _speechBytes;

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

        // During calibration (first 500ms), measure ambient noise and don't emit segments yet.
        // The frames go into the pre-roll rather than being discarded: if the user started
        // talking immediately, that audio IS the beginning of the turn.
        if (!_calibrated)
        {
            var rmsCal = ComputeRms(frame);
            _calibrationSum += rmsCal;
            _calibrationRms.Add(rmsCal);
            _calibrationFrameCount++;
            PushPreRoll(frame);

            if (_calibrationFrameCount >= CalibrationFrames)
            {
                var mean = _calibrationSum / _calibrationFrameCount;
                _noiseFloor = Math.Max(50, mean);   // floor at 50 to avoid hypersensitivity
                _threshold = Math.Min(_noiseFloor * 2.5, 6000);  // cap threshold, phone mics are noisy
                _calibrated = true;
                // Speech that happened DURING calibration still counts toward the segment minimum
                // (the user may have started talking the instant they pressed the key).
                foreach (var calRms in _calibrationRms)
                    if (calRms > _threshold) _speechBytes += FrameBytes;
                _logger.LogInformation(
                    "[vad] Calibration complete. noiseFloor={NoiseFloor:F0}, threshold={Threshold:F0}, speechDuringCalibration={SpeechMs}ms",
                    _noiseFloor, _threshold, _speechBytes / 32);
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
                // Start the segment with the audio that led up to the onset so the first
                // phoneme is not clipped.
                while (_preRoll.Count > 0) _currentSegment.AddRange(_preRoll.Dequeue());
            }
            _currentSegment.AddRange(frame.ToArray());
            _speechBytes += FrameBytes;
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
                if (_speechBytes >= MinSegmentBytesFor(_closedCount))
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
        else
        {
            // Confirmed silence — keep it as pre-roll context for the next onset.
            PushPreRoll(frame);
        }
    }

    private void PushPreRoll(ReadOnlySpan<byte> frame)
    {
        _preRoll.Enqueue(frame.ToArray());
        while (_preRoll.Count > PreRollFrames) _preRoll.Dequeue();
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

    /// <summary>
    /// Clears per-turn state (segments, pre-roll, speech counter) while KEEPING the learned noise
    /// floor — re-calibrating on every turn would spend the opening 500ms re-learning a room that
    /// has not changed. Called at the start of each listening session.
    /// </summary>
    public void Reset()
    {
        _currentSegment.Clear();
        _closedSegments.Clear();
        _preRoll.Clear();
        _speechBytes = 0;
        _closedCount = 0;
        _silenceMs = 0;
        _state = VadState.Silence;
    }

    private void CloseSegment()
    {
        if (_speechBytes < MinSpeechBytes)
        {
            // Drop segments that are too short — Whisper hallucinates on near-empty input.
            _logger.LogDebug("[vad] Dropping short segment ({SpeechBytes} bytes of speech, below {Min})", _speechBytes, MinSpeechBytes);
            _currentSegment.Clear();
            _silenceMs = 0;
            _speechBytes = 0;
            return;
        }

        var seg = _currentSegment.ToArray();
        var speechMs = _speechBytes * FrameMs / FrameBytes;
        _currentSegment.Clear();
        _silenceMs = 0;
        _speechBytes = 0;
        _closedSegments.Enqueue(seg);
        _closedCount++;
        _logger.LogInformation("[vad] Segment closed: {Bytes} bytes ({Ms}ms total, {SpeechMs}ms speech)",
            seg.Length, seg.Length / (FrameBytes / FrameMs), speechMs);
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
