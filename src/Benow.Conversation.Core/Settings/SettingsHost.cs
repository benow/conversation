using System.Net;
using System.Text;
using System.Text.Json;
using Benow.Conversation.Audio;
using Benow.Conversation.Config;
using Benow.Conversation.Personas;
using Benow.Conversation.Voices;
using Microsoft.Extensions.Logging;

namespace Benow.Conversation.Settings;

/// <summary>
/// Loopback-only settings host: serves a small HTML settings page plus a JSON API over the
/// shared config store, device enumerator, voice library and persona store. Phase-1 settings
/// are browser-served (the plan's choice: a real browser does getUserMedia fine on Linux, and
/// the settings surface is the same one NASTV's AI settings provide). Bound to 127.0.0.1 only —
/// never exposed off-machine.
/// </summary>
public sealed class SettingsHost : IAsyncDisposable
{
    private readonly ILogger<SettingsHost> _logger;
    private readonly ConfigStore _configStore;
    private readonly ConversationConfig _config;
    private readonly AudioDeviceEnumerator _devices;
    private readonly VoiceLibrary _voices;
    private readonly PersonaStore _personas;
    private readonly Func<string, Task<string>>? _testVoice;
    private readonly HttpListener _listener = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public SettingsHost(ILogger<SettingsHost> logger, ConfigStore configStore, ConversationConfig config,
        AudioDeviceEnumerator devices, VoiceLibrary voices, PersonaStore personas, Func<string, Task<string>>? testVoice = null)
    {
        _logger = logger;
        _configStore = configStore;
        _config = config;
        _devices = devices;
        _voices = voices;
        _personas = personas;
        _testVoice = testVoice;
    }

    public int Port { get; private set; }

    public string Url => $"http://127.0.0.1:{Port}/";

