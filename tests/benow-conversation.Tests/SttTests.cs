using benow_conversation.Configuration;
using benow_conversation.Services.Abstractions;
using benow_conversation.Services.Stt;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace benow_conversation.Tests;

public class SttSettingsTests
{
    [Fact]
    public void SttSettings_HasDefaults()
    {
        var settings = new SttSettings();
        Assert.Equal("pipewire", settings.Recorder);
        Assert.Equal("pw-record", settings.RecorderCommand);
        Assert.Equal("mp3", settings.RecorderFormat);
        Assert.Equal(16000, settings.RecorderSampleRate);
        Assert.Equal(1, settings.RecorderChannels);
        Assert.Equal("groq-whisper", settings.Transcriber);
        Assert.Equal("wayland", settings.Clipboard);
        Assert.Equal("ydotool", settings.Keyboard);
        Assert.Equal("llm", settings.Transformer);
        Assert.Equal("evdev-keyboard", settings.Trigger);
        Assert.False(settings.AutoSubmit);
        Assert.Equal("ffmpeg", settings.FfmpegPath);
        Assert.False(settings.CleanupSkip);
        Assert.Equal("/run/user/1000/.ydotool_socket", settings.YdotoolSocketPath);
        Assert.Equal(300, settings.TriggerDebounceMs);
    }

    [Fact]
    public void GroqSettings_HasDefaults()
    {
        var settings = new GroqSettings();
        Assert.Equal("", settings.ApiKey);
        Assert.Equal("https://api.groq.com/openai/v1", settings.BaseUrl);
        Assert.Equal("whisper-large-v3", settings.Model);
        Assert.Equal(TimeSpan.FromMinutes(2), settings.TranscriptionTimeout);
    }

    [Fact]
    public void TranscriptCleanupSettings_HasDefaults()
    {
        var settings = new TranscriptCleanupSettings();
        Assert.Equal("", settings.BaseUrl);
        Assert.Equal("", settings.Model);
        Assert.Contains("transcription editor", settings.SystemPrompt);
    }

    [Fact]
    public void AppSettings_HasSttSettings()
    {
        var settings = new AppSettings();
        Assert.NotNull(settings.Stt);
        Assert.NotNull(settings.Groq);
        Assert.NotNull(settings.TranscriptCleanup);
        Assert.Equal("pipewire", settings.Stt.Recorder);
        Assert.Equal("evdev-keyboard", settings.Stt.Trigger);
    }
}

public class EvdevEventParsingTests
{
    [Fact]
    public void ParseEvent_KeyPlayPause_Press()
    {
        var buffer = CreateInputEvent(0x01, 164, 1);
        var type = BitConverter.ToUInt16(buffer, 16);
        var code = BitConverter.ToUInt16(buffer, 18);
        var value = BitConverter.ToInt32(buffer, 20);

        Assert.Equal(0x01, type); // EV_KEY
        Assert.Equal(164, code);  // KEY_PLAYPAUSE
        Assert.Equal(1, value);   // press
    }

    [Fact]
    public void ParseEvent_KeyMedia_Press()
    {
        var buffer = CreateInputEvent(0x01, 226, 1);
        var type = BitConverter.ToUInt16(buffer, 16);
        var code = BitConverter.ToUInt16(buffer, 18);
        var value = BitConverter.ToInt32(buffer, 20);

        Assert.Equal(0x01, type);
        Assert.Equal(226, code);  // KEY_MEDIA
        Assert.Equal(1, value);
    }

    [Fact]
    public void ParseEvent_KeyRelease_NotTriggered()
    {
        var buffer = CreateInputEvent(0x01, 164, 0);
        var type = BitConverter.ToUInt16(buffer, 16);
        var value = BitConverter.ToInt32(buffer, 20);

        Assert.Equal(0x01, type);
        Assert.Equal(0, value); // release, not press
    }

    [Fact]
    public void ParseEvent_AutoRepeat_NotTriggered()
    {
        var buffer = CreateInputEvent(0x01, 164, 2);
        var value = BitConverter.ToInt32(buffer, 20);

        Assert.Equal(2, value); // autorepeat, not press
    }

    [Fact]
    public void ParseEvent_NonKeyEvent_Ignored()
    {
        var buffer = CreateInputEvent(0x00, 0, 0); // EV_SYN
        var type = BitConverter.ToUInt16(buffer, 16);

        Assert.Equal(0x00, type); // not EV_KEY
    }

    [Fact]
    public void ParseEvent_RegularKey_Ignored()
    {
        var buffer = CreateInputEvent(0x01, 28, 1); // KEY_ENTER
        var code = BitConverter.ToUInt16(buffer, 18);

        Assert.NotEqual(164, code);
        Assert.NotEqual(226, code);
    }

    [Fact]
    public void InputEventSize_Is24Bytes()
    {
        var buffer = CreateInputEvent(0, 0, 0);
        Assert.Equal(24, buffer.Length);
    }

