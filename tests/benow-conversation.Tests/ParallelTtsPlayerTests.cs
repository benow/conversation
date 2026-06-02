using benow_conversation.Configuration;
using benow_conversation.Models;
using benow_conversation.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace benow_conversation.Tests;

public class ParallelTtsPlayerTests
{
    private static readonly AudioFormat TestSourceFormat = AudioFormat.Pcm24000Mono;

    private static AudioFormatConverter CreateConverter()
    {
        var settings = Options.Create(new AppSettings
        {
            Audio = new AudioSettings
            {
                PcmSampleRate = 24000,
                PcmChannels = 1,
                PcmBitsPerSample = 16
            }
        });
        return new AudioFormatConverter(settings, Mock.Of<ILogger<AudioFormatConverter>>());
    }

    private static Mock<ITtsProvider> CreateTtsMock()
    {
        var mock = new Mock<ITtsProvider>();
        mock.SetupGet(t => t.OutputFormat).Returns(TestSourceFormat);
        return mock;
    }

    private static IOptions<AppSettings> CreateSettings()
    {
        return Options.Create(new AppSettings());
    }

    private static VoicePersona MakePersona(string voice = "nova", string? instructions = null, string? thoughtInstructions = null, double? temperature = 0.7, string? model = "test-model")
    {
        return new VoicePersona
        {
            Voice = voice,
            OpenAiInstructions = instructions,
            ThoughtInstructions = thoughtInstructions,
            Temperature = temperature,
            Model = model ?? ""
        };
    }

