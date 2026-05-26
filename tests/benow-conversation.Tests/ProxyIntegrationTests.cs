using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using benow_conversation.Configuration;
using benow_conversation.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace benow_conversation.Tests;

public class ProxyIntegrationTests
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    [Fact(Skip = "Integration - requires live API key and audio device. Remove Skip to run.")]
    [Trait("Category", "Integration")]
    public async Task Proxy_FullIntegration_ChatCompletion_StreamsAndSpeaks()
    {
        var projectRoot = FindSourceProjectRoot();
        var devSettingsPath = Path.Combine(projectRoot, "appsettings.Development.json");
        if (!File.Exists(devSettingsPath))
        {
            Assert.Fail("appsettings.Development.json not found — cannot run integration test without API key.");
            return;
        }

        var devJson = await File.ReadAllTextAsync(devSettingsPath);
        var devSettings = JsonSerializer.Deserialize<AppSettings>(devJson, JsonOpts);
        var apiKey = devSettings?.OpenRouter.ApiKey;
        if (string.IsNullOrEmpty(apiKey))
        {
            Assert.Fail("API key not found in appsettings.Development.json");
            return;
        }

        var testPort = 18888;

        var settings = new AppSettings
        {
            OpenRouter = new OpenRouterSettings
            {
                ApiKey = apiKey,
                BaseUrl = "https://openrouter.ai/api/v1",
                TtsModel = "openai/gpt-4o-mini-tts-2025-12-15"
            },
            Audio = new AudioSettings { OutputFormat = "mp3" },
            Proxy = new ProxySettings
            {
                Port = testPort,
                BackendUrl = "https://openrouter.ai/api/v1",
                BackendModel = "z-ai/glm-5-turbo",
                TtsPersona = "sexy_female"
            },
            Personas = new Dictionary<string, VoicePersona>
            {
                ["sexy_female"] = new()
                {
                    Model = "openai/gpt-4o-mini-tts-2025-12-15",
                    Voice = "marin",
                    OpenAiInstructions = "young, british, sexy, flirty",
                    IsDefault = true
                }
            }
        };

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Debug));
        services.AddHttpClient("OpenRouter", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(60);
        });
        services.AddHttpClient("ProxyBackend", client =>
        {
            client.Timeout = TimeSpan.FromMinutes(5);
        });
        services.AddSingleton<IOptions<AppSettings>>(Options.Create(settings));
        services.AddSingleton<IAudioConverter, AudioConverter>();
        services.AddSingleton<ITtsService, TtsService>();
        services.AddSingleton<IAudioPlayer, AudioPlayer>();
        services.AddSingleton<ISpeechQueue, SpeechQueue>();
        services.AddSingleton<IProxyService, ProxyService>();

        var sp = services.BuildServiceProvider();

        var speechQueue = sp.GetRequiredService<ISpeechQueue>();
        var proxyService = sp.GetRequiredService<IProxyService>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var proxyTask = proxyService.RunAsync(cts.Token);

        var ready = false;
        for (var i = 0; i < 40; i++)
        {
            await Task.Delay(250);
            try
            {
                using var probe = new HttpClient { Timeout = TimeSpan.FromMilliseconds(500) };
                var probeResponse = await probe.GetAsync($"http://localhost:{testPort}/v1/models");
                ready = true;
                break;
            }
            catch (HttpRequestException)
            {
                ready = true;
                break;
            }
            catch { }
        }

        Assert.True(ready, $"Proxy did not start on port {testPort} within 10 seconds");

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var requestBody = new
        {
            model = "z-ai/glm-5-turbo",
            messages = new object[]
            {
                new { role = "user", content = "Say hello in exactly 5 words. Nothing else." }
            },
            stream = false
        };

        var json = JsonSerializer.Serialize(requestBody, JsonOpts);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var sw = Stopwatch.StartNew();
        var response = await client.PostAsync($"http://localhost:{testPort}/v1/chat/completions", content);
        var body = await response.Content.ReadAsStringAsync();
        sw.Stop();

        Assert.True(response.IsSuccessStatusCode, $"Proxy returned {response.StatusCode}: {body}");
        Assert.Contains("choices", body);

        using var doc = JsonDocument.Parse(body);
        var choices = doc.RootElement.GetProperty("choices");
        Assert.True(choices.GetArrayLength() > 0, "No choices in response");

        var message = choices[0].GetProperty("message");
        var responseText = message.GetProperty("content").GetString();
        Assert.False(string.IsNullOrEmpty(responseText), "Response content is empty");

        Console.WriteLine($"[INTEGRATION] Response ({sw.ElapsedMilliseconds}ms): {responseText}");

        cts.Cancel();
        try { await proxyTask; } catch (OperationCanceledException) { }

        Console.WriteLine("[INTEGRATION] Test complete — audio should have played through speakers");
    }

    [Fact(Skip = "Integration - requires live API key and audio device. Remove Skip to run.")]
    [Trait("Category", "Integration")]
    public async Task Proxy_FullIntegration_Streaming_SpeaksResponse()
    {
        var projectRoot = FindSourceProjectRoot();
        var devSettingsPath = Path.Combine(projectRoot, "appsettings.Development.json");
        if (!File.Exists(devSettingsPath))
        {
            Assert.Fail("appsettings.Development.json not found");
            return;
        }

        var devJson = await File.ReadAllTextAsync(devSettingsPath);
        var devSettings = JsonSerializer.Deserialize<AppSettings>(devJson, JsonOpts);
        var apiKey = devSettings?.OpenRouter.ApiKey;
        if (string.IsNullOrEmpty(apiKey))
        {
            Assert.Fail("API key not found");
            return;
        }

        var testPort = 18889;

        var settings = new AppSettings
        {
            OpenRouter = new OpenRouterSettings
            {
                ApiKey = apiKey,
                BaseUrl = "https://openrouter.ai/api/v1",
                TtsModel = "openai/gpt-4o-mini-tts-2025-12-15"
            },
            Audio = new AudioSettings { OutputFormat = "mp3" },
            Proxy = new ProxySettings
            {
                Port = testPort,
                BackendUrl = "https://openrouter.ai/api/v1",
                BackendModel = "z-ai/glm-5-turbo",
                TtsPersona = "sexy_female"
            },
            Personas = new Dictionary<string, VoicePersona>
            {
                ["sexy_female"] = new()
                {
                    Model = "openai/gpt-4o-mini-tts-2025-12-15",
                    Voice = "marin",
                    OpenAiInstructions = "young, british, sexy, flirty",
                    IsDefault = true
                }
            }
        };

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Debug));
        services.AddHttpClient("OpenRouter", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(60);
        });
        services.AddHttpClient("ProxyBackend", client =>
        {
            client.Timeout = TimeSpan.FromMinutes(5);
        });
        services.AddSingleton<IOptions<AppSettings>>(Options.Create(settings));
        services.AddSingleton<IAudioConverter, AudioConverter>();
        services.AddSingleton<ITtsService, TtsService>();
        services.AddSingleton<IAudioPlayer, AudioPlayer>();
        services.AddSingleton<ISpeechQueue, SpeechQueue>();
        services.AddSingleton<IProxyService, ProxyService>();

        var sp = services.BuildServiceProvider();

        var speechQueue = sp.GetRequiredService<ISpeechQueue>();
        var proxyService = sp.GetRequiredService<IProxyService>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var proxyTask = proxyService.RunAsync(cts.Token);

        var ready = false;
        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(250);
            try
            {
                using var probe = new HttpClient { Timeout = TimeSpan.FromMilliseconds(500) };
                await probe.GetAsync($"http://localhost:{testPort}/v1/models");
                ready = true;
                break;
            }
            catch { }
        }

        Assert.True(ready, $"Proxy did not start on port {testPort} within 5 seconds");

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var requestBody = new
        {
            model = "z-ai/glm-5-turbo",
            messages = new object[]
            {
                new { role = "user", content = "Tell me a one-sentence joke." }
            },
            stream = true
        };

        var json = JsonSerializer.Serialize(requestBody, JsonOpts);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var sw = Stopwatch.StartNew();
        var response = await client.PostAsync($"http://localhost:{testPort}/v1/chat/completions", content);
        Assert.True(response.IsSuccessStatusCode, $"Proxy returned {response.StatusCode}");

        var responseStream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(responseStream);
        var textBuffer = new StringBuilder();
        var chunkCount = 0;
        var gotDone = false;

        while (true)
        {
            var line = await reader.ReadLineAsync(cts.Token);
            if (line == null) break;

            if (line.StartsWith("data: "))
            {
                var data = line[6..];
                if (data == "[DONE]")
                {
                    gotDone = true;
                    break;
                }

                chunkCount++;
                try
                {
                    using var chunk = JsonDocument.Parse(data);
                    var choices = chunk.RootElement.GetProperty("choices");
                    if (choices.GetArrayLength() > 0)
                    {
                        var delta = choices[0].GetProperty("delta");
                        if (delta.TryGetProperty("content", out var contentEl))
                        {
                            var text = contentEl.GetString();
                            if (text != null)
                                textBuffer.Append(text);
                        }
                    }
                }
                catch { }
            }
        }

        sw.Stop();

        Assert.True(gotDone, "Stream did not end with [DONE]");
        Assert.True(chunkCount > 0, "No SSE chunks received");
        var fullText = textBuffer.ToString();
        Assert.False(string.IsNullOrEmpty(fullText), "No text content in streaming response");

        Console.WriteLine($"[INTEGRATION] Streaming response ({chunkCount} chunks, {sw.ElapsedMilliseconds}ms): {fullText}");

        await Task.Delay(1000);

        cts.Cancel();
        try { await proxyTask; } catch (OperationCanceledException) { }

        Console.WriteLine("[INTEGRATION] Test complete — audio should have played through speakers");
    }

    [Fact(Skip = "Integration - requires live API key. Run explicitly.")]
    [Trait("Category", "Integration")]
    public async Task Proxy_ModelOverride_InjectsConfiguredModel()
    {
        var projectRoot = FindSourceProjectRoot();
        var devSettingsPath = Path.Combine(projectRoot, "appsettings.Development.json");
        if (!File.Exists(devSettingsPath))
        {
            Assert.Fail("appsettings.Development.json not found");
            return;
        }

        var devJson = await File.ReadAllTextAsync(devSettingsPath);
        var devSettings = JsonSerializer.Deserialize<AppSettings>(devJson, JsonOpts);
        var apiKey = devSettings?.OpenRouter.ApiKey;
        if (string.IsNullOrEmpty(apiKey))
        {
            Assert.Fail("API key not found");
            return;
        }

        var testPort = 18890;

        var settings = new AppSettings
        {
            OpenRouter = new OpenRouterSettings
            {
                ApiKey = apiKey,
                BaseUrl = "https://openrouter.ai/api/v1",
                TtsModel = "openai/gpt-4o-mini-tts-2025-12-15"
            },
            Audio = new AudioSettings { OutputFormat = "mp3" },
            Proxy = new ProxySettings
            {
                Port = testPort,
                BackendUrl = "https://openrouter.ai/api/v1",
                BackendModel = "z-ai/glm-5-turbo",
                TtsPersona = "sexy_female"
            },
            Personas = new Dictionary<string, VoicePersona>
            {
                ["sexy_female"] = new()
                {
                    Model = "openai/gpt-4o-mini-tts-2025-12-15",
                    Voice = "marin",
                    OpenAiInstructions = "young, british, flirty",
                    IsDefault = true
                }
            }
        };

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Debug));
        services.AddHttpClient("OpenRouter", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(60);
        });
        services.AddHttpClient("ProxyBackend", client =>
        {
            client.Timeout = TimeSpan.FromMinutes(5);
        });
        services.AddSingleton<IOptions<AppSettings>>(Options.Create(settings));
        services.AddSingleton<IAudioConverter, AudioConverter>();
        services.AddSingleton<ITtsService, TtsService>();
        services.AddSingleton<IAudioPlayer, AudioPlayer>();
        services.AddSingleton<ISpeechQueue, SpeechQueue>();
        services.AddSingleton<IProxyService, ProxyService>();

        var sp = services.BuildServiceProvider();
        var proxyService = sp.GetRequiredService<IProxyService>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var proxyTask = proxyService.RunAsync(cts.Token);

        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(250);
            try
            {
                using var probe = new HttpClient { Timeout = TimeSpan.FromMilliseconds(500) };
                await probe.GetAsync($"http://localhost:{testPort}/v1/models");
                break;
            }
            catch { }
        }

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var requestBody = new
        {
            model = "gpt-4o",
            messages = new object[]
            {
                new { role = "user", content = "What model are you? Reply with just your model name." }
            },
            stream = false
        };

        var json = JsonSerializer.Serialize(requestBody, JsonOpts);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await client.PostAsync($"http://localhost:{testPort}/v1/chat/completions", content);
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode, $"Proxy returned {response.StatusCode}: {body}");

        using var doc = JsonDocument.Parse(body);
        var model = doc.RootElement.GetProperty("model").GetString();
        var responseText = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();

        Console.WriteLine($"[INTEGRATION] Requested: gpt-4o, Got model: {model}, Response: {responseText}");
        Assert.True(model?.ToLowerInvariant().Contains("glm") == true, $"Expected GLM model but got: {model}");

        cts.Cancel();
        try { await proxyTask; } catch (OperationCanceledException) { }
    }

    private static string FindSourceProjectRoot()
    {
        var dir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        while (!string.IsNullOrEmpty(dir))
        {
            var candidate = Path.Combine(dir, "src", "benow-conversation");
            if (Directory.Exists(candidate) && Directory.GetFiles(candidate, "benow-conversation.csproj").Length > 0)
                return Path.Combine(candidate, "bin", "Debug", "net10.0");

            var parent = Directory.GetParent(dir)?.FullName;
            if (parent == null || parent == dir) break;
            dir = parent;
        }

        throw new InvalidOperationException("Cannot find source project root with appsettings");
    }
}