    public void Start(int port = 8791)
    {
        Port = port;
        _listener.Prefixes.Clear();
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        try
        {
            _listener.Start();
        }
        catch (HttpListenerException ex)
        {
            _logger.LogError(ex, "[settings] could not bind {Url} — settings page unavailable. " +
                "Fix: check nothing else holds port {Port}", Url, port);
            return;
        }
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
        _logger.LogInformation("[settings] serving at {Url}", Url);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch (HttpListenerException) { break; }
            catch (ObjectDisposedException) { break; }
            _ = Task.Run(() => HandleAsync(ctx), ct);
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url?.AbsolutePath ?? "/";
        try
        {
            switch (path)
            {
                case "/":
                    await WriteAsync(ctx, 200, "text/html; charset=utf-8", SettingsPage.Html);
                    return;
                case "/api/config" when ctx.Request.HttpMethod == "GET":
                    await WriteJsonAsync(ctx, 200, ConfigJson());
                    return;
                case "/api/config" when ctx.Request.HttpMethod == "POST":
                    await SaveConfigAsync(ctx);
                    return;
                case "/api/devices":
                    var inputs = await _devices.ListInputsAsync(CancellationToken.None);
                    await WriteJsonAsync(ctx, 200, new { inputs, current = _config.InputDevice });
                    return;
                case "/api/voices":
                    await WriteJsonAsync(ctx, 200, new { directory = _voices.DirectoryPath, voices = _voices.List() });
                    return;
                case "/api/personas" when ctx.Request.HttpMethod == "GET":
                    await WriteJsonAsync(ctx, 200, new { active = _personas.ActiveName, personas = _personas.All });
                    return;
                case "/api/personas" when ctx.Request.HttpMethod == "POST":
                    await SavePersonaAsync(ctx);
                    return;
                case "/api/personas/active" when ctx.Request.HttpMethod == "POST":
                    var activeReq = await ReadJsonAsync<PersonaRequest>(ctx);
                    var chosen = _personas.SetActive(activeReq?.Name);
                    await WriteJsonAsync(ctx, 200, new { active = chosen.Name });
                    return;
                case "/api/voice-test" when ctx.Request.HttpMethod == "POST":
                    if (_testVoice == null) { await WriteJsonAsync(ctx, 503, new { error = "voice test unavailable" }); return; }
                    await WriteJsonAsync(ctx, 200, new { result = await _testVoice("This is a voice test from the conversation app.") });
                    return;
                default:
                    await WriteAsync(ctx, 404, "text/plain", "not found");
                    return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[settings] {Path} failed: {Error}", path, ex.Message);
            try { await WriteJsonAsync(ctx, 500, new { error = ex.Message }); } catch { }
        }
    }

    /// <summary>Config with secrets masked to presence flags (the page never echoes keys back).</summary>
    private object ConfigJson() => new
    {
        providerKeys = new
        {
            groq = !string.IsNullOrWhiteSpace(_config.GroqApiKey),
            openRouter = !string.IsNullOrWhiteSpace(_config.OpenRouterApiKey),
            replicate = !string.IsNullOrWhiteSpace(_config.ReplicateApiKey),
            openAiCompat = !string.IsNullOrWhiteSpace(_config.OpenAiCompatApiKey)
        },
        stt = new { provider = _config.SttProvider, model = _config.SttModel, language = _config.SttLanguage },
        chat = new { provider = _config.AiProvider, model = _config.LlmModel, extractor = _config.ExtractorModel, useTools = _config.UseTools, systemPrompt = _config.LlmSystemPrompt },
        tts = new { provider = _config.TtsProvider, model = _config.TtsModel, voice = _config.TtsVoice },
        audio = new { inputDevice = _config.InputDevice, outputDevice = _config.OutputDevice, volume = _config.PlaybackVolume, speechRate = _config.SpeechRate <= 0 ? 1.0 : _config.SpeechRate },
        voicesDirectory = _config.VoicesDirectory,
        activePersona = _personas.ActiveName
    };

    private async Task SaveConfigAsync(HttpListenerContext ctx)
    {
        var patch = await ReadJsonAsync<ConfigPatch>(ctx);
        if (patch == null) { await WriteJsonAsync(ctx, 400, new { error = "invalid body" }); return; }

        // Only non-empty secrets are applied — an empty field means "leave the stored key alone".
        if (!string.IsNullOrWhiteSpace(patch.GroqApiKey)) _config.GroqApiKey = patch.GroqApiKey!;
        if (!string.IsNullOrWhiteSpace(patch.OpenRouterApiKey)) _config.OpenRouterApiKey = patch.OpenRouterApiKey!;
        if (!string.IsNullOrWhiteSpace(patch.ReplicateApiKey)) _config.ReplicateApiKey = patch.ReplicateApiKey!;
        if (!string.IsNullOrWhiteSpace(patch.OpenAiCompatApiKey)) _config.OpenAiCompatApiKey = patch.OpenAiCompatApiKey!;

        if (patch.SttProvider != null) _config.SttProvider = patch.SttProvider;
        if (patch.SttModel != null) _config.SttModel = patch.SttModel;
        if (patch.SttLanguage != null) _config.SttLanguage = patch.SttLanguage;
        if (patch.AiProvider != null) _config.AiProvider = patch.AiProvider;
        if (patch.LlmModel != null) _config.LlmModel = patch.LlmModel;
        if (patch.ExtractorModel != null) _config.ExtractorModel = patch.ExtractorModel;
        if (patch.UseTools.HasValue) _config.UseTools = patch.UseTools.Value;
        if (patch.LlmSystemPrompt != null) _config.LlmSystemPrompt = patch.LlmSystemPrompt;
        if (patch.TtsProvider != null) _config.TtsProvider = patch.TtsProvider;
        if (patch.TtsModel != null) _config.TtsModel = patch.TtsModel;
        if (patch.TtsVoice != null) _config.TtsVoice = patch.TtsVoice;
        if (patch.InputDevice != null) _config.InputDevice = patch.InputDevice;
        if (patch.OutputDevice != null) _config.OutputDevice = patch.OutputDevice;
        if (patch.PlaybackVolume.HasValue) _config.PlaybackVolume = patch.PlaybackVolume.Value;
        if (patch.SpeechRate.HasValue) _config.SpeechRate = Math.Clamp(patch.SpeechRate.Value, 0.5, 2.0);

        _configStore.Save(_config);
        _logger.LogInformation("[settings] config saved");
        await WriteJsonAsync(ctx, 200, new { ok = true, note = "applies on next restart unless the app reloads config" });
    }

    private async Task SavePersonaAsync(HttpListenerContext ctx)
    {
        var req = await ReadJsonAsync<PersonaRequest>(ctx);
        if (req == null) { await WriteJsonAsync(ctx, 400, new { error = "invalid body" }); return; }
        if (req.Delete == true)
        {
            var deleted = _personas.Delete(req.Name ?? "");
            await WriteJsonAsync(ctx, deleted ? 200 : 400, new { ok = deleted, error = deleted ? null : "cannot delete (built-in or missing)" });
            return;
        }
        try
        {
            var persona = _personas.Upsert(new Persona
            {
                Name = req.Name ?? "",
                SystemPrompt = req.SystemPrompt ?? "",
                InteractionClass = req.InteractionClass ?? "assistant",
                SpeakerModel = req.SpeakerModel,
                TtsProvider = req.TtsProvider,
                TtsModel = req.TtsModel,
                TtsVoice = req.TtsVoice
            });
            await WriteJsonAsync(ctx, 200, new { ok = true, persona });
        }
        catch (ArgumentException ex)
        {
            await WriteJsonAsync(ctx, 400, new { error = ex.Message });
        }
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpListenerContext ctx) where T : class
    {
        using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();
        return string.IsNullOrWhiteSpace(body)
            ? null
            : JsonSerializer.Deserialize<T>(body, ConversationJson.Options);
    }

    private static async Task WriteJsonAsync(HttpListenerContext ctx, int status, object payload) =>
        await WriteAsync(ctx, status, "application/json; charset=utf-8",
            JsonSerializer.Serialize(payload, new JsonSerializerOptions(ConversationJson.Options) { WriteIndented = true }));

    private static async Task WriteAsync(HttpListenerContext ctx, int status, string contentType, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = contentType;
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        try { _listener.Stop(); _listener.Close(); } catch { }
        if (_loop != null) await Task.WhenAny(_loop, Task.Delay(500));
        _cts?.Dispose();
    }

    private sealed class ConfigPatch
    {
        public string? GroqApiKey { get; set; }
        public string? OpenRouterApiKey { get; set; }
        public string? ReplicateApiKey { get; set; }
        public string? OpenAiCompatApiKey { get; set; }
        public string? SttProvider { get; set; }
        public string? SttModel { get; set; }
        public string? SttLanguage { get; set; }
        public string? AiProvider { get; set; }
        public string? LlmModel { get; set; }
        public string? ExtractorModel { get; set; }
        public bool? UseTools { get; set; }
        public string? LlmSystemPrompt { get; set; }
        public string? TtsProvider { get; set; }
        public string? TtsModel { get; set; }
        public string? TtsVoice { get; set; }
        public string? InputDevice { get; set; }
        public string? OutputDevice { get; set; }
        public int? PlaybackVolume { get; set; }
        public double? SpeechRate { get; set; }
    }

    private sealed class PersonaRequest
    {
        public string? Name { get; set; }
        public string? SystemPrompt { get; set; }
        public string? InteractionClass { get; set; }
        public string? SpeakerModel { get; set; }
        public string? TtsProvider { get; set; }
        public string? TtsModel { get; set; }
        public string? TtsVoice { get; set; }
        public bool? Delete { get; set; }
    }
}
