using Benow.Conversation.Audio;
using Benow.Conversation.Config;
using Benow.Conversation.Dictation;
using Benow.Conversation.Engine;
using Benow.Conversation.Input;
using Benow.Conversation.Llm;
using Benow.Conversation.Providers;
using Benow.Conversation.Stt;
using Benow.Conversation.Tts;
using Benow.Conversation.Voice;
using Benow.Conversation.Voices;
using Microsoft.Extensions.Logging;

namespace Benow.Conversation.Composition;

/// <summary>
/// A wired conversation stack: engine plus everything it owns. Disposing tears down the speech
/// queue, the playback process (marker-scoped) and the HTTP clients.
/// </summary>
public sealed class ConversationRuntime : IAsyncDisposable
{
    internal ConversationRuntime(ConversationConfig config, ConversationEngine engine, SpeechQueue speech,
        PcmPlaybackPipeline pipeline, VoiceLibrary voices, AudioDeviceEnumerator devices,
        VoiceSession session, DictationService dictation)
    {
        Config = config;
        Engine = engine;
        Speech = speech;
        Pipeline = pipeline;
        Voices = voices;
        Devices = devices;
        Session = session;
        Dictation = dictation;
    }

    public ConversationConfig Config { get; }
    public ConversationEngine Engine { get; }
    public SpeechQueue Speech { get; }
    public PcmPlaybackPipeline Pipeline { get; }
    public VoiceLibrary Voices { get; }
    public AudioDeviceEnumerator Devices { get; }
    /// <summary>Push-to-talk capture for Converse (caller decides what to do with the text).</summary>
    public VoiceSession Session { get; }
    /// <summary>Push-to-talk capture + paste into the focused app (Speak).</summary>
    public DictationService Dictation { get; }

    public async ValueTask DisposeAsync()
    {
        await Engine.DisposeAsync();
        await Pipeline.DisposeAsync();
    }
}

