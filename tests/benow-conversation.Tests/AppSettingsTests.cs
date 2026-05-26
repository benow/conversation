using benow_conversation.Configuration;
using Microsoft.Extensions.Configuration;

namespace benow_conversation.Tests;

public class AppSettingsTests
{
    [Fact]
    public void AppSettings_LoadsFromJson()
    {
        var config = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes("""
                {
                  "OpenRouter": {
                    "ApiKey": "test-key",
                    "BaseUrl": "https://openrouter.ai/api/v1",
                    "TtsModel": "openai/gpt-4o-mini-tts-2025-12-15"
                  },
                  "Audio": {
                    "OutputFormat": "mp3",
                    "OutputPath": "output"
                  }
                }
                """)))
            .Build();

        var settings = new AppSettings();
        config.GetSection("OpenRouter").Bind(settings.OpenRouter);
        config.GetSection("Audio").Bind(settings.Audio);

        Assert.Equal("test-key", settings.OpenRouter.ApiKey);
        Assert.Equal("https://openrouter.ai/api/v1", settings.OpenRouter.BaseUrl);
        Assert.Equal("openai/gpt-4o-mini-tts-2025-12-15", settings.OpenRouter.TtsModel);
        Assert.Equal("mp3", settings.Audio.OutputFormat);
        Assert.Equal("output", settings.Audio.OutputPath);
    }

    [Fact]
    public void AppSettings_HasDefaults()
    {
        var settings = new AppSettings();

        Assert.Equal("", settings.OpenRouter.ApiKey);
        Assert.Equal("https://openrouter.ai/api/v1", settings.OpenRouter.BaseUrl);
        Assert.Equal("openai/gpt-4o-mini-tts-2025-12-15", settings.OpenRouter.TtsModel);
        Assert.Equal("mp3", settings.Audio.OutputFormat);
        Assert.Equal("output", settings.Audio.OutputPath);
    }
}
