using System.Diagnostics;
using System.Text.Json;
using Benow.Conversation.Audio;
using Benow.Conversation.Config;
using Benow.Conversation.Engine;
using Microsoft.Extensions.Logging;

namespace Benow.Conversation.Lab;

/// <summary>Knobs the bench can vary, so every claim about latency is measured, not assumed.</summary>
internal sealed record BenchOptions
{
    public required string InputWav { get; init; }
    public int Repeats { get; init; } = 2;
    public string? Label { get; init; }
    public string? JsonOut { get; init; }
    public bool Prewarm { get; init; } = true;
    public int WarmupMs { get; init; } = 500;
    public bool Correction { get; init; } = true;
    public int FirstMinChars { get; init; } = 40;
    public int MaxChars { get; init; } = 320;
    public double GrowthFactor { get; init; } = 1.4;
    public bool Muted { get; init; }
    public bool Speak { get; init; } = true;

    public string Describe() =>
        $"prewarm={(Prewarm ? "on" : "off")} warmup={WarmupMs}ms correction={(Correction ? "on" : "off")} " +
        $"first={FirstMinChars}c cap={MaxChars}c growth={GrowthFactor} speak={(Speak ? "on" : "off")}";
}

/// <summary>
/// The latency experiment: one full turn through the REAL capture path (replayed WAV paced at
/// native rate) → VAD → streaming STT → LLM → paced TTS → playback, instrumented with
/// <see cref="TurnTimeline"/>.
///
/// Metrics answer the three questions that matter for how a conversation FEELS:
///   stop → transcript    (how long after you stop talking the text is ready)
///   stop → first token   (how long until the reply starts being produced)
///   stop → first audio   (how long until you HEAR something)
///   gaps                 (does the audio stream or stutter between chunks)
/// </summary>
internal static class Bench
{
    public static async Task<int> RunAsync(
        BenchOptions options, ConversationEngine engine, SpeechQueue speech,
        ILoggerFactory loggerFactory, CancellationToken ct = default)
    {
        if (!File.Exists(options.InputWav))
        {
            Console.Error.WriteLine($"bench: input not found: {options.InputWav}");
            return 1;
        }

        var capture = new FfmpegStreamingCapture(
            loggerFactory.CreateLogger<FfmpegStreamingCapture>(),
            new CaptureOptions { InputFile = options.InputWav });

        var runs = new List<RunResult>();
        Console.WriteLine($"[bench] {options.Describe()}  repeats={options.Repeats}");
        Console.WriteLine($"[bench] input {options.InputWav} ({new FileInfo(options.InputWav).Length / 1024} KB)");

        for (var i = 0; i < options.Repeats; i++)
        {
            var result = await RunOnceAsync(options, engine, speech, capture, i + 1, loggerFactory, ct);
            if (result == null) return 1;
            runs.Add(result);
            PrintRun(result);
            if (i + 1 < options.Repeats) await Task.Delay(600, ct);   // let the provider pacing gates settle
        }

        PrintSummary(options, runs);

        if (options.JsonOut != null)
        {
            await File.WriteAllTextAsync(options.JsonOut,
                JsonSerializer.Serialize(new { options = options.Describe(), runs }, JsonOpts) + "\n", ct);
            Console.WriteLine($"[bench] wrote {options.JsonOut}");
        }
        return 0;
    }

    private static async Task<RunResult?> RunOnceAsync(
        BenchOptions options, ConversationEngine engine, SpeechQueue speech,
        FfmpegStreamingCapture capture, int index, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var timeline = new TurnTimeline();
        engine.Timeline = timeline;
        engine.Muted = options.Muted;

        // Per-chunk playback timestamps: pipe time + audio duration tells us whether the next
        // chunk arrived before the previous one finished (a real audible gap, not a guess).
        var pipes = new List<(string Text, long Ms, long AudioMs)>();
        var started = Stopwatch.StartNew();
        void OnPlaybackStarted(string text, long audioMs)
        {
            lock (pipes) pipes.Add((text, started.ElapsedMilliseconds, audioMs));
        }
        speech.PlaybackStarted += OnPlaybackStarted;

        try
        {
            using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            engine.BeginSession();
            var captureTask = capture.CaptureAsync(frame => engine.PushFrameAsync(frame, sessionCts.Token), sessionCts.Token);

            // The replayed file ends when the capture process exits — that IS the "user stopped
            // talking" moment, so the stop timestamp is taken the instant capture returns.
            await captureTask;
            var stopMs = started.ElapsedMilliseconds;
            timeline.MarkAlways(TurnTimeline.Milestones.CaptureStop);

            var transcript = await engine.EndSessionAsync(ct);
            if (string.IsNullOrWhiteSpace(transcript))
            {
                Console.Error.WriteLine($"[bench] run {index}: no transcript produced (nothing heard) — aborting");
                return null;
            }

            var result = await engine.ConverseAsync(transcript, ct);
            if (result == null)
            {
                Console.Error.WriteLine($"[bench] run {index}: LLM turn failed");
                return null;
            }

            // Wait for the speaker to go IDLE and then for the last chunk's audio to finish.
            // Queue depth alone is not a completion signal: chunks are enqueued as the LLM
            // streams, so the queue passes through empty mid-reply (this made runs end early
            // and report "1 chunk" for a three-chunk reply).
            var idleSince = (long?)null;
            var drainDeadline = DateTime.UtcNow.AddSeconds(180);
            while (DateTime.UtcNow < drainDeadline)
            {
                if (speech.IsIdle)
                {
                    idleSince ??= started.ElapsedMilliseconds;
                    break;
                }
                idleSince = null;
                await Task.Delay(150, ct);
            }
            long playbackEnd;
            lock (pipes) playbackEnd = pipes.Count == 0 ? 0 : pipes[^1].Ms + pipes[^1].AudioMs;
            if (playbackEnd > started.ElapsedMilliseconds)
                await Task.Delay((int)(playbackEnd - started.ElapsedMilliseconds) + 500, ct);

            var chunks = new List<(string Text, long Ms, long AudioMs)>();
            lock (pipes) chunks.AddRange(pipes);

            return new RunResult
            {
                Index = index,
                Transcript = transcript,
                Reply = result.Text,
                ReplyChars = result.Text.Length,
                Chunks = chunks.Count,
                StopMs = stopMs,
                FirstSttMs = timeline[TurnTimeline.Milestones.FirstSttDone],
                FinalTranscriptMs = timeline[TurnTimeline.Milestones.FinalTranscript],
                TtftMs = timeline[TurnTimeline.Milestones.LlmFirstToken],
                FirstAudioMs = timeline[TurnTimeline.Milestones.FirstAudioPiped],
                LastAudioMs = timeline[TurnTimeline.Milestones.LastAudioPiped],
                LlmMs = result.TotalMs,
                Gaps = ComputeGaps(chunks),
                ChunkMs = chunks.Select(c => c.Ms).ToList()
            };
        }
        finally
        {
            speech.PlaybackStarted -= OnPlaybackStarted;
        }
    }

