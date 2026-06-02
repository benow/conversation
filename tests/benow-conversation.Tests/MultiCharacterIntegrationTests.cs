using benow_conversation.Configuration;
using benow_conversation.Models;
using benow_conversation.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace benow_conversation.Tests;

/// <summary>Integration tests exercising the full multi-character pipeline from parsing through playback.</summary>
public class MultiCharacterIntegrationTests
{
    private static IOptions<AppSettings> CreateSettings(bool withThoughtAndNarrator = true)
    {
        var settings = new AppSettings
        {
            MultiCharacter = new MultiCharacterSettings
            {
                ThoughtPersona = withThoughtAndNarrator ? "female-3" : null,
                NarratorPersona = withThoughtAndNarrator ? "female-9" : null,
                AutoInjectModifiers = false,
                ModifierModel = "test-model",
                ModifierTimeoutMs = 2000
            },
            OpenRouter = new OpenRouterSettings { TtsModel = "openai/gpt-4o-mini-tts-2025-12-15" }
        };

        settings.Personas["female-1"] = new VoicePersona { Voice = "marin", Gender = "F", IsDefault = true, Model = "openai/gpt-4o-mini-tts-2025-12-15" };
        settings.Personas["female-2"] = new VoicePersona { Voice = "nova", Gender = "F", Model = "openai/gpt-4o-mini-tts-2025-12-15" };
        settings.Personas["female-3"] = new VoicePersona { Voice = "coral", Gender = "F", ThoughtInstructions = "think deeply", Model = "openai/gpt-4o-mini-tts-2025-12-15" };
        settings.Personas["female-9"] = new VoicePersona { Voice = "ballad", Gender = "F", Model = "openai/gpt-4o-mini-tts-2025-12-15" };
        settings.Personas["female-8"] = new VoicePersona { Voice = "ash", Gender = "F", Model = "openai/gpt-4o-mini-tts-2025-12-15" };
        settings.Personas["female-10"] = new VoicePersona { Voice = "echo", Gender = "F", Model = "openai/gpt-4o-mini-tts-2025-12-15" };

        settings.CharacterAssignments["Marina"] = "female-2";
        settings.CharacterAssignments["Sofia"] = "female-8";
        settings.CharacterAssignments["Mopsie"] = "female-10";

        return Options.Create(settings);
    }

    [Fact]
    public void Parse_ScriptWithCharactersThoughtsAndNarration()
    {
        var text = "[Marina] *her eyes flutter open* Ah, señor... [thought] *ponders* I wonder if he'll follow. [/thought] *she smiles*";
        var segments = CharacterParser.Parse(text);

        Assert.Equal(5, segments.Count);
        Assert.True(segments[0].IsNarration);
        Assert.Equal("her eyes flutter open", segments[0].SpokenText);
        Assert.Equal("Marina", segments[0].CharacterName);

        Assert.False(segments[1].IsNarration);
        Assert.False(segments[1].IsThought);
        Assert.Equal("Ah, señor...", segments[1].SpokenText);

        Assert.True(segments[2].IsNarration);
        Assert.True(segments[2].IsThought);
        Assert.Equal("ponders", segments[2].SpokenText);

        Assert.False(segments[3].IsNarration);
        Assert.True(segments[3].IsThought);
        Assert.Equal("I wonder if he'll follow.", segments[3].SpokenText);

        Assert.True(segments[4].IsNarration);
        Assert.Equal("she smiles", segments[4].SpokenText);
    }

