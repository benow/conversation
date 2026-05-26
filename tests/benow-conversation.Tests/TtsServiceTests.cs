using System.Net;
using System.Text;
using benow_conversation.Configuration;
using benow_conversation.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace benow_conversation.Tests;

public class TtsServiceTests : IDisposable
{
    private readonly string _tempDir;

    public TtsServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"tts_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    private (TtsService service, MockHttpMessageHandler handler, Mock<IAudioConverter> converterMock) CreateService(
        AppSettings? settings = null)
    {
        var appSettings = settings ?? new AppSettings
        {
            OpenRouter = new OpenRouterSettings
            {
                ApiKey = "test-key",
                BaseUrl = "https://openrouter.ai/api/v1"
            },
            Audio = new AudioSettings
            {
                OutputFormat = "mp3",
                OutputPath = _tempDir
            }
        };

        var options = Options.Create(appSettings);
        var handler = new MockHttpMessageHandler();
        var client = new HttpClient(handler);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("OpenRouter")).Returns(client);

        var logger = new Mock<ILogger<TtsService>>();
        var converter = new Mock<IAudioConverter>();
        converter.Setup(c => c.IsFfmpegAvailable()).Returns(true);
        converter.Setup(c => c.ConvertPcmToMp3Async(It.IsAny<byte[]>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync((byte[] pcm, int _, int _) => Encoding.UTF8.GetBytes("converted-mp3:" + pcm.Length));

        var service = new TtsService(factory.Object, options, logger.Object, converter.Object);

        return (service, handler, converter);
    }

    private static (TtsService service, MockHttpMessageHandler handler) CreateServiceSimple(
        AppSettings? settings = null)
    {
        var (service, handler, _) = CreateServiceStatic(settings);
        return (service, handler);
    }

    private static (TtsService service, MockHttpMessageHandler handler, Mock<IAudioConverter> converterMock) CreateServiceStatic(
        AppSettings? settings = null)
    {
        var appSettings = settings ?? new AppSettings
        {
            OpenRouter = new OpenRouterSettings
            {
                ApiKey = "test-key",
                BaseUrl = "https://openrouter.ai/api/v1"
            },
            Audio = new AudioSettings
            {
                OutputFormat = "mp3",
                OutputPath = Path.Combine(Path.GetTempPath(), $"tts_test_{Guid.NewGuid():N}")
            }
        };

        var options = Options.Create(appSettings);
        var handler = new MockHttpMessageHandler();
        var client = new HttpClient(handler);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("OpenRouter")).Returns(client);

        var logger = new Mock<ILogger<TtsService>>();
        var converter = new Mock<IAudioConverter>();
        converter.Setup(c => c.IsFfmpegAvailable()).Returns(true);
        converter.Setup(c => c.ConvertPcmToMp3Async(It.IsAny<byte[]>(), It.IsAny<int>(), It.IsAny<int>()))
            .ReturnsAsync((byte[] pcm, int _, int _) => Encoding.UTF8.GetBytes("converted-mp3:" + pcm.Length));

        var service = new TtsService(factory.Object, options, logger.Object, converter.Object);

        return (service, handler, converter);
    }

    private static void SetupAudioResponse(MockHttpMessageHandler handler, byte[] data)
    {
        handler.Response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(data)
        };
        handler.Response.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("audio/mpeg");
    }

    [Fact(Skip = "Requires API key")]
    public async Task SynthesizeToFileAsync_WithValidText_ReturnsFilePath()
    {
        var (service, _, _) = CreateService();
        var result = await service.SynthesizeToFileAsync("Hello world");
        Assert.True(File.Exists(result));
    }

    [Fact]
    public async Task SynthesizeToFileAsync_CreatesOutputDirectory_IfMissing()
    {
        var nestedDir = Path.Combine(_tempDir, "nested", "deep");
        var settings = new AppSettings
        {
            OpenRouter = new OpenRouterSettings
            {
                ApiKey = "test-key",
                BaseUrl = "https://openrouter.ai/api/v1"
            },
            Audio = new AudioSettings { OutputFormat = "mp3", OutputPath = nestedDir }
        };

        var (service, handler, _) = CreateService(settings);
        SetupAudioResponse(handler, Encoding.UTF8.GetBytes("fake-audio"));

        var result = await service.SynthesizeToFileAsync("test");
        Assert.True(Directory.Exists(nestedDir));
        Assert.True(File.Exists(result));
    }

    [Fact]
    public async Task SynthesizeToFileAsync_GeneratesTimestampedFilename()
    {
        var (service, handler, _) = CreateService();
        SetupAudioResponse(handler, Encoding.UTF8.GetBytes("fake-audio"));

        var result = await service.SynthesizeToFileAsync("test");
        var filename = Path.GetFileName(result);

        Assert.Matches(@"^\d{8}-\d{6}\.mp3$", filename);
    }

    [Fact]
    public async Task SynthesizeToFileAsync_UsesCustomOutputFileName()
    {
        var (service, handler, _) = CreateService();
        SetupAudioResponse(handler, Encoding.UTF8.GetBytes("fake-audio"));

        var result = await service.SynthesizeToFileAsync("test", "custom.mp3");
        Assert.EndsWith("custom.mp3", result);
    }

    [Fact]
    public async Task SynthesizeToFileAsync_UsesSpecifiedVoice()
    {
        var (service, handler, _) = CreateService();
        SetupAudioResponse(handler, Encoding.UTF8.GetBytes("fake-audio"));

        var result = await service.SynthesizeToFileAsync("test", voice: "nova");
        Assert.True(File.Exists(result));

        var body = handler.LastRequestBody;
        Assert.Contains("\"nova\"", body);
    }

    [Fact]
    public async Task SynthesizeToFileAsync_IncludesInstructionsInRequest()
    {
        var (service, handler, _) = CreateService();
        SetupAudioResponse(handler, Encoding.UTF8.GetBytes("fake-audio"));

        await service.SynthesizeToFileAsync("test", instructions: "Speak in a warm, friendly tone");

        var body = handler.LastRequestBody;
        Assert.Contains("warm, friendly tone", body);
        Assert.Contains("\"provider\"", body);
    }

    [Fact]
    public async Task SynthesizeToFileAsync_RetriesWithPcmOnFormatError_AndConverts()
    {
        var (service, handler, converterMock) = CreateService();

        var formatErrorBody = "{\"error\":{\"message\":\"Gemini TTS only supports response_format=\\\"pcm\\\". Got \\\"mp3\\\".\"}}";
        handler.SetSequence(
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(formatErrorBody, Encoding.UTF8, "application/json")
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("raw-pcm-data"))
                { Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/pcm") } }
            }
        );

        var result = await service.SynthesizeToFileAsync("test", "format-test.mp3");
        Assert.True(File.Exists(result));
        Assert.EndsWith(".mp3", result);

        converterMock.Verify(c => c.ConvertPcmToMp3Async(It.IsAny<byte[]>(), It.IsAny<int>(), It.IsAny<int>()), Times.Once);
    }

    [Fact]
    public async Task SynthesizeToFileAsync_RetriesWithPcm_SavesAsPcm_WhenNoFfmpeg()
    {
        var settings = new AppSettings
        {
            OpenRouter = new OpenRouterSettings
            {
                ApiKey = "test-key",
                BaseUrl = "https://openrouter.ai/api/v1"
            },
            Audio = new AudioSettings { OutputFormat = "mp3", OutputPath = _tempDir }
        };

        var options = Options.Create(settings);
        var handler = new MockHttpMessageHandler();
        var client = new HttpClient(handler);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("OpenRouter")).Returns(client);

        var logger = new Mock<ILogger<TtsService>>();
        var converter = new Mock<IAudioConverter>();
        converter.Setup(c => c.IsFfmpegAvailable()).Returns(false);
        converter.Setup(c => c.ConvertPcmToMp3Async(It.IsAny<byte[]>(), It.IsAny<int>(), It.IsAny<int>()))
            .ThrowsAsync(new InvalidOperationException("ffmpeg is not available"));

        var svc = new TtsService(factory.Object, options, logger.Object, converter.Object);

        var formatErrorBody = "{\"error\":{\"message\":\"response_format not supported. Got \\\"mp3\\\".\"}}";
        handler.SetSequence(
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(formatErrorBody, Encoding.UTF8, "application/json")
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("raw-pcm-data"))
                { Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/pcm") } }
            }
        );

        var result = await svc.SynthesizeToFileAsync("test", "no-ffmpeg.mp3");
        Assert.True(File.Exists(result));
        Assert.EndsWith(".pcm", result);

        var content = await File.ReadAllTextAsync(result);
        Assert.Equal("raw-pcm-data", content);
    }

    [Fact]
    public async Task SynthesizeAllProvidersAsync_RetriesFormatErrors_PerVoice()
    {
        var (service, handler, converterMock) = CreateService();
        var modelsJson = """
            {"data":[
                {"id":"google/gemini-tts","supported_voices":["voice-a","voice-b"]}
            ]}
            """;
        var formatErrorBody = "{\"error\":{\"message\":\"only supports response_format=\\\"pcm\\\". Got \\\"mp3\\\".\"}}";

        var pcmResponse = new Func<byte[], HttpResponseMessage>(data => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(data)
            { Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/pcm") } }
        });

        handler.SetSequence(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(modelsJson, Encoding.UTF8, "application/json")
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(modelsJson, Encoding.UTF8, "application/json")
            },
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(formatErrorBody, Encoding.UTF8, "application/json")
            },
            pcmResponse(Encoding.UTF8.GetBytes("pcm-voice-a")),
            pcmResponse(Encoding.UTF8.GetBytes("pcm-voice-b"))
        );

        var results = await service.SynthesizeAllProvidersAsync("test", "batch");
        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.True(File.Exists(r)));
        Assert.All(results, r => Assert.EndsWith(".mp3", r));

        converterMock.Verify(c => c.ConvertPcmToMp3Async(It.IsAny<byte[]>(), It.IsAny<int>(), It.IsAny<int>()), Times.Exactly(2));
    }

    [Fact]
    public async Task SynthesizeAllModelsAsync_GeneratesOneFilePerModel_WithFirstVoice()
    {
        var (service, handler, _) = CreateService();
        var modelsJson = """
            {"data":[
                {"id":"openai/gpt-4o-mini-tts","supported_voices":["alloy","nova"]},
                {"id":"google/gemini-tts","supported_voices":["voice-a"]}
            ]}
            """;
        handler.SetSequence(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(modelsJson, Encoding.UTF8, "application/json")
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(modelsJson, Encoding.UTF8, "application/json")
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("audio-google"))
                { Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/mpeg") } }
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(modelsJson, Encoding.UTF8, "application/json")
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("audio-openai"))
                { Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/mpeg") } }
            }
        );

        var results = await service.SynthesizeAllModelsAsync("test", "sample");
        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.True(File.Exists(r)));

        var filenames = results.Select(Path.GetFileName).OrderBy(f => f).ToList();
        Assert.Equal("sample_google_gemini-tts.mp3", filenames[0]);
        Assert.Equal("sample_openai_gpt-4o-mini-tts.mp3", filenames[1]);
    }

    [Fact]
    public async Task SynthesizeAllModelsAsync_UsesExplicitVoice_WhenProvided()
    {
        var (service, handler, _) = CreateService();
        var modelsJson = """
            {"data":[
                {"id":"openai/gpt-4o-mini-tts","supported_voices":["alloy","nova"]}
            ]}
            """;
        handler.SetSequence(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(modelsJson, Encoding.UTF8, "application/json")
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("audio-nova"))
                { Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/mpeg") } }
            }
        );

        await service.SynthesizeAllModelsAsync("test", "sample", voice: "nova");

        var body = handler.LastRequestBody;
        Assert.Contains("\"nova\"", body);
    }

    [Fact]
    public async Task SynthesizeAllProvidersAsync_GeneratesAllVoicesForAllModels()
    {
        var (service, handler, _) = CreateService();
        var modelsJson = """
            {"data":[
                {"id":"openai/gpt-4o-mini-tts","supported_voices":["alloy","nova"]},
                {"id":"google/gemini-tts","supported_voices":["achernar"]}
            ]}
            """;
        handler.SetSequence(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(modelsJson, Encoding.UTF8, "application/json")
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(modelsJson, Encoding.UTF8, "application/json")
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("audio-alloy"))
                { Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/mpeg") } }
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("audio-nova"))
                { Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/mpeg") } }
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(modelsJson, Encoding.UTF8, "application/json")
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("audio-achernar"))
                { Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/mpeg") } }
            }
        );

        var results = await service.SynthesizeAllProvidersAsync("test", "sample");
        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.True(File.Exists(r)));

        var filenames = results.Select(Path.GetFileName).OrderBy(f => f).ToList();
        Assert.Equal("sample_google_gemini-tts_achernar.mp3", filenames[0]);
        Assert.Equal("sample_openai_gpt-4o-mini-tts_alloy.mp3", filenames[1]);
        Assert.Equal("sample_openai_gpt-4o-mini-tts_nova.mp3", filenames[2]);
    }

    [Fact]
    public async Task SynthesizeAllVoicesAsync_GeneratesAllFiles()
    {
        var (service, handler, _) = CreateService();
        var modelsJson = """
            {"data":[{"id":"openai/gpt-4o-mini-tts-2025-12-15","supported_voices":["alloy","nova","echo"]}]}
            """;
        handler.SetSequence(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(modelsJson, Encoding.UTF8, "application/json")
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("audio-alloy"))
                { Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/mpeg") } }
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("audio-nova"))
                { Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/mpeg") } }
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("audio-echo"))
                { Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/mpeg") } }
            }
        );

        var results = await service.SynthesizeAllVoicesAsync("test", "sample");
        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.True(File.Exists(r)));

        var filenames = results.Select(Path.GetFileName).OrderBy(f => f).ToList();
        Assert.Equal("sample_alloy.mp3", filenames[0]);
        Assert.Equal("sample_echo.mp3", filenames[1]);
        Assert.Equal("sample_nova.mp3", filenames[2]);
    }

    [Fact]
    public async Task SynthesizeToFileAsync_ThrowsOnNon2xx_WithActionableMessage()
    {
        var (service, handler, _) = CreateService();
        handler.Response = new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("{\"error\":{\"message\":\"Server error\"}}", Encoding.UTF8, "application/json")
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SynthesizeToFileAsync("test"));

        Assert.Contains("HTTP 500", ex.Message);
        Assert.Contains("Server error", ex.Message);
    }

    [Fact]
    public async Task SynthesizeToFileAsync_ThrowsOnMissingApiKey_WithConfigGuidance()
    {
        var settings = new AppSettings
        {
            OpenRouter = new OpenRouterSettings { ApiKey = "" },
            Audio = new AudioSettings()
        };

        var (service, _, _) = CreateService(settings);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SynthesizeToFileAsync("test"));

        Assert.Contains("appsettings.Development.json", ex.Message);
    }

    [Fact]
    public async Task SynthesizeToFileAsync_ThrowsOn401_WithAuthGuidance()
    {
        var (service, handler, _) = CreateService();
        handler.Response = new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("Unauthorized")
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SynthesizeToFileAsync("test"));

        Assert.Contains("invalid or unauthorized", ex.Message);
    }

    [Fact]
    public async Task SynthesizeToFileAsync_ThrowsOn429_WithRateLimitMessage()
    {
        var (service, handler, _) = CreateService();
        handler.Response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("Rate limited")
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SynthesizeToFileAsync("test"));

        Assert.Contains("rate limit", ex.Message.ToLower());
    }

    [Fact]
    public async Task SynthesizeToFileAsync_ThrowsOnEmptyResponse()
    {
        var (service, handler, _) = CreateService();
        handler.Response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([])
        };
        handler.Response.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("audio/mpeg");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SynthesizeToFileAsync("test"));

        Assert.Contains("empty response", ex.Message.ToLower());
    }

    [Fact]
    public async Task SynthesizeToStreamAsync_ReturnsAudioStream()
    {
        var (service, handler, _) = CreateService();
        var audioData = Encoding.UTF8.GetBytes("streaming-audio-data");
        handler.Response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(audioData)
        };
        handler.Response.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("audio/mpeg");

        var (stream, format) = await service.SynthesizeToStreamAsync("test", voice: "alloy");
        Assert.NotNull(stream);
        Assert.Equal("mp3", format);

        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        var result = ms.ToArray();
        Assert.Equal(audioData, result);
    }

    private class MockHttpMessageHandler : DelegatingHandler
    {
        private readonly Queue<HttpResponseMessage> _responseQueue = new();
        private string? _lastRequestBody;

        public HttpResponseMessage Response
        {
            get => _responseQueue.TryPeek(out var r) ? r : new HttpResponseMessage(HttpStatusCode.OK);
            set { _responseQueue.Clear(); _responseQueue.Enqueue(value); }
        }

        public string LastRequestBody => _lastRequestBody ?? "";

        public void SetSequence(params HttpResponseMessage[] responses)
        {
            _responseQueue.Clear();
            foreach (var r in responses) _responseQueue.Enqueue(r);
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content != null)
                _lastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);

            return _responseQueue.Count > 0
                ? _responseQueue.Dequeue()
                : new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
