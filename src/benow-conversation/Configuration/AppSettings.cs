using System.Text.Json.Serialization;

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
    public Dictionary<string, OutputProfile> OutputProfiles { get; set; } = new();
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
}

public class LoggingSettings
{
    public string LogDirectory { get; set; } = "logs";
    public string LogFileName { get; set; } = "benow-conversation.log";
}

public class VoicePersona
{
    public string Model { get; set; } = "";
    public string Voice { get; set; } = "alloy";
    public string? OpenAiInstructions { get; set; }
    public double? Temperature { get; set; }
    public int? Seed { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsDefault { get; set; }
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
}

public class SttSettings
{
    public string Recorder { get; set; } = "pipewire";
    public string RecorderCommand { get; set; } = "pw-record";
    public string RecorderFormat { get; set; } = "wav";
    public int RecorderSampleRate { get; set; } = 16000;
    public int RecorderChannels { get; set; } = 1;
    public string Transcriber { get; set; } = "groq-whisper";
    public string Clipboard { get; set; } = "wayland";
    public string Keyboard { get; set; } = "ydotool";
    public string Transformer { get; set; } = "llm";
    public string Trigger { get; set; } = "console";
    public bool AutoSubmit { get; set; } = true;
    public string FfmpegPath { get; set; } = "ffmpeg";
    public bool CleanupSkip { get; set; }
    public string YdotoolSocketPath { get; set; } = "/tmp/.ydotool_socket";
    public int TriggerDebounceMs { get; set; } = 300;
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
    public string Model { get; set; } = "meta-llama/llama-3.1-8b-instruct";
    public string SystemPrompt { get; set; } = @"You are a professional editor. Your task is to clean up the raw transcript provided below, making it highly readable while preserving the exact meaning, tone, and authenticity of the speaker.

Guidelines for editing:
- Remove fillers & stutters (uh, um, like, you know, I mean)
- Remove repeated words caused by stutters or false starts
- Fix transcription errors
- Add proper capitalization and punctuation
- DO NOT paraphrase, summarize, or alter the subject matter
- Keep editorial changes strictly limited to formatting and clarity
- Output only the cleaned transcript, no preamble";
}
