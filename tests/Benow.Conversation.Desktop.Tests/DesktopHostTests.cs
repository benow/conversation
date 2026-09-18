using Benow.Conversation.Composition;
using Benow.Conversation.Config;
using Benow.Conversation.Desktop;
using Benow.Conversation.Personas;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Benow.Conversation.Desktop.Tests;

/// <summary>
/// The host's decision logic: mute semantics, persona switching, error handling. Uses a real
/// <see cref="ConversationRuntime"/> (the same wiring the app ships) pointed at an unroutable
/// local address, so an accidental provider call fails in milliseconds instead of hanging.
/// No audio device is opened by any of these.
/// </summary>
public sealed class DesktopHostTests : IAsyncLifetime
{
    private string _dir = "";
    private ConversationRuntime _runtime = null!;
    private ConfigStore _store = null!;
    private PersonaStore _personas = null!;

    public async Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "conv-host-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        var config = new ConversationConfig
        {
            // Port 1 is a closed port: connection refused immediately, no DNS, no timeout wait.
            AiProvider = "openai-compatible",
            OpenAiCompatUrl = "http://127.0.0.1:1/v1",
            OpenAiCompatApiKey = "test",
            LlmModel = "engine-speaker",
            LlmSystemPrompt = "engine prompt",
            TtsProvider = "openai",
            TtsModel = "engine-tts",
            TtsVoice = "engine-voice",
            SttProvider = "openai-compatible",
            VoicesDirectory = Path.Combine(_dir, "voices")
        };
        _store = new ConfigStore(Path.Combine(_dir, "config.json"));
        _store.Save(config);
        _personas = new PersonaStore(Path.Combine(_dir, "personas.json"));
        _personas.Upsert(new Persona
        {
            Name = "Storyteller",
            SystemPrompt = "You are a bard.",
            SpeakerModel = "persona-speaker",
            TtsVoice = "persona-voice"
        });

        _runtime = await ConversationFactory.CreateAsync(NullLoggerFactory.Instance, config);
    }

    public async Task DisposeAsync()
    {
        await _runtime.DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private DesktopHost NewHost() => new(NullLoggerFactory.Instance, _runtime, _store, _personas);

    [Fact]
    public async Task ToggleMute_SilencesAtTheEngineAndPersists()
    {
        var host = NewHost();
        Assert.False(host.State.Muted);
        Assert.False(_runtime.Engine.Muted);

        await host.DispatchAsync(TrayAction.ToggleMute);
        Assert.True(_runtime.Engine.Muted);
        Assert.True(host.State.Muted);

        await host.DispatchAsync(TrayAction.ToggleMute);
        Assert.False(_runtime.Engine.Muted);
        Assert.False(new ConfigStore(_store.FilePath).Load().Muted);
    }

    [Fact]
    public async Task ToggleMute_WritesTheMutedFlagThroughToDisk()
    {
        var host = NewHost();
        await host.DispatchAsync(TrayAction.ToggleMute);
        Assert.True(new ConfigStore(_store.FilePath).Load().Muted);
    }

    [Fact]
    public async Task SetPersona_AppliesTheBundleAndPersistsTheChoice()
    {
        var host = NewHost();
        await host.DispatchAsync(TrayAction.SetPersona, "Storyteller");

        Assert.Equal("Storyteller", host.State.ActivePersona);
        Assert.Equal("persona-speaker", _runtime.Config.LlmModel);
        Assert.Equal("You are a bard.", _runtime.Config.LlmSystemPrompt);
        Assert.Equal("persona-voice", _runtime.Config.TtsVoice);
        // Not persona-scoped — must survive a switch.
        Assert.Equal("engine-tts", _runtime.Config.TtsModel);
        Assert.Equal("Storyteller", new ConfigStore(_store.FilePath).Load().ActivePersona);
    }

    [Fact]
    public async Task SetPersona_SwitchingBackToDefault_KeepsTheLastAppliedBundle()
    {
        // Documented behaviour: Default is pass-through, so it does not restore engine defaults —
        // it simply stops overriding. The engine keeps whatever the last persona set.
        var host = NewHost();
        await host.DispatchAsync(TrayAction.SetPersona, "Storyteller");
        await host.DispatchAsync(TrayAction.SetPersona, PersonaStore.DefaultName);

        Assert.Equal(PersonaStore.DefaultName, host.State.ActivePersona);
        Assert.Equal("persona-speaker", _runtime.Config.LlmModel);
    }

    [Fact]
    public async Task Interrupt_LeavesTheHostIdle()
    {
        var host = NewHost();
        await host.DispatchAsync(TrayAction.Interrupt);
        Assert.Equal("idle", host.State.Status);
    }

    [Fact]
    public async Task ReplayLast_WhenMuted_TellsTheUserInsteadOfSpeakingSilently()
    {
        var host = NewHost();
        host.PrimeLastReply("a previous reply");   // no live provider in tests
        await host.DispatchAsync(TrayAction.ToggleMute);
        await host.DispatchAsync(TrayAction.ReplayLast);
        Assert.Contains("muted", host.State.Status);
    }

    [Fact]
    public async Task ReplayLast_WithAKeyButNoReply_DoesNothing()
    {
        var host = NewHost();
        await host.DispatchAsync(TrayAction.ReplayLast);
        Assert.Equal("idle", host.State.Status);
    }

    [Fact]
    public async Task Send_FailingProvider_SurfacesTheCauseInsteadOfClaimingNoReply()
    {
        var host = NewHost();
        var errors = new List<string>();
        host.Error += errors.Add;

        // The provider is unreachable: the turn must fail into the state, never escape the host.
        await host.SendAsync("hello there");

        Assert.Equal("error", host.State.Status);
        Assert.NotEmpty(errors);
        Assert.Contains(errors, e => e.StartsWith("llm:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Send_StreamingReply_ReachesTheOverlayChunkByChunk()
    {
        // The overlay is fed by the engine's sink; with an unreachable provider no chunks arrive,
        // but the wiring itself (sink → host event) must be live.
        var host = NewHost();
        var chunks = new List<string>();
        host.ReplyChunk += chunks.Add;

        host.Sink.LlmChunk("hello ");
        host.Sink.LlmChunk("world");

        Assert.Equal(new[] { "hello ", "world" }, chunks);
    }

    [Fact]
    public async Task StateChanged_FiresForEveryMutationTheTrayRenders()
    {
        var host = NewHost();
        var states = new List<TrayState>();
        host.StateChanged += states.Add;

        await host.DispatchAsync(TrayAction.ToggleMute);
        await host.DispatchAsync(TrayAction.SetPersona, "Storyteller");

        Assert.True(states.Count >= 2);
        Assert.Contains(states, s => s.Muted);
        Assert.Contains(states, s => s.ActivePersona == "Storyteller");
    }
}
