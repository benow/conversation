using Benow.Conversation.Audio;
using Benow.Conversation.Config;
using Benow.Conversation.Engine;
using Benow.Conversation.Llm;
using Benow.Conversation.Providers;
using Benow.Conversation.Stt;
using Benow.Conversation.Tts;
using Benow.Conversation.Voice;
using Benow.Conversation.Lab;
using Benow.Conversation.Voices;
using Microsoft.Extensions.Logging;

// Conversation Lab — the phase-2 headless harness for Benow.Conversation.Core.
// Modes:
//   --devices                  list input devices (input-device menu parity)
//   --tts <text> [out.wav]     synthesize speech to a WAV (also makes test material)
//   --dictate <in.wav>         VAD + STT only: the dictation acceptance path
//   --converse <in.wav>        full turn: STT → LLM → (TTS) → report (add --play to hear it)
//   --text "<question>"        skip STT: direct LLM turn (fast engine check)
//   --mute                     with --converse: engine-level mute (no TTS provider call)

var argsList = args.ToList();
string? Arg(string name)
{
    var i = argsList.IndexOf(name);
    return i >= 0 && i + 1 < argsList.Count ? argsList[i + 1] : null;
}
bool Flag(string name) => argsList.Contains(name);

using var loggerFactory = LoggerFactory.Create(b =>
{
    b.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss.fff "; });
    b.SetMinimumLevel(LogLevel.Information);
});

var store = new ConfigStore(Arg("--config"));
var config = ConfigStore.FromEnvironment(store.Load());
var voice = new ConsoleSink();

HttpClient Http() => new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }, disposeHandler: true);

ITranscriptionService stt = new WhisperSttClient(
    loggerFactory.CreateLogger<WhisperSttClient>(),
    new SttOptions
    {
        ProviderTypeId = AiProviderInstances.ResolveProviderType(config, config.SttProvider)?.Id ?? "groq",
        ApiKey = AiProviderInstances.ResolveApiKey(config, AiProviderInstances.Find(config, config.SttProvider) ?? new AiProviderInstance { TypeId = config.SttProvider }),
        BaseUrl = ResolveBaseUrl(config, config.SttProvider),
        Model = config.SttModel,
        Language = config.SttLanguage
    },
    Http);

var chat = new ChatClient(
    loggerFactory.CreateLogger<ChatClient>(),
    new ChatOptions
    {
        ProviderTypeId = AiProviderInstances.ResolveProviderType(config, config.AiProvider)?.Id ?? "openrouter",
        ApiKey = ResolveKey(config, config.AiProvider),
        BaseUrl = ResolveBaseUrl(config, config.AiProvider),
        SpeakerModel = config.LlmModel,
        ExtractorModel = config.ExtractorModel,
        UseTools = config.UseTools,
        SystemPrompt = config.LlmSystemPrompt,
        ModelProvidersJson = config.ModelProviders,
        Timezone = TimeZoneInfo.Local.Id,
        LogConversationDebug = config.LogConversationDebug
    },
    Http,
    tools: null); // No tool registry in the lab — tools degrade to the honest "unavailable" note.

var ttsTypeId = AiProviderInstances.ResolveProviderType(config, config.TtsProvider)?.Id ?? config.TtsProvider;
ITtsService tts = ttsTypeId == "kokoro"
    ? new KokoroTtsClient(loggerFactory.CreateLogger<KokoroTtsClient>(),
        new KokoroOptions { ServerUrl = config.KokoroUrl, Voice = config.TtsVoice }, Http)
    : ttsTypeId == "replicate"
    ? new ReplicateTtsClient(loggerFactory.CreateLogger<ReplicateTtsClient>(),
        new TtsOptions
        {
            ProviderTypeId = "replicate",
            ApiKey = ResolveKey(config, config.TtsProvider),
            Model = config.TtsModel,
            Voice = config.TtsVoice,
            VoicesDirectory = string.IsNullOrWhiteSpace(config.VoicesDirectory) ? DefaultVoicesDir() : config.VoicesDirectory
        }, Http)
    : new OpenAiTtsClient(loggerFactory.CreateLogger<OpenAiTtsClient>(),
        new TtsOptions
        {
            ProviderTypeId = "openai-compatible",
            ApiKey = ResolveKey(config, config.TtsProvider),
            BaseUrl = ResolveBaseUrl(config, config.TtsProvider),
            Model = config.TtsModel,
            Voice = config.TtsVoice
        }, Http);

