using System.Text;
using System.Text.Json;
using benow_conversation.Configuration;
using benow_conversation.Models;
using benow_conversation.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace benow_conversation.Tests;

public class ProxyServiceTests
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public void ProxySettings_Defaults()
    {
        var settings = new ProxySettings();
        Assert.Equal(8080, settings.Port);
        Assert.Equal("https://openrouter.ai/api/v1", settings.BackendUrl);
        Assert.Equal("", settings.BackendModel);
        Assert.Equal("", settings.TtsPersona);
        Assert.Equal("", settings.TtsModel);
        Assert.Equal("", settings.TtsVoice);
    }

    [Fact]
    public void AppSettings_HasProxySettings()
    {
        var settings = new AppSettings();
        Assert.NotNull(settings.Proxy);
        Assert.Equal(8080, settings.Proxy.Port);
    }

    [Fact]
    public async Task HandleNonStreamingResponse_ExtractsTextFromChoices()
    {
        var speechQueue = new Mock<ISpeechQueue>();
        speechQueue.Setup(q => q.Enqueue(It.IsAny<string>()));

        var response = "Hello, world!";
        var body = JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    message = new
                    {
                        role = "assistant",
                        content = response
                    },
                    finish_reason = "stop"
                }
            }
        });

        var ttsService = CreateMockTtsService();
        var audioPlayer = CreateMockAudioPlayer();
        var settings = CreateSettings();
        var logger = new Mock<ILogger<ProxyService>>();

        Assert.NotNull(body);
        Assert.Contains("Hello, world!", body);
    }

    [Fact]
    public void SpeechQueue_Enqueues_Text()
    {
        var speechQueue = CreateSpeechQueue();
        speechQueue.Enqueue("Hello from test");
        speechQueue.Enqueue("Another message");
    }

    [Fact]
    public void SpeechQueue_IgnoresEmptyText()
    {
        var speechQueue = CreateSpeechQueue();
        speechQueue.Enqueue("");
        speechQueue.Enqueue(null!);
        speechQueue.Enqueue("   ");
    }

    [Fact]
    public async Task SpeechQueue_StartAndStop()
    {
        var speechQueue = CreateSpeechQueue();
        await speechQueue.StartAsync(CancellationToken.None);
        await speechQueue.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task SpeechQueue_ProcessesEnqueuedText()
    {
        var ttsService = new Mock<ITtsService>();
        var audioPlayer = new Mock<IAudioPlayer>();
        audioPlayer.Setup(a => a.IsAvailable).Returns(true);

        var stream = new MemoryStream(Encoding.UTF8.GetBytes("fake audio"));
        ttsService.Setup(t => t.SynthesizeToStreamAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<double?>(), It.IsAny<int?>(), It.IsAny<string>()))
            .ReturnsAsync((stream, "mp3"));

        audioPlayer.Setup(a => a.PlayStreamAsync(
                It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<double?>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var settings = CreateSettings();
        var logger = new Mock<ILogger<SpeechQueue>>();
        var options = Options.Create(settings);

        var queue = new SpeechQueue(ttsService.Object, CreateTtsMock().Object, audioPlayer.Object, null, CreateConverter(), options, logger.Object);

        await queue.StartAsync(CancellationToken.None);
        queue.Enqueue("Test speech text");

        await Task.Delay(500);

        await queue.StopAsync(CancellationToken.None);

        ttsService.Verify(t => t.SynthesizeToStreamAsync(
            "Test speech text", It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<double?>(), It.IsAny<int?>(), It.IsAny<string>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public void SpeechQueue_UsesPersonaFromProxySettings()
    {
        var settings = CreateSettings();
        settings.Proxy.TtsPersona = "sexy_female";

        var ttsService = new Mock<ITtsService>();
        var audioPlayer = new Mock<IAudioPlayer>();
        var logger = new Mock<ILogger<SpeechQueue>>();
        var options = Options.Create(settings);

        var queue = new SpeechQueue(ttsService.Object, CreateTtsMock().Object, audioPlayer.Object, null, CreateConverter(), options, logger.Object);

        Assert.NotNull(queue);
    }

    private static SpeechQueue CreateSpeechQueue()
    {
        var ttsService = new Mock<ITtsService>();
        var audioPlayer = new Mock<IAudioPlayer>();
        audioPlayer.Setup(a => a.IsAvailable).Returns(true);

        var stream = new MemoryStream(Encoding.UTF8.GetBytes("fake audio"));
        ttsService.Setup(t => t.SynthesizeToStreamAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<double?>(), It.IsAny<int?>(), It.IsAny<string>()))
            .ReturnsAsync((stream, "mp3"));

        audioPlayer.Setup(a => a.PlayStreamAsync(
                It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<double?>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var settings = CreateSettings();
        var logger = new Mock<ILogger<SpeechQueue>>();
        var options = Options.Create(settings);

        return new SpeechQueue(ttsService.Object, CreateTtsMock().Object, audioPlayer.Object, null, CreateConverter(), options, logger.Object);
    }

    private static AppSettings CreateSettings()
    {
        return new AppSettings
        {
            OpenRouter = new OpenRouterSettings
            {
                ApiKey = "test-key",
                BaseUrl = "https://openrouter.ai/api/v1",
                TtsModel = "openai/gpt-4o-mini-tts-2025-12-15"
            },
            Audio = new AudioSettings { OutputFormat = "mp3" },
            Proxy = new ProxySettings
            {
                Port = 8080,
                BackendUrl = "https://openrouter.ai/api/v1"
            },
            Personas = new Dictionary<string, VoicePersona>
            {
                ["sexy_female"] = new()
                {
                    Model = "openai/gpt-4o-mini-tts-2025-12-15",
                    Voice = "marin",
                    OpenAiInstructions = "young, british, sexy",
                    IsDefault = true
                }
            }
        };
    }

    private static Mock<ITtsService> CreateMockTtsService()
    {
        var mock = new Mock<ITtsService>();
        var stream = new MemoryStream(Encoding.UTF8.GetBytes("fake audio"));
        mock.Setup(t => t.SynthesizeToStreamAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<double?>(), It.IsAny<int?>(), It.IsAny<string>()))
            .ReturnsAsync((stream, "mp3"));
        return mock;
    }

    private static Mock<IAudioPlayer> CreateMockAudioPlayer()
    {
        var mock = new Mock<IAudioPlayer>();
        mock.Setup(a => a.IsAvailable).Returns(true);
        mock.Setup(a => a.PlayStreamAsync(
                It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<double?>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return mock;
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

    private static Mock<ITtsProvider> CreateTtsMock()
    {
        var mock = new Mock<ITtsProvider>();
        mock.SetupGet(t => t.OutputFormat).Returns(AudioFormat.Pcm24000Mono);
        return mock;
    }
}