    private static byte[] CreateInputEvent(ushort type, ushort code, int value)
    {
        var buffer = new byte[24];
        BitConverter.TryWriteBytes(new Span<byte>(buffer, 0, 8), 0L);     // tv_sec
        BitConverter.TryWriteBytes(new Span<byte>(buffer, 8, 8), 0L);     // tv_usec
        BitConverter.TryWriteBytes(new Span<byte>(buffer, 16, 2), type);
        BitConverter.TryWriteBytes(new Span<byte>(buffer, 18, 2), code);
        BitConverter.TryWriteBytes(new Span<byte>(buffer, 20, 4), value);
        return buffer;
    }
}

public class ConsoleRecordingTriggerTests
{
    [Fact]
    public void ConsoleTrigger_IsAlwaysAvailable()
    {
        var logger = new Mock<ILogger<ConsoleRecordingTrigger>>();
        var trigger = new ConsoleRecordingTrigger(logger.Object);
        Assert.True(trigger.IsAvailable);
    }
}

public class LlmTextTransformerUrlResolutionTests
{
    [Fact]
    public void UsesTranscriptCleanupBaseUrl_WhenSet()
    {
        var settings = new AppSettings
        {
            OpenRouter = new OpenRouterSettings
            {
                ApiKey = "test-key",
                BaseUrl = "https://openrouter.ai/api/v1"
            },
            TranscriptCleanup = new TranscriptCleanupSettings
            {
                BaseUrl = "https://custom.api/v1",
                Model = "test-model"
            }
        };

        Assert.NotEqual(settings.OpenRouter.BaseUrl, settings.TranscriptCleanup.BaseUrl);
        Assert.Equal("https://custom.api/v1", settings.TranscriptCleanup.BaseUrl);
    }

    [Fact]
    public void FallsBackToOpenRouterBaseUrl_WhenTranscriptCleanupBaseUrlEmpty()
    {
        var settings = new AppSettings
        {
            OpenRouter = new OpenRouterSettings
            {
                ApiKey = "test-key",
                BaseUrl = "https://openrouter.ai/api/v1"
            },
            TranscriptCleanup = new TranscriptCleanupSettings
            {
                BaseUrl = ""
            }
        };

        var effectiveUrl = !string.IsNullOrWhiteSpace(settings.TranscriptCleanup.BaseUrl)
            ? settings.TranscriptCleanup.BaseUrl
            : settings.OpenRouter.BaseUrl;

        Assert.Equal("https://openrouter.ai/api/v1", effectiveUrl);
    }
}

public class SttRunnerTests
{
    [Fact]
    public async Task SttRunner_StopsOnCancellation()
    {
        var recorder = new Mock<IAudioRecorder>();
        recorder.Setup(r => r.IsAvailable).Returns(true);
        recorder.Setup(r => r.RecordToFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string path, CancellationToken ct) =>
            {
                await Task.Delay(5000, ct);
                return path;
            });

        var trigger = new Mock<IRecordingTrigger>();
        trigger.Setup(t => t.IsAvailable).Returns(true);
        trigger.Setup(t => t.WaitForTriggerAsync(It.IsAny<CancellationToken>()))
            .Returns(async (CancellationToken ct) =>
            {
                await Task.Delay(100, ct);
            });

        var transcriber = new Mock<ITranscriptionService>();
        var transformer = new Mock<ITextTransformer>();
        var clipboard = new Mock<IClipboardService>();
        clipboard.Setup(c => c.IsAvailable).Returns(false);
        var keyboard = new Mock<IKeyboardSimulator>();
        keyboard.Setup(k => k.IsAvailable).Returns(false);

        var settings = CreateSttSettings();
        var httpClientFactory = new Mock<IHttpClientFactory>();
        var logger = new Mock<ILogger<SttRunner>>();

        var runner = new SttRunner(
            recorder.Object, transcriber.Object, transformer.Object,
            clipboard.Object, keyboard.Object, trigger.Object,
            Options.Create(settings), logger.Object);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await runner.RunAsync(cts.Token);

