using System.Diagnostics;

namespace Benow.Conversation.Engine;

/// <summary>
/// Per-turn latency timeline (2026-09-18, phase-3 optimization work). Records the moment each
/// pipeline milestone happens relative to a session/turn start, so "where did the delay go?"
/// is answered by numbers in the log instead of by feel. One instance per turn; thread-safe
/// marks (the LLM callback and the speech queue run on different threads).
/// </summary>
public sealed class TurnTimeline
{
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private readonly Dictionary<string, long> _marks = new();
    private readonly object _lock = new();

    /// <summary>Marks a milestone (first occurrence wins — "first token", "first chunk"…).</summary>
    public void Mark(string name)
    {
        lock (_lock)
        {
            if (!_marks.ContainsKey(name)) _marks[name] = _sw.ElapsedMilliseconds;
        }
    }

    /// <summary>Marks and overwrites (counters like "last chunk").</summary>
    public void MarkAlways(string name)
    {
        lock (_lock) { _marks[name] = _sw.ElapsedMilliseconds; }
    }

    public long ElapsedMs => _sw.ElapsedMilliseconds;

    public long? this[string name]
    {
        get { lock (_lock) return _marks.TryGetValue(name, out var v) ? v : null; }
    }

    /// <summary>Ordered "milestone=ms" summary for logs.</summary>
    public string Summary()
    {
        lock (_lock)
        {
            return string.Join(" ", _marks.OrderBy(kv => kv.Value).Select(kv => $"{kv.Key}={kv.Value}ms"));
        }
    }

    /// <summary>Deltas between successive milestones (the interesting numbers).</summary>
    public string Deltas()
    {
        lock (_lock)
        {
            var ordered = _marks.OrderBy(kv => kv.Value).ToList();
            var parts = new List<string>();
            for (var i = 1; i < ordered.Count; i++)
                parts.Add($"{ordered[i].Key}−{ordered[i - 1].Key}={ordered[i].Value - ordered[i - 1].Value}ms");
            return string.Join(" ", parts);
        }
    }

    /// <summary>The keys the pipeline marks (kept in one place so bench output is stable).</summary>
    public static class Milestones
    {
        public const string SessionStart = "session_start";
        public const string CaptureStart = "capture_start";
        public const string FirstFrame = "first_frame";
        public const string FirstSegmentClosed = "first_segment";      // VAD closed segment 1
        public const string FirstSttStart = "first_stt_start";
        public const string FirstSttDone = "first_stt_done";
        public const string CaptureStop = "capture_stop";              // button release
        public const string FinalTranscript = "final_transcript";      // correction done, text ready
        public const string LlmStart = "llm_start";
        public const string LlmFirstToken = "llm_first_token";
        public const string LlmDone = "llm_done";
        public const string FirstTtsEnqueue = "first_tts_enqueue";
        public const string FirstTtsSynthDone = "first_tts_synth_done";
        public const string FirstAudioPiped = "first_audio_piped";     // bytes handed to ffplay
        public const string LastAudioPiped = "last_audio_piped";
    }
}