    /// <summary>
    /// Audible gaps between chunks: how long after chunk N's audio should have finished did
    /// chunk N+1 start? Positive = silence (a stutter); ≤0 = seamless.
    /// </summary>
    private static List<long> ComputeGaps(List<(string Text, long Ms, long AudioMs)> chunks)
    {
        var gaps = new List<long>();
        for (var i = 1; i < chunks.Count; i++)
        {
            var prevEnd = chunks[i - 1].Ms + chunks[i - 1].AudioMs;
            if (chunks[i - 1].AudioMs > 0) gaps.Add(chunks[i].Ms - prevEnd);
        }
        return gaps;
    }

    private static void PrintRun(RunResult r)
    {
        Console.WriteLine($"\n[bench] run {r.Index}");
        Console.WriteLine($"  transcript   : \"{Clip(r.Transcript, 90)}\"");
        Console.WriteLine($"  reply        : \"{Clip(r.Reply, 90)}\"");
        Console.WriteLine($"  stop→final   : {Diff(r.FinalTranscriptMs, r.StopMs),6} ms   (correction included)");
        Console.WriteLine($"  →first token : {Diff(r.TtftMs, r.StopMs),6} ms");
        Console.WriteLine($"  →first audio : {Diff(r.FirstAudioMs, r.StopMs),6} ms   ← what the user feels");
        Console.WriteLine($"  →last audio  : {Diff(r.LastAudioMs, r.StopMs),6} ms");
        Console.WriteLine($"  chunks       : {r.Chunks} ({r.ReplyChars} chars), llm {r.LlmMs} ms");
        if (r.Gaps.Count > 0)
        {
            var worst = r.Gaps.Max();
            var seamless = r.Gaps.Count(g => g <= 0);
            Console.WriteLine($"  chunk gaps   : {seamless}/{r.Gaps.Count} seamless, worst {worst} ms " +
                              $"[{string.Join(", ", r.Gaps.Select(g => g.ToString()))}]");
        }
    }

    private static void PrintSummary(BenchOptions options, List<RunResult> runs)
    {
        Console.WriteLine($"\n[bench] summary — {options.Describe()} (n={runs.Count})");
        Console.WriteLine($"  stop→final   : median {Median(runs.Select(r => Diff(r.FinalTranscriptMs, r.StopMs))) ,6} ms");
        Console.WriteLine($"  stop→1st tok : median {Median(runs.Select(r => Diff(r.TtftMs, r.StopMs))),6} ms");
        Console.WriteLine($"  stop→1st aud : median {Median(runs.Select(r => Diff(r.FirstAudioMs, r.StopMs))),6} ms");
        Console.WriteLine($"  stop→last aud: median {Median(runs.Select(r => Diff(r.LastAudioMs, r.StopMs))),6} ms");
        Console.WriteLine($"  chunks       : median {Median(runs.Select(r => (long)r.Chunks))}");
    }

    private static long Diff(long? later, long earlier) =>
        later.HasValue ? later.Value - earlier : -1;

    private static long Median(IEnumerable<long> values)
    {
        var list = values.Where(v => v >= 0).OrderBy(v => v).ToList();
        if (list.Count == 0) return -1;
        return list.Count % 2 == 1 ? list[list.Count / 2] : (list[list.Count / 2 - 1] + list[list.Count / 2]) / 2;
    }

    private static string Clip(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";

    private static readonly JsonSerializerOptions JsonOpts = new(ConversationJson.Options) { WriteIndented = true };

    internal sealed class RunResult
    {
        public int Index { get; set; }
        public string Transcript { get; set; } = "";
        public string Reply { get; set; } = "";
        public int ReplyChars { get; set; }
        public int Chunks { get; set; }
        public long StopMs { get; set; }
        public long? FirstSttMs { get; set; }
        public long? FinalTranscriptMs { get; set; }
        public long? TtftMs { get; set; }
        public long? FirstAudioMs { get; set; }
        public long? LastAudioMs { get; set; }
        public long LlmMs { get; set; }
        public List<long> Gaps { get; set; } = new();
        public List<long> ChunkMs { get; set; } = new();
    }
}