/// <summary>
/// Single composition root for the conversation stack (plan §4.1). Both hosts use it: the Lab
/// harness and the desktop app — so a change in provider wiring lands in one place. NASTV's
/// phase-4 adoption swaps this for its DI container but keeps the same service graph.
/// </summary>
public static class ConversationFactory
{
    /// <summary>Where voice references live by default (never in git — see AGENTS.md).</summary>
    public static string DefaultVoicesDirectory()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "conversation", "voices");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static HttpClient NewHttpClient() =>
        new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }, disposeHandler: true);

    public static async Task<ConversationRuntime> CreateAsync(
        ILoggerFactory loggerFactory,
        ConversationConfig config,
        EngineOptions? engineOptions = null,
        IConversationEventSink? sink = null,
        CancellationToken ct = default)
    {
        var log = loggerFactory.CreateLogger("ConversationFactory");
        var voicesDir = string.IsNullOrWhiteSpace(config.VoicesDirectory) ? DefaultVoicesDirectory() : config.VoicesDirectory;
        Directory.CreateDirectory(voicesDir);
        var eventSink = sink ?? NullEventSink.Instance;

        // ---- STT ----
        var sttInstance = AiProviderInstances.Find(config, config.SttProvider);
        var stt = new WhisperSttClient(
            loggerFactory.CreateLogger<WhisperSttClient>(),
            new SttOptions
            {
                ProviderTypeId = AiProviderInstances.ResolveProviderType(config, config.SttProvider)?.Id ?? "groq",
                ApiKey = sttInstance == null ? "" : AiProviderInstances.ResolveApiKey(config, sttInstance),
                BaseUrl = sttInstance == null ? "" : AiProviderInstances.ResolveBaseUrl(config, sttInstance),
                Model = config.SttModel,
                Language = config.SttLanguage
            },
            NewHttpClient);

        // ---- Chat (extractor + speaker) ----
        var chatInstance = AiProviderInstances.Find(config, config.AiProvider);
        var chat = new ChatClient(
            loggerFactory.CreateLogger<ChatClient>(),
            new ChatOptions
            {
                ProviderTypeId = AiProviderInstances.ResolveProviderType(config, config.AiProvider)?.Id ?? "openrouter",
                ApiKey = chatInstance == null ? "" : AiProviderInstances.ResolveApiKey(config, chatInstance),
                BaseUrl = chatInstance == null ? "" : AiProviderInstances.ResolveBaseUrl(config, chatInstance),
                SpeakerModel = config.LlmModel,
                ExtractorModel = config.ExtractorModel,
                UseTools = config.UseTools,
                SystemPrompt = config.LlmSystemPrompt,
                ModelProvidersJson = config.ModelProviders,
                Timezone = TimeZoneInfo.Local.Id,
                LogConversationDebug = config.LogConversationDebug
            },
            NewHttpClient,
            tools: null);

        // ---- TTS ----
        var ttsTypeId = AiProviderInstances.ResolveProviderType(config, config.TtsProvider)?.Id ?? config.TtsProvider;
        var ttsInstance = AiProviderInstances.Find(config, config.TtsProvider);
        var ttsApiKey = ttsInstance == null ? "" : AiProviderInstances.ResolveApiKey(config, ttsInstance);
        var ttsBaseUrl = ttsInstance == null ? "" : AiProviderInstances.ResolveBaseUrl(config, ttsInstance);

        ITtsService tts = ttsTypeId switch
        {
            "kokoro" => new KokoroTtsClient(loggerFactory.CreateLogger<KokoroTtsClient>(),
                new KokoroOptions { ServerUrl = config.KokoroUrl, Voice = config.TtsVoice }, NewHttpClient),
            "replicate" => new ReplicateTtsClient(loggerFactory.CreateLogger<ReplicateTtsClient>(),
                new TtsOptions
                {
                    ProviderTypeId = "replicate",
                    ApiKey = ttsApiKey,
                    Model = config.TtsModel,
                    Voice = config.TtsVoice,
                    VoicesDirectory = voicesDir
                }, NewHttpClient),
            _ => new OpenAiTtsClient(loggerFactory.CreateLogger<OpenAiTtsClient>(),
                new TtsOptions
                {
                    ProviderTypeId = "openai-compatible",
                    ApiKey = ttsApiKey,
                    BaseUrl = ttsBaseUrl,
                    Model = config.TtsModel,
                    Voice = config.TtsVoice
                }, NewHttpClient)
        };
        log.LogInformation("[factory] stt={Stt} chat={Chat}/{Model} tts={Tts}/{TtsModel} voices={VoicesDir}",
            config.SttProvider, config.AiProvider, config.LlmModel, ttsTypeId, config.TtsModel, voicesDir);

        // ---- Audio out ----
        var pipeline = new PcmPlaybackPipeline(loggerFactory.CreateLogger<PcmPlaybackPipeline>(), new PcmPlaybackOptions
        {
            SampleRate = 24000,
            Volume = config.PlaybackVolume,
            Device = string.IsNullOrWhiteSpace(config.OutputDevice) ? null : config.OutputDevice,
            Rate = config.SpeechRate <= 0 ? 1.0 : config.SpeechRate
        });

        var speech = new SpeechQueue(tts, new PcmPlaybackAudioOut(pipeline), loggerFactory.CreateLogger<SpeechQueue>());
        await speech.StartAsync(ct);

        // The pacer must know the speech rate: faster speech shortens each chunk's playback
        // without shortening synthesis, so the no-gap growth limit tightens proportionally.
        var baseOptions = engineOptions ?? new EngineOptions();
        var engineOptionsWithRate = baseOptions with
        {
            Pacer = baseOptions.Pacer with
            {
                PlaybackSpeed = double.IsFinite(baseOptions.Pacer.PlaybackSpeed) && baseOptions.Pacer.PlaybackSpeed > 1.0
                    ? baseOptions.Pacer.PlaybackSpeed
                    : Math.Clamp(config.SpeechRate, 0.5, 2.0)
            }
        };
        var engine = new ConversationEngine(
            loggerFactory.CreateLogger<ConversationEngine>(), stt, chat, tts, speech, engineOptionsWithRate, sink);

        // Point the chat client's failure reporting at the ENGINE's sink, resolved per call, so a
        // host that attaches its UI sink after construction still receives provider errors.
        chat.OnError = message => engine.Sink.Error("llm", message);

        var voices = new VoiceLibrary(loggerFactory.CreateLogger<VoiceLibrary>(), voicesDir,
            recorder: new FfmpegAudioRecorder(loggerFactory.CreateLogger<FfmpegAudioRecorder>(),
                new CaptureOptions { Device = config.InputDevice }));

        var devices = new AudioDeviceEnumerator(loggerFactory.CreateLogger<AudioDeviceEnumerator>());

        // ---- Voice input (shared by Speak and Converse) ----
        var capture = new FfmpegStreamingCapture(loggerFactory.CreateLogger<FfmpegStreamingCapture>(),
            new CaptureOptions { Device = config.InputDevice });
        var session = new VoiceSession(loggerFactory.CreateLogger<VoiceSession>(), engine, capture);
        var dictation = new DictationService(loggerFactory.CreateLogger<DictationService>(), session,
            TextInserters.Create(loggerFactory.CreateLogger("TextInserter")));

        return new ConversationRuntime(config, engine, speech, pipeline, voices, devices, session, dictation);
    }
}
