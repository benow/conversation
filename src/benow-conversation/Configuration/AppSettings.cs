using System.Text.Json.Serialization;

namespace benow_conversation.Configuration;

public class AppSettings
{
    public OpenRouterSettings OpenRouter { get; set; } = new();
    public AudioSettings Audio { get; set; } = new();
    public LoggingSettings Logging { get; set; } = new();
    public PlaybackSettings Playback { get; set; } = new();
    public ProxySettings Proxy { get; set; } = new();
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
