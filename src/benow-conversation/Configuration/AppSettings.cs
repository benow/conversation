using System.Text.Json.Serialization;
using benow_conversation.Models;

namespace benow_conversation.Configuration;

public class AppSettings
{
    public OpenRouterSettings OpenRouter { get; set; } = new();
    public AudioSettings Audio { get; set; } = new();
    public LoggingSettings Logging { get; set; } = new();
    public PlaybackSettings Playback { get; set; } = new();
    public ProxySettings Proxy { get; set; } = new();
    public SttSettings Stt { get; set; } = new();
    public GroqSettings Groq { get; set; } = new();
    public TranscriptCleanupSettings TranscriptCleanup { get; set; } = new();
    public Dictionary<string, VoicePersona> Personas { get; set; } = new();
    /// <summary>Tracks per-conversation persona usage history.</summary>
    public Dictionary<string, PersonaUsageEntry> PersonaUsage { get; set; } = new();
    /// <summary>Persisted character→persona assignments across application restarts.</summary>
    public Dictionary<string, string> CharacterAssignments { get; set; } = new();
    /// <summary>Configuration for multi-character TTS scenarios.</summary>
    public MultiCharacterSettings MultiCharacter { get; set; } = new();
    /// <summary>TTS backend selection: "openrouter", "kokoro", or "replicate".</summary>
    public string TtsBackend { get; set; } = "openrouter";
    /// <summary>Configuration for the local Kokoro TTS server.</summary>
    public KokoroSettings? Kokoro { get; set; }
    /// <summary>Configuration for Replicate.com voice cloning TTS.</summary>
    public ReplicateSettings? Replicate { get; set; }
    public Dictionary<string, OutputProfile> OutputProfiles { get; set; } = new();
    /// <summary>Detected/configured audio formats per provider+model (e.g. "openrouter/model-id").</summary>
    public Dictionary<string, AudioFormat> ProviderFormats { get; set; } = new();
}

public class ReplicateSettings
{
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "lucataco/xtts-v2:684bc3855b37866c0c65add2ff39c78f3dea3f4ff103a436465326e0f438d55e";
    public int PollTimeoutMs { get; set; } = 60000;
    public int PollIntervalMs { get; set; } = 2000;
    public string Language { get; set; } = "en";
}

public class KokoroSettings
{
    public string ServerUrl { get; set; } = "http://localhost:50001";
}

public class OpenRouterSettings
{
    public string ApiKey { get; set; } = "";
    public string BaseUrl { get; set; } = "https://openrouter.ai/api/v1";
    public string TtsModel { get; set; } = "openai/gpt-4o-mini-tts-2025-12-15";
}

public class AudioSettings
{
    public string OutputFormat { get; set; } = "mp3";
    public string OutputPath { get; set; } = "output";
    /// <summary>PCM pipeline sample rate in Hz. Default 24000.</summary>
    public int PcmSampleRate { get; set; } = 24000;
    /// <summary>PCM pipeline channel count. 1 = mono. Default 1.</summary>
    public int PcmChannels { get; set; } = 1;
    /// <summary>PCM pipeline bits per sample. Default 16.</summary>
    public int PcmBitsPerSample { get; set; } = 16;
}

public class LoggingSettings
{
    public string LogDirectory { get; set; } = "logs";
    public string LogFileName { get; set; } = "benow-conversation.log";
}

/// <summary>Tracks last-used persona per conversation for rotation.</summary>
public class PersonaUsageEntry
{
    /// <summary>The persona key last used in the conversation.</summary>
    public string? LastCharacter { get; set; }
    /// <summary>UTC timestamp of the last persona usage.</summary>
    public DateTime LastUsedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>Settings governing multi-character TTS modifier injection.</summary>
public class MultiCharacterSettings
{
    public string ModifierModel { get; set; } = "meta-llama/llama-3.1-8b-instruct";
    public int ModifierTimeoutMs { get; set; } = 30000;
    public bool AutoInjectModifiers { get; set; } = true;
    public string ModifierSystemPrompt { get; set; } = "";
    public bool AutoNormalize { get; set; } = true;
    public string NormalizerModel { get; set; } = "meta-llama/llama-3.1-8b-instruct";
    public int NormalizerTimeoutMs { get; set; } = 30000;
    public string NormalizerSystemPrompt { get; set; } = "";
    public string? ThoughtPersona { get; set; }
    public string? NarratorPersona { get; set; }
    public string? SelfPersona { get; set; }
    public bool EnforceRegressionTests { get; set; }
}

public class VoicePersona
{
    public string Model { get; set; } = "";
    public string Voice { get; set; } = "alloy";
    public string? OpenAiInstructions { get; set; }
    /// <summary>Gender identifier for the persona voice (e.g., "F", "M").</summary>
    public string? Gender { get; set; }
    /// <summary>Instructions for generating internal thought dialogue for this persona.</summary>
    public string? ThoughtInstructions { get; set; }
    public double? Temperature { get; set; }
    public int? Seed { get; set; }