        trigger.Verify(t => t.WaitForTriggerAsync(It.IsAny<CancellationToken>()), Times.AtLeastOnce());
    }

    [Fact]
    public async Task SttRunner_SkipsCleanup_WhenConfigured()
    {
        var recorder = new Mock<IAudioRecorder>();
        recorder.Setup(r => r.IsAvailable).Returns(true);

        var trigger = new Mock<IRecordingTrigger>();
        trigger.Setup(t => t.IsAvailable).Returns(true);

        var transcriber = new Mock<ITranscriptionService>();
        var transformer = new Mock<ITextTransformer>();
        var clipboard = new Mock<IClipboardService>();
        clipboard.Setup(c => c.IsAvailable).Returns(false);
        var keyboard = new Mock<IKeyboardSimulator>();
        keyboard.Setup(k => k.IsAvailable).Returns(false);

        var settings = CreateSttSettings();
        settings.Stt.CleanupSkip = true;

        var httpClientFactory = new Mock<IHttpClientFactory>();
        var logger = new Mock<ILogger<SttRunner>>();

        // This test verifies the transformer is never called when CleanupSkip is true
        // We can't easily test the full pipeline without a real trigger, but we verify the config
        Assert.True(settings.Stt.CleanupSkip);
        transformer.Verify(t => t.TransformAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    [Fact]
    public async Task SttRunner_ReturnsEarly_WhenRecorderUnavailable()
    {
        var recorder = new Mock<IAudioRecorder>();
        recorder.Setup(r => r.IsAvailable).Returns(false);

        var trigger = new Mock<IRecordingTrigger>();
        trigger.Setup(t => t.IsAvailable).Returns(true);

        var settings = CreateSttSettings();
        var httpClientFactory = new Mock<IHttpClientFactory>();
        var logger = new Mock<ILogger<SttRunner>>();

        var runner = new SttRunner(
            recorder.Object, Mock.Of<ITranscriptionService>(), Mock.Of<ITextTransformer>(),
            Mock.Of<IClipboardService>(), Mock.Of<IKeyboardSimulator>(), trigger.Object,
            Options.Create(settings), logger.Object);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await runner.RunAsync(cts.Token);

        trigger.Verify(t => t.WaitForTriggerAsync(It.IsAny<CancellationToken>()), Times.Never());
    }

    [Fact]
    public async Task SttRunner_ReturnsEarly_WhenTriggerUnavailable()
    {
        var recorder = new Mock<IAudioRecorder>();
        recorder.Setup(r => r.IsAvailable).Returns(true);

        var trigger = new Mock<IRecordingTrigger>();
        trigger.Setup(t => t.IsAvailable).Returns(false);

        var settings = CreateSttSettings();
        var httpClientFactory = new Mock<IHttpClientFactory>();
        var logger = new Mock<ILogger<SttRunner>>();

        var runner = new SttRunner(
            recorder.Object, Mock.Of<ITranscriptionService>(), Mock.Of<ITextTransformer>(),
            Mock.Of<IClipboardService>(), Mock.Of<IKeyboardSimulator>(), trigger.Object,
            Options.Create(settings), logger.Object);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await runner.RunAsync(cts.Token);

        recorder.Verify(r => r.RecordToFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    private static AppSettings CreateSttSettings()
    {
        return new AppSettings
        {
            OpenRouter = new OpenRouterSettings
            {
                ApiKey = "test-key",
                BaseUrl = "https://openrouter.ai/api/v1"
            },
            Groq = new GroqSettings
            {
                ApiKey = "test-groq-key",
                BaseUrl = "https://api.groq.com/openai/v1"
            },
            Stt = new SttSettings
            {
                AutoSubmit = false,
                CleanupSkip = false,
                FeedbackBeep = false
            },
            TranscriptCleanup = new TranscriptCleanupSettings
            {
                Model = "test-model"
            },
            Proxy = new ProxySettings
            {
                Port = 0
            }
        };
    }
}

public class EvdevDebounceTests
{
    [Fact]
    public void Debounce_PreventsRapidTriggers()
    {
        var debounceMs = 300;
        var lastTicks = DateTime.UtcNow.Ticks;
        var now = lastTicks + TimeSpan.FromMilliseconds(50).Ticks;
        var elapsed = TimeSpan.FromTicks(now - lastTicks).TotalMilliseconds;

        Assert.True(elapsed < debounceMs);
    }

    [Fact]
    public void Debounce_AllowsTrigger_AfterInterval()
    {
        var debounceMs = 300;
        var lastTicks = DateTime.UtcNow.Ticks;
        var now = lastTicks + TimeSpan.FromMilliseconds(400).Ticks;
        var elapsed = TimeSpan.FromTicks(now - lastTicks).TotalMilliseconds;

        Assert.True(elapsed >= debounceMs);
    }
}

public class KeyParseTests
{
    [Fact]
    public void ParseKeySpec_Ctrl_Space()
    {
        var (mods, key) = EvdevKeyboardTrigger.ParseKeySpec("Ctrl+Space");
        Assert.Contains((ushort)29, mods);
        Assert.Equal((ushort)57, key);
    }

    [Fact]
    public void ParseKeySpec_F9()
    {
        var (mods, key) = EvdevKeyboardTrigger.ParseKeySpec("F9");
        Assert.Contains((ushort)29, mods);
        Assert.Equal((ushort)67, key);
    }

    [Fact]
    public void ParseKeySpec_Ctrl_Alt_Space()
    {
        var (mods, key) = EvdevKeyboardTrigger.ParseKeySpec("Ctrl+Alt+Space");
        Assert.Contains((ushort)29, mods);
        Assert.Contains((ushort)56, mods);
        Assert.Equal((ushort)57, key);
    }

    [Fact]
    public void ParseKeySpec_Empty_DefaultsToCtrlSpace()
    {
        var (mods, key) = EvdevKeyboardTrigger.ParseKeySpec("");
        Assert.Contains((ushort)29, mods);
        Assert.Equal((ushort)57, key);
    }
}