    [Fact]
    public async Task FullPipeline_ParsesAllocatesPlays()
    {
        var settings = CreateSettings();
        var tts = new Mock<ITtsProvider>();
        tts.SetupGet(t => t.OutputFormat).Returns(AudioFormat.Pcm24000Mono);
        var synthesizedTexts = new List<string>();
        var synthesizedVoices = new List<string?>();
        var synthesizedTemps = new List<double?>();

        tts.Setup(t => t.SynthesizeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<double?>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(new byte[] { 0x01 }))
            .Callback<string, string, string, string?, double?, int?, CancellationToken>((text, personaKey, voice, inst, temp, seed, ct) =>
            {
                synthesizedTexts.Add(text);
                synthesizedVoices.Add(voice);
                synthesizedTemps.Add(temp);
            });

        var pipeline = new Mock<IPersistentAudioPipeline>();
        var allocator = new PersonaAllocator(settings, Mock.Of<ILogger<PersonaAllocator>>());
        var player = new ParallelTtsPlayer(
            tts.Object, pipeline.Object, Mock.Of<IAudioPlayer>(),
            allocator, CreateConverter(), Mock.Of<ILogger<ParallelTtsPlayer>>());

        var text = "[Marina] *her eyes flutter open* Ah, señor... [Sofia] Mami! [thought] *giggles* He's cute! [/thought] Can we go? [Mopsie] *beeps* Systems nominal.";
        var segments = CharacterParser.Parse(text);

        var thoughtPersonaKey = settings.Value.MultiCharacter.ThoughtPersona;
        var narratorPersonaKey = settings.Value.MultiCharacter.NarratorPersona;
        var defaultPersonaKey = settings.Value.Personas.FirstOrDefault(p => p.Value.IsDefault).Key ?? "";

        var segmentsWithPersonas = new List<CharacterSegment>();
        foreach (var seg in segments)
        {
            string? personaKey;
            if (seg.IsNarration && !string.IsNullOrEmpty(narratorPersonaKey))
                personaKey = narratorPersonaKey;
            else if (seg.IsThought && !string.IsNullOrEmpty(thoughtPersonaKey))
                personaKey = thoughtPersonaKey;
            else
                personaKey = string.IsNullOrEmpty(seg.CharacterName)
                    ? defaultPersonaKey
                    : allocator.AllocateForCharacter(seg.CharacterName, seg.Gender);

            segmentsWithPersonas.Add(seg with { PersonaKey = personaKey });
        }

        await player.PlaySegmentsAsync(segmentsWithPersonas, CancellationToken.None);

        Assert.Equal(segments.Count, synthesizedTexts.Count);
        Assert.NotEmpty(synthesizedVoices);
    }

    [Fact]
    public void CharacterAssignments_AreRestored()
    {
        var settings = CreateSettings();
        var allocator = new PersonaAllocator(settings, Mock.Of<ILogger<PersonaAllocator>>());

        var marinaKey = allocator.AllocateForCharacter("Marina", "F");
        var sofiaKey = allocator.AllocateForCharacter("Sofia", "F");

        Assert.Equal("female-2", marinaKey);
        Assert.Equal("female-8", sofiaKey);
    }

    [Fact]
    public void PersonaAllocator_NoAvailablePersona_FallsBackToDefault()
    {
        var settings = Options.Create(new AppSettings
        {
            OpenRouter = new OpenRouterSettings { TtsModel = "nonexistent-model" }
        });
        settings.Value.Personas["female-1"] = new VoicePersona { Voice = "marin", Gender = "F", IsDefault = true, Model = "openai/gpt-4o-mini-tts-2025-12-15" };

        var allocator = new PersonaAllocator(settings, Mock.Of<ILogger<PersonaAllocator>>());

        var key = allocator.AllocateForCharacter("NewChar", "F");
        Assert.Equal("female-1", key);
    }

    [Fact]
    public void ModifierMapping_ContainsAllModifiers()
    {
        var mapping = ParallelTtsPlayer.ModifierMapping;
        Assert.Contains("thirsty", mapping.Keys);
        Assert.Contains("flirtatious", mapping.Keys);
        Assert.Contains("whisper", mapping.Keys);
        Assert.NotEmpty(mapping["thirsty"]);
    }

    [Fact]
    public void Config_ThoughtAndNarratorPersonasAreSet()
    {
        var settings = CreateSettings();
        Assert.Equal("female-3", settings.Value.MultiCharacter.ThoughtPersona);
        Assert.Equal("female-9", settings.Value.MultiCharacter.NarratorPersona);
    }

    [Fact]
    public void Config_AllPersonasAreFemale()
    {
        var settings = CreateSettings();
        foreach (var kvp in settings.Value.Personas)
        {
            Assert.Equal("F", kvp.Value.Gender);
        }
    }

    private static AudioFormatConverter CreateConverter()
    {
        var opts = Options.Create(new AppSettings
        {
            Audio = new AudioSettings
            {
                PcmSampleRate = 24000,
                PcmChannels = 1,
                PcmBitsPerSample = 16
            }
        });
        return new AudioFormatConverter(opts, Mock.Of<ILogger<AudioFormatConverter>>());
    }
}