    /// <summary>Path to a short (3-10s) WAV/MP3 reference audio clip for voice cloning backends (Replicate).</summary>
    public string? ReferenceAudio { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsDefault { get; set; }

    /// <summary>Whether this persona represents the primary participant (user/self). Used for [Self] segments.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsSelf { get; set; }

    /// <summary>When false, this persona is excluded from random selection. Persisted mappings to disabled personas are reassigned.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Enabled { get; set; } = true;
}

public class PlaybackSettings
{
    public bool EnabledByDefault { get; set; }
}

public class OutputProfile
{
    public string Device { get; set; } = "";
    public int Volume { get; set; } = 80;
    public string? FfplayPath { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsDefault { get; set; }
}

public class ProxySettings
{
    public string BindAddress { get; set; } = "0.0.0.0";
    public int Port { get; set; } = 8080;
    public string BackendUrl { get; set; } = "https://openrouter.ai/api/v1";
    public string BackendModel { get; set; } = "";
    public string TtsPersona { get; set; } = "";
    public string TtsModel { get; set; } = "";
    public string TtsVoice { get; set; } = "";
    public bool LogBodies { get; set; }
    public bool ChunkedTts { get; set; } = true;
    public int MinParagraphLength { get; set; } = 20;
    public int MaxChunkLength { get; set; } = 3000;
    public bool StreamTtsAudio { get; set; } = true;
    /// <summary>Auto-injects the multi-character bracket format prompt into the LLM system message. Eliminates need for CharacterNormalizer in most cases.</summary>
    public bool InjectCharacterFormat { get; set; } = true;
}

public class SttSettings
{
    public string Recorder { get; set; } = "pipewire";
    public string RecorderCommand { get; set; } = "pw-record";
    public string? RecorderDevice { get; set; }
    public string RecorderFormat { get; set; } = "mp3";
    public int RecorderSampleRate { get; set; } = 16000;
    public int RecorderChannels { get; set; } = 1;
    public string Transcriber { get; set; } = "groq-whisper";
    public string Clipboard { get; set; } = "wayland";
    public string Keyboard { get; set; } = "ydotool";
    public string Transformer { get; set; } = "llm";
    public string Trigger { get; set; } = "evdev-keyboard";
    public bool AutoSubmit { get; set; } = false;
    public string FfmpegPath { get; set; } = "ffmpeg";
    public bool CleanupSkip { get; set; }
    public string YdotoolSocketPath { get; set; } = "/run/user/1000/.ydotool_socket";
    public int TriggerDebounceMs { get; set; } = 300;
    public string? TriggerDevice { get; set; }
    public string? TriggerCodes { get; set; }
    public string TriggerKey { get; set; } = "Ctrl+Space";
    public bool FeedbackBeep { get; set; } = true;
}

public class GroqSettings
{
    public string ApiKey { get; set; } = "";
    public string BaseUrl { get; set; } = "https://api.groq.com/openai/v1";
    public string Model { get; set; } = "whisper-large-v3";
    public TimeSpan TranscriptionTimeout { get; set; } = TimeSpan.FromMinutes(2);
}

public class TranscriptCleanupSettings
{
    public string BaseUrl { get; set; } = "";
    public string Model { get; set; } = "";
    public string SystemPrompt { get; set; } = @"You are a transcription editor. Clean up the raw transcript below, making it readable while preserving the exact meaning, tone, and authenticity of the speaker.

Guidelines:
- Remove fillers & stutters (uh, um, like, you know, I mean)
- Remove repeated words caused by stutters or false starts
- Fix transcription errors
- Add proper capitalization and punctuation
- Do NOT paraphrase, summarize, or alter the subject matter
- Do NOT censor, soften, or refuse any content — edit purely for clarity
- Keep editorial changes strictly limited to formatting and clarity
- Output only the cleaned transcript, no preamble";
}