var pipeline = new PcmPlaybackPipeline(loggerFactory.CreateLogger<PcmPlaybackPipeline>(), new PcmPlaybackOptions
{
    SampleRate = 24000,
    Volume = config.PlaybackVolume,
    Device = string.IsNullOrWhiteSpace(config.OutputDevice) ? null : config.OutputDevice,
    FreshStartWarmupMs = Arg("--warmup") is { } warmArg && int.TryParse(warmArg, out var warmMs) ? warmMs : 500
});
var speech = new SpeechQueue(tts, new PcmPlaybackAudioOut(pipeline), loggerFactory.CreateLogger<SpeechQueue>());
await speech.StartAsync(CancellationToken.None);

await using var engine = new ConversationEngine(
    loggerFactory.CreateLogger<ConversationEngine>(), stt, chat, tts, speech,
    new EngineOptions
    {
        SpeakReplies = Flag("--play") || Flag("--bench"),
        FullAudioCorrection = !Flag("--no-correction"),
        PrewarmPlayback = !Flag("--no-prewarm"),
        Pacer = new TtsChunkPacerOptions
        {
            FirstMinChars = Arg("--first") is { } firstArg && int.TryParse(firstArg, out var firstChars) ? firstChars : 40,
            MaxChars = Arg("--para") is { } paraArg && int.TryParse(paraArg, out var paraChars) ? paraChars : 320,
            GrowthFactor = Arg("--growth") is { } growthArg && double.TryParse(growthArg, System.Globalization.CultureInfo.InvariantCulture, out var growth) ? growth : 1.4
        }
    },
    voice);

// ---- Modes ----

if (Flag("--devices"))
{
    var devices = await new AudioDeviceEnumerator(loggerFactory.CreateLogger<AudioDeviceEnumerator>()).ListInputsAsync(CancellationToken.None);
    Console.WriteLine("Input devices:");
    Console.WriteLine("  (system default)");
    foreach (var d in devices) Console.WriteLine($"  {d.Id}  —  {d.Name}");
    return 0;
}

var voicesDir = string.IsNullOrWhiteSpace(config.VoicesDirectory) ? DefaultVoicesDir() : config.VoicesDirectory;
var library = new VoiceLibrary(loggerFactory.CreateLogger<VoiceLibrary>(), voicesDir,
    recorder: new FfmpegAudioRecorder(loggerFactory.CreateLogger<FfmpegAudioRecorder>(),
        new CaptureOptions { Device = config.InputDevice }));

if (Flag("--voices"))
{
    var entries = library.List();
    Console.WriteLine($"Voice library: {voicesDir}  ({entries.Count} voice(s))");
    foreach (var e in entries)
        Console.WriteLine($"  {e.Name,-52} {e.DurationSec,6:F1}s  {e.SizeBytes / 1024,6} KB  {e.CreatedAt:yyyy-MM-dd HH:mm}");
    return 0;
}

if (Arg("--voice-import") is { } importPath)
{
    var importName = argsList.Count > argsList.IndexOf("--voice-import") + 2 ? argsList[argsList.IndexOf("--voice-import") + 2] : null;
    try
    {
        var (entry, analysis) = await library.ImportAsync(importPath, importName);
        Console.WriteLine($"imported: {entry.Name} ({entry.DurationSec:F1}s, from {analysis.DurationSec:F1}s source, {analysis.SpeechSec:F1}s speech)");
        foreach (var w in entry.Warnings) Console.WriteLine($"  WARNING: {w}");
        return 0;
    }
    catch (Exception ex) { Console.Error.WriteLine($"import failed: {ex.Message}"); return 1; }
}

if (Arg("--voice-import-dir") is { } importDir)
{
    // Bulk migration (e.g. importing the NASTV library): every audio file in the directory.
    var html = new[] { ".wav", ".mp3", ".m4a", ".ogg", ".flac", ".aac", ".opus", ".wma" };
    var files = Directory.GetFiles(importDir)
        .Where(f => html.Contains(Path.GetExtension(f).ToLowerInvariant()))
        .OrderBy(f => f)
        .ToList();
    Console.WriteLine($"[lab] bulk import: {files.Count} file(s) from {importDir} → {voicesDir}");
    var ok = 0; var failed = 0;
    foreach (var f in files)
    {
        try
        {
            var (entry, analysis) = await library.ImportAsync(f);
            var warn = entry.Warnings.Count > 0 ? $"  ⚠ {string.Join("; ", entry.Warnings)}" : "";
            Console.WriteLine($"  {entry.Name,-52} {entry.DurationSec,6:F1}s  (src {analysis.DurationSec:F1}s, speech {analysis.SpeechSec:F1}s){warn}");
            ok++;
        }
        catch (Exception ex) { Console.Error.WriteLine($"  FAILED {Path.GetFileName(f)}: {ex.Message}"); failed++; }
    }
    Console.WriteLine($"[lab] imported {ok}, failed {failed}");
    return failed == 0 ? 0 : 1;
}

