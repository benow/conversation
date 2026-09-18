using Benow.Conversation.Config;
using Benow.Conversation.Personas;
using Xunit;

namespace Benow.Conversation.Desktop.Tests;

/// <summary>
/// The persona bundle contract (plan §4.9): a persona overrides prompt + speaker model + TTS
/// engine + voice, and passes everything else through. These are the rules the tray's persona
/// fan-out depends on.
/// </summary>
public class PersonaBindingTests
{
    private static ConversationConfig Config() => new()
    {
        LlmModel = "engine-speaker",
        LlmSystemPrompt = "engine prompt",
        TtsProvider = "replicate",
        TtsModel = "engine-tts",
        TtsVoice = "engine-voice",
        SttModel = "whisper-large-v3",
        ExtractorModel = "extractor-model"
    };

    [Fact]
    public void Apply_DefaultPersona_ChangesNothing()
    {
        var config = Config();
        PersonaBinding.Apply(config, new Persona { Name = PersonaStore.DefaultName });

        Assert.Equal("engine-speaker", config.LlmModel);
        Assert.Equal("engine prompt", config.LlmSystemPrompt);
        Assert.Equal("engine-tts", config.TtsModel);
        Assert.Equal("engine-voice", config.TtsVoice);
        Assert.Empty(PersonaBinding.Describe(new Persona { Name = PersonaStore.DefaultName }));
    }

    [Fact]
    public void Apply_OverridesOnlyWhatThePersonaDefines()
    {
        var config = Config();
        PersonaBinding.Apply(config, new Persona
        {
            Name = "Storyteller",
            SystemPrompt = "You are a bard.",
            SpeakerModel = "persona-speaker"
            // no TTS fields: the engine's voice settings must survive
        });

        Assert.Equal("persona-speaker", config.LlmModel);
        Assert.Equal("You are a bard.", config.LlmSystemPrompt);
        Assert.Equal("replicate", config.TtsProvider);
        Assert.Equal("engine-tts", config.TtsModel);
        Assert.Equal("engine-voice", config.TtsVoice);
    }

    [Fact]
    public void Apply_NeverTouchesTranscriptionOrTheExtractor()
    {
        var config = Config();
        PersonaBinding.Apply(config, new Persona
        {
            Name = "Anything",
            SystemPrompt = "Be terse.",
            TtsModel = "persona-tts"
        });

        Assert.Equal("whisper-large-v3", config.SttModel);
        Assert.Equal("extractor-model", config.ExtractorModel);
    }

    [Fact]
    public void Describe_ListsEveryOverride()
    {
        var description = PersonaBinding.Describe(new Persona
        {
            Name = "X",
            SystemPrompt = "12345",
            SpeakerModel = "m",
            TtsProvider = "replicate",
            TtsModel = "t",
            TtsVoice = "v"
        });

        Assert.Contains("prompt (5c)", description);
        Assert.Contains("model=m", description);
        Assert.Contains("tts=replicate", description);
        Assert.Contains("tts-model=t", description);
        Assert.Contains("voice=v", description);
    }

    [Fact]
    public void Apply_BlankStringsAreTreatedAsPassThroughNotAsClearing()
    {
        var config = Config();
        PersonaBinding.Apply(config, new Persona { Name = "X", SystemPrompt = "  ", SpeakerModel = "", TtsVoice = "" });

        Assert.Equal("engine-speaker", config.LlmModel);
        Assert.Equal("engine prompt", config.LlmSystemPrompt);
        Assert.Equal("engine-voice", config.TtsVoice);
    }
}
