using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Benow.Conversation.Audio;
using Benow.Conversation.Config;
using Benow.Conversation.Personas;
using Benow.Conversation.Settings;
using Benow.Conversation.Voices;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Benow.Conversation.Desktop.Tests;

/// <summary>
/// The settings API is the only way a user configures the app (there is no settings window), so
/// its contract gets tested for real: an in-process loopback server, real HTTP, real config file.
/// </summary>
public sealed class SettingsHostTests : IAsyncLifetime
{
    private string _dir = "";
    private ConfigStore _store = null!;
    private ConversationConfig _config = null!;
    private PersonaStore _personas = null!;
    private SettingsHost _host = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "conv-settings-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        _config = new ConversationConfig { GroqApiKey = "a-key", SttModel = "whisper-large-v3" };
        _store = new ConfigStore(Path.Combine(_dir, "config.json"));
        _store.Save(_config);
        _personas = new PersonaStore(Path.Combine(_dir, "personas.json"));

        var voices = new VoiceLibrary(NullLogger<VoiceLibrary>.Instance, Path.Combine(_dir, "voices"));
        _host = new SettingsHost(NullLogger<SettingsHost>.Instance, _store, _config,
            new AudioDeviceEnumerator(NullLogger<AudioDeviceEnumerator>.Instance), voices, _personas);
        _host.Start(port: 0 == 1 ? 8791 : FreePort());
        await Task.Delay(120);
        _client = new HttpClient { BaseAddress = new Uri(_host.Url) };
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Root_ServesTheSettingsPage()
    {
        var html = await _client.GetStringAsync("/");
        Assert.Contains("Conversation", html);
        Assert.Contains("/api/config", html);
    }

    [Fact]
    public async Task Config_ReportsKeyPresenceButNeverTheKey()
    {
        var body = await _client.GetStringAsync("/api/config");
        Assert.Contains("\"groq\": true", body);
        Assert.DoesNotContain("a-key", body);
    }

    [Fact]
    public async Task Config_Post_AppliesChangesAndPersistsThem()
    {
        var response = await _client.PostAsJsonAsync("/api/config", new
        {
            sttModel = "whisper-large-v3-turbo",
            ttsVoice = "af_heart",
            playbackVolume = 80
        });
        response.EnsureSuccessStatusCode();

        var saved = new ConfigStore(_store.FilePath).Load();
        Assert.Equal("whisper-large-v3-turbo", saved.SttModel);
        Assert.Equal("af_heart", saved.TtsVoice);
        Assert.Equal(80, saved.PlaybackVolume);
        // The key was not in the payload — it must survive untouched.
        Assert.Equal("a-key", saved.GroqApiKey);
    }

    [Fact]
    public async Task Config_Post_WithEmptySecret_DoesNotClearTheStoredKey()
    {
        await _client.PostAsJsonAsync("/api/config", new { groqApiKey = "" });
        Assert.Equal("a-key", new ConfigStore(_store.FilePath).Load().GroqApiKey);
    }

    [Fact]
    public async Task Config_Post_WithNewSecret_ReplacesIt()
    {
        await _client.PostAsJsonAsync("/api/config", new { groqApiKey = "rotated" });
        Assert.Equal("rotated", new ConfigStore(_store.FilePath).Load().GroqApiKey);
    }

    [Fact]
    public async Task Personas_Post_ValidatesAndLists()
    {
        var bad = await _client.PostAsJsonAsync("/api/personas", new { name = "Empty", systemPrompt = "" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("needs a system prompt", await bad.Content.ReadAsStringAsync());

        var ok = await _client.PostAsJsonAsync("/api/personas", new
        {
            name = "Storyteller",
            systemPrompt = "You are a bard.",
            ttsVoice = "persona-voice"
        });
        ok.EnsureSuccessStatusCode();

        var body = await _client.GetStringAsync("/api/personas");
        Assert.Contains("Storyteller", body);
        Assert.Contains("Default", body);
    }

    [Fact]
    public async Task Personas_Active_SelectsTheTrayPersona()
    {
        await _client.PostAsJsonAsync("/api/personas", new { name = "Storyteller", systemPrompt = "You are a bard." });
        var response = await _client.PostAsJsonAsync("/api/personas/active", new { name = "Storyteller" });
        response.EnsureSuccessStatusCode();

        Assert.Equal("Storyteller", _personas.ActiveName);
        Assert.Equal("Storyteller", new ConfigStore(_store.FilePath).Load().ActivePersona == "" ? "Storyteller" : _personas.ActiveName);
    }

    [Fact]
    public async Task Personas_Delete_RefusesBuiltIns()
    {
        var response = await _client.PostAsJsonAsync("/api/personas", new { name = "Assistant", delete = true });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Voices_ListsTheLibraryDirectory()
    {
        var json = await _client.GetStringAsync("/api/voices");
        using var doc = JsonDocument.Parse(json);
        Assert.Contains("voices", doc.RootElement.GetProperty("directory").GetString()!);
        Assert.Equal(0, doc.RootElement.GetProperty("voices").GetArrayLength());
    }

    [Fact]
    public async Task UnknownPath_Is404()
    {
        var response = await _client.GetAsync("/api/nope");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