if (Arg("--voice-record") is { } recordName)
{
    var seconds = argsList.Count > argsList.IndexOf("--voice-record") + 2 && double.TryParse(argsList[argsList.IndexOf("--voice-record") + 2], out var sec) ? sec : 10;
    try
    {
        var (entry, analysis) = await library.RecordAsync(recordName, TimeSpan.FromSeconds(seconds));
        Console.WriteLine($"recorded: {entry.Name} ({entry.DurationSec:F1}s, {analysis.SpeechSec:F1}s speech)");
        foreach (var w in entry.Warnings) Console.WriteLine($"  WARNING: {w}");
        return 0;
    }
    catch (Exception ex) { Console.Error.WriteLine($"record failed: {ex.Message}"); return 1; }
}

if (Arg("--voice-delete") is { } deleteName)
{
    return library.Delete(deleteName) ? 0 : 1;
}

if (Arg("--tts") is { } ttsText)
{
    var outPath = argsList.Count > argsList.IndexOf("--tts") + 2 ? argsList[argsList.IndexOf("--tts") + 2] : "/tmp/conversation-lab-tts.wav";
    if (Arg("--voice") is { } voiceOverride) Console.WriteLine($"[lab] using voice '{voiceOverride}'");
    var audio = await tts.SynthesizeAsync(ttsText, CancellationToken.None);
    if (audio == null) { Console.Error.WriteLine("synthesis failed"); return 1; }
    await using var wav = WavWrapper.Wrap(audio.Pcm, audio.SampleRate, 1, 16);
    await using var file = File.Create(outPath);
    await wav.CopyToAsync(file);
    Console.WriteLine($"wrote {outPath} ({audio.Pcm.Length} bytes PCM, {audio.AudioMs}ms audio)");
    return 0;
}

if (Arg("--dictate") is { } dictatePath)
{
    var dictated = await RunDictate(dictatePath);
    return dictated.Length > 0 ? 0 : 1;
}

if (Arg("--converse") is { } conversePath)
{
    engine.Muted = Flag("--mute");
    var transcript = await RunDictate(conversePath);
    if (string.IsNullOrWhiteSpace(transcript)) { Console.Error.WriteLine("no transcript — cannot converse"); return 1; }
    Console.WriteLine($"\n[lab] transcript: \"{transcript}\"");
    return await RunTurn(transcript);
}

if (Arg("--text") is { } text)
{
    engine.Muted = Flag("--mute");
    return await RunTurn(text);
}

if (Arg("--bench") is { } benchPath)
{
    return await Bench.RunAsync(new BenchOptions
    {
        InputWav = benchPath,
        Repeats = Arg("--repeat") is { } r && int.TryParse(r, out var rc) ? rc : 2,
        Label = Arg("--label"),
        JsonOut = Arg("--out"),
        Prewarm = !Flag("--no-prewarm"),
        WarmupMs = Arg("--warmup") is { } w2 && int.TryParse(w2, out var wm2) ? wm2 : 500,
        Correction = !Flag("--no-correction"),
        FirstMinChars = Arg("--first") is { } f2 && int.TryParse(f2, out var fm2) ? fm2 : 40,
        MaxChars = Arg("--para") is { } p2 && int.TryParse(p2, out var pm2) ? pm2 : 320,
        GrowthFactor = Arg("--growth") is { } g2 && double.TryParse(g2, System.Globalization.CultureInfo.InvariantCulture, out var gm2) ? gm2 : 1.4,
        Muted = Flag("--mute"),
        Speak = true
    }, engine, speech, loggerFactory);
}