    [Fact]
    public async Task SingleSegment_SynthesizesAndPlays()
    {
        var text = "Hello world";
        var expectedAudio = new byte[] { 0x01, 0x02, 0x03 };
        var audioStream = new MemoryStream(expectedAudio);

        var tts = CreateTtsMock();
        tts.Setup(t => t.SynthesizeAsync(It.IsAny<string>(), It.IsAny<string>(), "nova", It.IsAny<string>(), It.IsAny<double>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(audioStream);

        var persona = MakePersona(voice: "nova", instructions: "friendly and warm");
        var allocator = new Mock<IPersonaAllocator>();
        allocator.Setup(a => a.GetPersona("test-persona")).Returns(persona);

        var pipeline = new Mock<IPersistentAudioPipeline>();
        var audioPlayer = new Mock<IAudioPlayer>();

        var player = new ParallelTtsPlayer(
            tts.Object, pipeline.Object, audioPlayer.Object,
            allocator.Object, CreateConverter(), Mock.Of<ILogger<ParallelTtsPlayer>>());

        var segments = new List<CharacterSegment>
        {
            new() { SequenceIndex = 0, CharacterName = "TestChar", SpokenText = text, PersonaKey = "test-persona", Gender = "F" }
        };

        await player.PlaySegmentsAsync(segments, CancellationToken.None);

        pipeline.Verify(p => p.PipeAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()), Times.Once);
        tts.Verify(t => t.SynthesizeAsync(It.IsAny<string>(), It.IsAny<string>(), "nova", It.IsAny<string>(), It.IsAny<double>(), null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MultipleSegments_PlayInOrder()
    {
        var tts = CreateTtsMock();
        var callOrder = new List<string>();

        tts.Setup(t => t.SynthesizeAsync(It.IsAny<string>(), It.IsAny<string>(), "nova", It.IsAny<string>(), It.IsAny<double>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(new byte[] { 0x01 }))
            .Callback(() => callOrder.Add("Hello"));
        tts.Setup(t => t.SynthesizeAsync(It.IsAny<string>(), It.IsAny<string>(), "nova", It.IsAny<string>(), It.IsAny<double>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(new byte[] { 0x02 }))
            .Callback(() => callOrder.Add("World"));

        var persona = MakePersona();
        var allocator = new Mock<IPersonaAllocator>();
        allocator.Setup(a => a.GetPersona("p1")).Returns(persona);

        var pipeline = new Mock<IPersistentAudioPipeline>();
        var player = new ParallelTtsPlayer(
            tts.Object, pipeline.Object, Mock.Of<IAudioPlayer>(),
            allocator.Object, CreateConverter(), Mock.Of<ILogger<ParallelTtsPlayer>>());

        var segments = new List<CharacterSegment>
        {
            new() { SequenceIndex = 0, CharacterName = "A", SpokenText = "Hello", PersonaKey = "p1", Gender = "F" },
            new() { SequenceIndex = 1, CharacterName = "A", SpokenText = "World", PersonaKey = "p1", Gender = "F" }
        };

        await player.PlaySegmentsAsync(segments, CancellationToken.None);

        pipeline.Verify(p => p.PipeAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task ThoughtSegment_IncludesThoughtInstructions()
    {
        var text = "I wonder...";
        var actualInstructions = "";

        var tts = CreateTtsMock();
        tts.Setup(t => t.SynthesizeAsync(It.IsAny<string>(), It.IsAny<string>(), "nova", It.IsAny<string>(), It.IsAny<double>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(new byte[] { 0x01 }))
            .Callback<string, string, string, string?, double?, int?, CancellationToken>((t, v, i, temp, seed, m, ct) =>
            {
                actualInstructions = temp ?? "";
            });

        var persona = MakePersona(instructions: "friendly", thoughtInstructions: "speak contemplatively");
        var allocator = new Mock<IPersonaAllocator>();
        allocator.Setup(a => a.GetPersona("p1")).Returns(persona);

        var pipeline = new Mock<IPersistentAudioPipeline>();
        var player = new ParallelTtsPlayer(
            tts.Object, pipeline.Object, Mock.Of<IAudioPlayer>(),
            allocator.Object, CreateConverter(), Mock.Of<ILogger<ParallelTtsPlayer>>());

        var segments = new List<CharacterSegment>
        {
            new() { SequenceIndex = 0, CharacterName = "A", SpokenText = text, PersonaKey = "p1", Gender = "F", IsThought = true }
        };

        await player.PlaySegmentsAsync(segments, CancellationToken.None);

        Assert.Contains("speak contemplatively", actualInstructions);
    }

    [Fact]
    public async Task ModiferSegment_IncludesModifierInstructions()
    {
        var text = "Hello";
        var actualInstructions = "";

        var tts = CreateTtsMock();
        tts.Setup(t => t.SynthesizeAsync(It.IsAny<string>(), It.IsAny<string>(), "nova", It.IsAny<string>(), It.IsAny<double>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(new byte[] { 0x01 }))
            .Callback<string, string, string, string?, double?, int?, CancellationToken>((t, v, i, temp, seed, m, ct) =>
            {
                actualInstructions = temp ?? "";
            });

        var persona = MakePersona();
        var allocator = new Mock<IPersonaAllocator>();
        allocator.Setup(a => a.GetPersona("p1")).Returns(persona);

        var pipeline = new Mock<IPersistentAudioPipeline>();
        var player = new ParallelTtsPlayer(
            tts.Object, pipeline.Object, Mock.Of<IAudioPlayer>(),
            allocator.Object, CreateConverter(), Mock.Of<ILogger<ParallelTtsPlayer>>());

        var segments = new List<CharacterSegment>
        {
            new() { SequenceIndex = 0, CharacterName = "A", SpokenText = text, PersonaKey = "p1", Gender = "F", Modifier = "whisper" }
        };

        await player.PlaySegmentsAsync(segments, CancellationToken.None);

        Assert.Contains("hushed", actualInstructions);
    }

    [Fact]
    public async Task PersonaAllocation_UsesAllocatorWhenNoKey()
    {
        var tts = CreateTtsMock();
        tts.Setup(t => t.SynthesizeAsync(It.IsAny<string>(), It.IsAny<string>(), "nova", It.IsAny<string>(), It.IsAny<double>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(new byte[] { 0x01 }));

        var persona = MakePersona();
        var allocator = new Mock<IPersonaAllocator>();
        allocator.Setup(a => a.AllocateForCharacter("Alice", "F")).Returns("allocated-p1");
        allocator.Setup(a => a.GetPersona("allocated-p1")).Returns(persona);

        var pipeline = new Mock<IPersistentAudioPipeline>();
        var player = new ParallelTtsPlayer(
            tts.Object, pipeline.Object, Mock.Of<IAudioPlayer>(),
            allocator.Object, CreateConverter(), Mock.Of<ILogger<ParallelTtsPlayer>>());

        var segments = new List<CharacterSegment>
        {
            new() { SequenceIndex = 0, CharacterName = "Alice", SpokenText = "Hi", Gender = "F" }
        };

        await player.PlaySegmentsAsync(segments, CancellationToken.None);

        allocator.Verify(a => a.AllocateForCharacter("Alice", "F"), Times.Once);
        allocator.Verify(a => a.GetPersona("allocated-p1"), Times.Once);
    }

    [Fact]
    public async Task NullPersona_SkipsSegment()
    {
        var tts = CreateTtsMock();
        var allocator = new Mock<IPersonaAllocator>();
        allocator.Setup(a => a.AllocateForCharacter(It.IsAny<string>(), It.IsAny<string>())).Returns((string?)null);

        var pipeline = new Mock<IPersistentAudioPipeline>();
        var player = new ParallelTtsPlayer(
            tts.Object, pipeline.Object, Mock.Of<IAudioPlayer>(),
            allocator.Object, CreateConverter(), Mock.Of<ILogger<ParallelTtsPlayer>>());

        var segments = new List<CharacterSegment>
        {
            new() { SequenceIndex = 0, CharacterName = "Unknown", SpokenText = "Hi", Gender = "F" }
        };

        await player.PlaySegmentsAsync(segments, CancellationToken.None);

        tts.Verify(t => t.SynthesizeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double>(), null, It.IsAny<CancellationToken>()), Times.Never);
        pipeline.Verify(p => p.PipeAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CancelledToken_StopsPlayback()
    {
        var tts = CreateTtsMock();
        tts.Setup(t => t.SynthesizeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(new byte[] { 0x01 }));

        var persona = MakePersona();
        var allocator = new Mock<IPersonaAllocator>();
        allocator.Setup(a => a.GetPersona(It.IsAny<string>())).Returns(persona);

        using var cts = new CancellationTokenSource();

        var pipeline = new Mock<IPersistentAudioPipeline>();
        pipeline.Setup(p => p.PipeAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .Callback(() => cts.Cancel());

        var player = new ParallelTtsPlayer(
            tts.Object, pipeline.Object, Mock.Of<IAudioPlayer>(),
            allocator.Object, CreateConverter(), Mock.Of<ILogger<ParallelTtsPlayer>>());

        var segments = new List<CharacterSegment>
        {
            new() { SequenceIndex = 0, CharacterName = "A", SpokenText = "Text1", PersonaKey = "p1", Gender = "F" },
            new() { SequenceIndex = 1, CharacterName = "A", SpokenText = "Text2", PersonaKey = "p1", Gender = "F" }
        };

        await player.PlaySegmentsAsync(segments, cts.Token);

        pipeline.Verify(p => p.PipeAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()), Times.AtMost(1));
    }

    [Fact]
    public async Task Replay_NoPlayback_ReturnsSilently()
    {
        var player = new ParallelTtsPlayer(
            CreateTtsMock().Object, null, Mock.Of<IAudioPlayer>(),
            Mock.Of<IPersonaAllocator>(), CreateConverter(), Mock.Of<ILogger<ParallelTtsPlayer>>());

        await player.ReplayLastAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Replay_AfterPlayback_PlaysBufferedAudio()
    {
        var audioData = new byte[] { 0x01, 0x02, 0x03 };

        var tts = CreateTtsMock();
        tts.Setup(t => t.SynthesizeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(audioData));

        var persona = MakePersona();
        var allocator = new Mock<IPersonaAllocator>();
        allocator.Setup(a => a.GetPersona(It.IsAny<string>())).Returns(persona);

        var pipeline = new Mock<IPersistentAudioPipeline>();
        var player = new ParallelTtsPlayer(
            tts.Object, pipeline.Object, Mock.Of<IAudioPlayer>(),
            allocator.Object, CreateConverter(), Mock.Of<ILogger<ParallelTtsPlayer>>());

        var segments = new List<CharacterSegment>
        {
            new() { SequenceIndex = 0, CharacterName = "A", SpokenText = "Hi", PersonaKey = "p1", Gender = "F" }
        };

        await player.PlaySegmentsAsync(segments, CancellationToken.None);

        var audioPlayer = new Mock<IAudioPlayer>();
        var replayPlayer = new ParallelTtsPlayer(
            tts.Object, null, audioPlayer.Object,
            allocator.Object, CreateConverter(), Mock.Of<ILogger<ParallelTtsPlayer>>());

        await replayPlayer.ReplayLastAsync(CancellationToken.None);

        tts.Verify(t => t.SynthesizeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double>(), null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TemperatureJitter_ThoughtSegmentAddsHalfJitter()
    {
        var temps = new List<double>();

        var tts = CreateTtsMock();
        tts.Setup(t => t.SynthesizeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(new byte[] { 0x01 }))
            .Callback<string, string, string, string?, double?, int?, CancellationToken>((t, v, i, temp, seed, m, ct) =>
            {
                temps.Add(seed ?? 0);
            });

        var persona = MakePersona(temperature: 0.6);
        var allocator = new Mock<IPersonaAllocator>();
        allocator.Setup(a => a.GetPersona("p1")).Returns(persona);

        var pipeline = new Mock<IPersistentAudioPipeline>();
        var player = new ParallelTtsPlayer(
            tts.Object, pipeline.Object, Mock.Of<IAudioPlayer>(),
            allocator.Object, CreateConverter(), Mock.Of<ILogger<ParallelTtsPlayer>>());

        var segments = new List<CharacterSegment>
        {
            new() { SequenceIndex = 0, CharacterName = "A", SpokenText = "Think", PersonaKey = "p1", Gender = "F", IsThought = true }
        };

        await player.PlaySegmentsAsync(segments, CancellationToken.None);

        Assert.Single(temps);
        Assert.True(temps[0] >= 0.6, "Thought temp should be >= base");
    }

    [Fact]
    public async Task EmptySegments_DoesNothing()
    {
        var tts = CreateTtsMock();
        var player = new ParallelTtsPlayer(
            tts.Object, null, Mock.Of<IAudioPlayer>(),
            Mock.Of<IPersonaAllocator>(), CreateConverter(), Mock.Of<ILogger<ParallelTtsPlayer>>());

        await player.PlaySegmentsAsync(new List<CharacterSegment>(), CancellationToken.None);

        tts.Verify(t => t.SynthesizeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double>(), null, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UnknownModifier_SynthesizesWithoutModifierInstructions()
    {
        var actualInstructions = "";

        var tts = CreateTtsMock();
        tts.Setup(t => t.SynthesizeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(new byte[] { 0x01 }))
            .Callback<string, string, string, string?, double?, int?, CancellationToken>((t, v, i, temp, seed, m, ct) =>
            {
                actualInstructions = temp ?? "";
            });

        var persona = MakePersona(instructions: "base instructions");
        var allocator = new Mock<IPersonaAllocator>();
        allocator.Setup(a => a.GetPersona("p1")).Returns(persona);

        var pipeline = new Mock<IPersistentAudioPipeline>();
        var player = new ParallelTtsPlayer(
            tts.Object, pipeline.Object, Mock.Of<IAudioPlayer>(),
            allocator.Object, CreateConverter(), Mock.Of<ILogger<ParallelTtsPlayer>>());

        var segments = new List<CharacterSegment>
        {
            new() { SequenceIndex = 0, CharacterName = "A", SpokenText = "Hi", PersonaKey = "p1", Gender = "F", Modifier = "nonexistent" }
        };

        await player.PlaySegmentsAsync(segments, CancellationToken.None);

        Assert.Equal("base instructions", actualInstructions);
    }

    [Fact]
    public async Task AudioPlayerFallback_UsedWhenPipelineNull()
    {
        var audioData = new byte[] { 0x01, 0x02, 0x03 };

        var tts = CreateTtsMock();
        tts.Setup(t => t.SynthesizeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(audioData));

        var persona = MakePersona();
        var allocator = new Mock<IPersonaAllocator>();
        allocator.Setup(a => a.GetPersona("p1")).Returns(persona);

        var audioPlayer = new Mock<IAudioPlayer>();
        var player = new ParallelTtsPlayer(
            tts.Object, null, audioPlayer.Object,
            allocator.Object, CreateConverter(), Mock.Of<ILogger<ParallelTtsPlayer>>());

        var segments = new List<CharacterSegment>
        {
            new() { SequenceIndex = 0, CharacterName = "A", SpokenText = "Hi", PersonaKey = "p1", Gender = "F" }
        };

        await player.PlaySegmentsAsync(segments, CancellationToken.None);

        audioPlayer.Verify(p => p.PlayStreamAsync(It.IsAny<Stream>(), "pcm", null, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SynthFailure_SkipsSegment()
    {
        var tts = CreateTtsMock();
        tts.SetupSequence(t => t.SynthesizeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(new byte[] { 0x01 }))       // seq 0: success
            .ThrowsAsync(new Exception("TTS error"))                          // seq 1: attempt 1 fail
            .ThrowsAsync(new Exception("TTS error"))                          // seq 1: attempt 2 fail
            .ThrowsAsync(new Exception("TTS error"))                          // seq 1: attempt 3 fail (retries exhausted)
            .ReturnsAsync(() => new MemoryStream(new byte[] { 0x02 }));       // seq 2: success

        var persona = MakePersona();
        var allocator = new Mock<IPersonaAllocator>();
        allocator.Setup(a => a.GetPersona(It.IsAny<string>())).Returns(persona);

        var pipeline = new Mock<IPersistentAudioPipeline>();
        var player = new ParallelTtsPlayer(
            tts.Object, pipeline.Object, Mock.Of<IAudioPlayer>(),
            allocator.Object, CreateConverter(), Mock.Of<ILogger<ParallelTtsPlayer>>());

        var segments = new List<CharacterSegment>
        {
            new() { SequenceIndex = 0, CharacterName = "A", SpokenText = "Hello", PersonaKey = "p1", Gender = "F" },
            new() { SequenceIndex = 1, CharacterName = "A", SpokenText = "Fail", PersonaKey = "p1", Gender = "F" },
            new() { SequenceIndex = 2, CharacterName = "A", SpokenText = "World", PersonaKey = "p1", Gender = "F" }
        };

        await player.PlaySegmentsAsync(segments, CancellationToken.None);

        pipeline.Verify(p => p.PipeAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }
}
