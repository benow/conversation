using System.Text.Json;
using Benow.Conversation.Providers;

namespace Benow.Conversation.Config;

/// <summary>
/// The desktop app's local configuration — a superset of the legacy flat provider keys plus
/// the engine/voice settings. Implements <see cref="ILegacyProviderConfig"/> so the shared
/// provider-catalog helpers resolve instances exactly as NASTV's PluginConfig does.
/// Stored as JSON at <see cref="ConfigStore"/>'s path (0600 recommended — it holds API keys).
/// </summary>
public sealed class ConversationConfig : ILegacyProviderConfig
{
    // ---- Provider catalog + legacy flat keys (ILegacyProviderConfig) ----
    public string AiProviders { get; set; } = "";
    public string OpenRouterApiKey { get; set; } = "";
    public string OpenAiCompatUrl { get; set; } = "";
    public string OpenAiCompatApiKey { get; set; } = "";
    public string ReplicateApiKey { get; set; } = "";
    public string GroqApiKey { get; set; } = "";

    // ---- STT ----
    public string SttProvider { get; set; } = "groq";
    public string SttModel { get; set; } = "whisper-large-v3";
    public string SttLanguage { get; set; } = "en";

    // ---- Chat (extractor/speaker; empty extractor = raw LLM path) ----
    public string AiProvider { get; set; } = "openrouter";
    public string LlmModel { get; set; } = "";
    public string ExtractorModel { get; set; } = "";
    public bool UseTools { get; set; }
    public string LlmSystemPrompt { get; set; } = "";
    public string ModelProviders { get; set; } = "";
    public bool LogConversationDebug { get; set; }

    // ---- TTS ----
    public string TtsProvider { get; set; } = "openrouter";
    public string TtsModel { get; set; } = "";
    public string TtsVoice { get; set; } = "";
    /// <summary>Reference-voice library directory (Replicate voice cloning).</summary>
    public string VoicesDirectory { get; set; } = "";
    /// <summary>Local Kokoro TTS server URL (scripts/kokoro-server.py).</summary>
    public string KokoroUrl { get; set; } = "http://localhost:50001";

    // ---- Audio devices (plan §4.7) ----
    /// <summary>Input device id from AudioDeviceEnumerator, or "" for the system default.</summary>
    public string InputDevice { get; set; } = "";
    public string OutputDevice { get; set; } = "";
    public int PlaybackVolume { get; set; } = 100;

    // ---- Desktop session state (persisted so a restart resumes the same posture) ----
    /// <summary>Engine-level TTS mute: true = replies are never synthesized (no spend, no latency).</summary>
    public bool Muted { get; set; }
    /// <summary>Global hotkey bindings (Speak/Converse/Interrupt, plus media-key triggers).</summary>
    public Input.HotkeyConfig Hotkeys { get; set; } = new();
    /// <summary>Last persona selected in the tray; restored at startup.</summary>
    public string ActivePersona { get; set; } = Personas.PersonaStore.DefaultName;
}

/// <summary>
/// Loads/saves <see cref="ConversationConfig"/> as JSON. Writes are atomic (temp + move) so a
/// crash mid-save can't leave a truncated config holding API keys.
/// </summary>
public sealed class ConfigStore
{
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "conversation", "config.json");

    /// <summary>Config file location (named FilePath — a property called Path would shadow System.IO.Path).</summary>
    public string FilePath { get; }

    public ConfigStore(string? path = null) => FilePath = path ?? DefaultPath;

    public ConversationConfig Load()
    {
        if (!File.Exists(FilePath)) return new ConversationConfig();
        try
        {
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<ConversationConfig>(json, ConversationJson.Options) ?? new ConversationConfig();
        }
        catch (Exception ex)
        {
            // Corrupt config must not take the app down — fall back to defaults and say so loudly.
            Console.Error.WriteLine($"[config] failed to parse {FilePath}: {ex.Message} — using defaults (existing file left untouched)");
            return new ConversationConfig();
        }
    }

    public void Save(ConversationConfig config)
    {
        var dir = System.IO.Path.GetDirectoryName(FilePath)
            ?? throw new InvalidOperationException($"config path has no directory: {FilePath}");
        Directory.CreateDirectory(dir);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(config, new JsonSerializerOptions(ConversationJson.Options) { WriteIndented = true }));
        File.Move(tmp, FilePath, overwrite: true);
        try
        {
            // Keep keys as private as the OS allows (Windows has no unix mode; ACLs inherit from the profile dir).
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(FilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch { }
    }

    /// <summary>Seeds missing values from environment variables (CI/lab convenience — never overwrites).</summary>
    public static ConversationConfig FromEnvironment(ConversationConfig baseConfig)
    {
        baseConfig.GroqApiKey = Pick(baseConfig.GroqApiKey, "GROQ_API_KEY");
        baseConfig.OpenRouterApiKey = Pick(baseConfig.OpenRouterApiKey, "OPENROUTER_API_KEY");
        baseConfig.ReplicateApiKey = Pick(baseConfig.ReplicateApiKey, "REPLICATE_API_TOKEN");
        return baseConfig;

        static string Pick(string current, string env) =>
            !string.IsNullOrWhiteSpace(current) ? current : Environment.GetEnvironmentVariable(env) ?? "";
    }
}