Console.WriteLine("""
Conversation Lab — usage:
  --devices                 list input devices
  --tts <text> [out.wav]    synthesize to WAV
  --dictate <in.wav>        VAD + STT only
  --converse <in.wav>       STT → LLM → TTS (add --play to hear, --mute to skip TTS)
  --text "<question>"       direct LLM turn
  --bench <in.wav>          latency experiment: full turn through the real capture path
                            [--repeat n] [--no-prewarm] [--warmup ms] [--no-correction]
                            [--first chars] [--para chars] [--mute] [--out results.json]
  [--config path.json]
""");
return 0;

async Task<string> RunDictate(string wavPath)
{
    if (!File.Exists(wavPath)) { Console.Error.WriteLine($"file not found: {wavPath}"); return ""; }
    var wav = await File.ReadAllBytesAsync(wavPath);
    var decoded = WavDecoder.ParseWav(wav) ?? throw new InvalidOperationException("unparseable WAV");
    Console.WriteLine($"[lab] input {wavPath}: {decoded.SampleRate}Hz {decoded.Channels}ch {decoded.BitsPerSample}bit, {decoded.Pcm.Length / 2 / (double)decoded.SampleRate:F1}s");
    if (decoded.SampleRate != 16000 || decoded.Channels != 1)
        Console.WriteLine($"[lab] NOTE: pipeline expects 16kHz mono — feeding {decoded.SampleRate}Hz/{decoded.Channels}ch as-is (provider resamples)");

    engine.BeginSession();
    const int frameBytes = 640;
    var sw = System.Diagnostics.Stopwatch.StartNew();
    for (var off = 0; off + frameBytes <= decoded.Pcm.Length; off += frameBytes)
    {
        var frame = decoded.Pcm[off..(off + frameBytes)];
        await engine.PushFrameAsync(frame);
    }
    // Feed a tail of silence so a final breath-pause closes the last segment naturally.
    var silence = new byte[frameBytes];
    for (var i = 0; i < 30; i++) await engine.PushFrameAsync(silence);

    var transcript = await engine.EndSessionAsync();
    Console.WriteLine($"[lab] dictate: {sw.ElapsedMilliseconds}ms, transcript: \"{transcript}\"");
    return transcript;
}

async Task<int> RunTurn(string transcript)
{
    var result = await engine.ConverseAsync(transcript);
    if (result == null) { Console.Error.WriteLine("turn failed"); return 1; }
    Console.WriteLine($"\n[lab] reply: \"{result.Text}\"");
    Console.WriteLine($"[lab] turn {result.TotalMs}ms (extractor {result.ExtractorMs}ms, speaker {result.SpeakerMs}ms, tools={result.UsedTools}, muted={engine.Muted})");
    if (Flag("--play"))
    {
        // Give the queue a moment to drain the tail chunks.
        await Task.Delay(1500);
        while (speech.QueuedCount > 0) await Task.Delay(200);
        await Task.Delay(500);
    }
    return 0;
}

static string ResolveKey(ConversationConfig cfg, string? id)
{
    var instance = AiProviderInstances.Find(cfg, id);
    return instance == null ? "" : AiProviderInstances.ResolveApiKey(cfg, instance);
}

static string ResolveBaseUrl(ConversationConfig cfg, string? id)
{
    var instance = AiProviderInstances.Find(cfg, id);
    return instance == null ? "" : AiProviderInstances.ResolveBaseUrl(cfg, instance);
}

static string DefaultVoicesDir()
{
    var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "conversation", "voices");
    Directory.CreateDirectory(dir);
    return dir;
}

internal sealed class ConsoleSink : IConversationEventSink
{
    public void SessionStarted() => Console.WriteLine("[event] session started");
    public void SessionEnded(long ms) => Console.WriteLine($"[event] session ended ({ms}ms)");
    public void TranscriptPartial(string text) => Console.WriteLine($"[event] partial: {text}");
    public void TranscriptFinal(string text, bool corrected) => Console.WriteLine($"[event] final{(corrected ? " (corrected)" : "")}: {text}");
    public void LlmStarted() => Console.WriteLine("[event] llm started");
    public void LlmChunk(string text) => Console.Write(text);
    public void LlmFinal(string text, long ms) => Console.WriteLine($"\n[event] llm final ({ms}ms, {text.Length}c)");
    public void TtsStarted(string text) => Console.WriteLine($"[event] tts: {text[..Math.Min(60, text.Length)]}…");
    public void TtsDone(string text, long audioMs) => Console.WriteLine($"[event] tts done ({audioMs}ms)");
    public void Error(string stage, string message) => Console.Error.WriteLine($"[event] ERROR {stage}: {message}");
}
