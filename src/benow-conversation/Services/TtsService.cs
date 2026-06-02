using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using benow_conversation.Configuration;
using benow_conversation.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace benow_conversation.Services;

public class TtsService : ITtsService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AppSettings _settings;
    private readonly ILogger<TtsService> _logger;
    private readonly IAudioConverter _audioConverter;
    private readonly ProviderFormatCache _formatCache;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public TtsService(
        IHttpClientFactory httpClientFactory,
        IOptions<AppSettings> settings,
        ILogger<TtsService> logger,
        IAudioConverter audioConverter,
        ProviderFormatCache formatCache)
    {
        _httpClientFactory = httpClientFactory;
        _settings = settings.Value;
        _logger = logger;
        _audioConverter = audioConverter;
        _formatCache = formatCache;
        _formatCache.EnsureLoaded();
    }

    public async Task<string> SynthesizeToFileAsync(
        string text,
        string? outputFileName = null,
        string? voice = null,
        string? instructions = null,
        double? temperature = null,
        int? seed = null,
        string? model = null)
    {
        ValidateApiKey();

        var resolvedVoice = voice ?? "alloy";
        var resolvedModel = model ?? _settings.OpenRouter.TtsModel;
        var outputFormat = _settings.Audio.OutputFormat;
        var apiFormat = outputFormat;

        var cacheKey = $"openrouter/{resolvedModel}";
        if (_formatCache.Get(cacheKey) is AudioFormat cached && !string.IsNullOrEmpty(cached.Codec))
        {
            apiFormat = cached.Codec;
            _logger.LogInformation("Using cached API format '{Cached}' for model {Model}", cached.Codec, resolvedModel);
        }

        var request = new TtsRequest
        {
            Model = resolvedModel,
            Input = text,
            Voice = resolvedVoice,
            ResponseFormat = apiFormat,
            Temperature = temperature,
            Seed = seed
        };

        if (!string.IsNullOrWhiteSpace(instructions) && resolvedModel.StartsWith("openai/", StringComparison.OrdinalIgnoreCase))
        {
            request.Provider = new ProviderOptions
            {
                Options = new Dictionary<string, Dictionary<string, string>>
                {
                    ["openai"] = new() { ["instructions"] = instructions }
                }
            };
        }

        _logger.LogInformation("Synthesizing: model={Model}, voice={Voice}, format={Format}, textLength={Length}, temp={Temp}, seed={Seed}, instructions={Instructions}",
            request.Model, request.Voice, apiFormat, text.Length, temperature?.ToString() ?? "default", seed?.ToString() ?? "default", instructions ?? "(none)");

        var sw = Stopwatch.StartNew();

        var client = _httpClientFactory.CreateClient("OpenRouter");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _settings.OpenRouter.ApiKey);

        var response = await SendRequestAsync(client, request);
        _logger.LogInformation("API responded: {StatusCode} in {ElapsedMs}ms", (int)response.StatusCode, sw.ElapsedMilliseconds);

        byte[] audioBytes;
        var needPcmConvert = false;

        try
        {
            audioBytes = await ReadAudioResponseAsync(response);
            if (_formatCache.Get(cacheKey) == null)
                _formatCache.Set(cacheKey, new AudioFormat(apiFormat, 24000, 1, 16));
        }
        catch (InvalidOperationException ex) when (IsFormatError(ex) && apiFormat != "pcm")
        {
            _formatCache.Set(cacheKey, new AudioFormat("pcm", 24000, 1, 16));
            _logger.LogWarning("Model {Model} does not support '{Format}', retrying with PCM (cached for session)", resolvedModel, apiFormat);

            request.ResponseFormat = "pcm";
            response = await SendRequestAsync(client, request);
            _logger.LogInformation("PCM retry responded: {StatusCode} in {ElapsedMs}ms", (int)response.StatusCode, sw.ElapsedMilliseconds);

            audioBytes = await ReadAudioResponseAsync(response);
            needPcmConvert = true;
        }

        if (needPcmConvert || (apiFormat == "pcm" && outputFormat != "pcm"))
        {
            if (_audioConverter.IsFfmpegAvailable())
            {
                _logger.LogInformation("Converting PCM ({Size} bytes) to {Format} via ffmpeg", audioBytes.Length, outputFormat);
                audioBytes = await _audioConverter.ConvertPcmToMp3Async(audioBytes);
            }
            else
            {
                _logger.LogWarning("ffmpeg not available, saving as raw PCM");
                outputFormat = "pcm";
            }
        }

        _logger.LogInformation("Audio received: {Size} bytes ({Format}) in {ElapsedMs}ms total", audioBytes.Length, outputFormat, sw.ElapsedMilliseconds);

        var outputDir = ResolveOutputPath();
        Directory.CreateDirectory(outputDir);

        var filename = outputFileName ?? $"{DateTime.Now:yyyyMMdd-HHmmss}.{outputFormat}";
        if (outputFormat != _settings.Audio.OutputFormat && outputFileName != null)
        {
            var ext = Path.GetExtension(outputFileName);
            if (!string.IsNullOrEmpty(ext))
                filename = $"{Path.GetFileNameWithoutExtension(outputFileName)}.{outputFormat}";
        }

        var fullPath = Path.Combine(outputDir, filename);

        try
        {
            await File.WriteAllBytesAsync(fullPath, audioBytes);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to write audio file to '{fullPath}': {ex.Message}.", ex);
        }

        _logger.LogInformation("Saved: {Path} ({Size} bytes, {ElapsedMs}ms total)", fullPath, audioBytes.Length, sw.ElapsedMilliseconds);
        return fullPath;
    }

    public async Task<(Stream AudioStream, string Format)> SynthesizeToStreamAsync(
        string text,
        string? voice = null,
        string? instructions = null,
        double? temperature = null,
        int? seed = null,
        string? model = null)
    {
        ValidateApiKey();

        var resolvedVoice = voice ?? "alloy";
        var resolvedModel = model ?? _settings.OpenRouter.TtsModel;
        var apiFormat = _settings.Audio.OutputFormat;

        var cacheKey = $"openrouter/{resolvedModel}";
        if (_formatCache.Get(cacheKey) is AudioFormat cached && !string.IsNullOrEmpty(cached.Codec))
        {
            apiFormat = cached.Codec;
            _logger.LogInformation("Using cached API format '{Cached}' for model {Model}", cached.Codec, resolvedModel);
        }

        var request = new TtsRequest
        {
            Model = resolvedModel,
            Input = text,
            Voice = resolvedVoice,
            ResponseFormat = apiFormat,
            Temperature = temperature,
            Seed = seed
        };

        if (!string.IsNullOrWhiteSpace(instructions) && resolvedModel.StartsWith("openai/", StringComparison.OrdinalIgnoreCase))
        {
            request.Provider = new ProviderOptions
            {
                Options = new Dictionary<string, Dictionary<string, string>>
                {
                    ["openai"] = new() { ["instructions"] = instructions }
                }
            };
        }

        _logger.LogInformation("Streaming synthesis: model={Model}, voice={Voice}, format={Format}, textLength={Length}",
            request.Model, request.Voice, apiFormat, text.Length);

        var sw = Stopwatch.StartNew();

        var client = _httpClientFactory.CreateClient("OpenRouter");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _settings.OpenRouter.ApiKey);

        var response = await SendRequestAsync(client, request);
        _logger.LogInformation("Stream API responded: {StatusCode} in {ElapsedMs}ms", (int)response.StatusCode, sw.ElapsedMilliseconds);

        byte[] audioBytes;

        try
        {
            audioBytes = await ReadAudioResponseAsync(response);
            if (_formatCache.Get(cacheKey) == null)
                _formatCache.Set(cacheKey, new AudioFormat(apiFormat, 24000, 1, 16));
        }
        catch (InvalidOperationException ex) when (IsFormatError(ex) && apiFormat != "pcm")
        {
            _formatCache.Set(cacheKey, new AudioFormat("pcm", 24000, 1, 16));
            _logger.LogWarning("Model {Model} does not support '{Format}', retrying with PCM (cached for session)", resolvedModel, apiFormat);

            request.ResponseFormat = "pcm";
            response = await SendRequestAsync(client, request);
            _logger.LogInformation("PCM retry responded: {StatusCode} in {ElapsedMs}ms", (int)response.StatusCode, sw.ElapsedMilliseconds);

            audioBytes = await ReadAudioResponseAsync(response);
            apiFormat = "pcm";
        }

        _logger.LogInformation("Stream audio received: {Size} bytes ({Format}) in {ElapsedMs}ms", audioBytes.Length, apiFormat, sw.ElapsedMilliseconds);

        return (new MemoryStream(audioBytes), apiFormat);
    }

    public async Task<Stream> SynthesizeLiveStreamAsync(
        string text,
        string? voice = null,
        string? instructions = null,
        double? temperature = null,
        int? seed = null,
        string? model = null)
    {
        ValidateApiKey();

        var resolvedVoice = voice ?? "alloy";
        var resolvedModel = model ?? _settings.OpenRouter.TtsModel;
        var apiFormat = _settings.Audio.OutputFormat;

        var cacheKey = $"openrouter/{resolvedModel}";
        if (_formatCache.Get(cacheKey) is AudioFormat cached && !string.IsNullOrEmpty(cached.Codec))
            apiFormat = cached.Codec;

        if (_formatCache.Get(cacheKey) == null)
        {
            _logger.LogInformation("Format not cached for {Model} — using buffered path first", resolvedModel);
            var (ms, fmt) = await SynthesizeToStreamAsync(text, voice, instructions, temperature, seed, model);
            ms.Position = 0;
            return ms;
        }

        var request = new TtsRequest
        {
            Model = resolvedModel,
            Input = text,
            Voice = resolvedVoice,
            ResponseFormat = apiFormat,
            Temperature = temperature,
            Seed = seed
        };

        if (!string.IsNullOrWhiteSpace(instructions) && resolvedModel.StartsWith("openai/", StringComparison.OrdinalIgnoreCase))
        {
            request.Provider = new ProviderOptions
            {
                Options = new Dictionary<string, Dictionary<string, string>>
                {
                    ["openai"] = new() { ["instructions"] = instructions }
                }
            };
        }

        _logger.LogInformation("Live stream synthesis: model={Model}, voice={Voice}, format={Format}, textLength={Length}",
            request.Model, request.Voice, apiFormat, text.Length);

        var client = _httpClientFactory.CreateClient("OpenRouter");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _settings.OpenRouter.ApiKey);

        var response = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, $"{_settings.OpenRouter.BaseUrl}/audio/speech")
            {
                Content = JsonContent.Create(request, options: JsonOptions)
            },
            HttpCompletionOption.ResponseHeadersRead);

        if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
        {
            response.Dispose();
            throw new InvalidOperationException("OpenRouter API key is invalid or unauthorized.");
        }

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            response.Dispose();
            throw new InvalidOperationException("OpenRouter rate limit exceeded.");
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            response.Dispose();
            throw new InvalidOperationException($"TTS API failed (HTTP {(int)response.StatusCode}): {TryExtractErrorMessage(errorBody)}");
        }

        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (contentType != null && contentType.Contains("json"))
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            response.Dispose();
            throw new InvalidOperationException($"TTS API error: {TryExtractErrorMessage(errorBody)}");
        }

        _logger.LogInformation("Live stream response headers received (HTTP {Status})", (int)response.StatusCode);

        return await response.Content.ReadAsStreamAsync();
    }

    public async Task<List<string>> SynthesizeAllVoicesAsync(
        string text,
        string? outputFileName = null,
        string? instructions = null,
        double? temperature = null,
        int? seed = null)
    {
        ValidateApiKey();

        var modelService = CreateModelService();
        var voices = await modelService.GetVoicesForModelAsync(_settings.OpenRouter.TtsModel);

        _logger.LogInformation("Batch synthesis: {Count} voices for model {Model}", voices.Count, _settings.OpenRouter.TtsModel);

        var results = new List<string>();
        var baseName = outputFileName != null
            ? Path.GetFileNameWithoutExtension(outputFileName)
            : $"{DateTime.Now:yyyyMMdd-HHmmss}";
        var ext = $".{_settings.Audio.OutputFormat}";
        var totalSw = Stopwatch.StartNew();

        for (var i = 0; i < voices.Count; i++)
        {
            var voiceInfo = voices[i];
            _logger.LogInformation("[{Current}/{Total}] Generating voice: {Voice}", i + 1, voices.Count, voiceInfo.Id);
            var voiceFileName = $"{baseName}_{voiceInfo.Id}{ext}";
            try
            {
                var path = await SynthesizeToFileAsync(text, voiceFileName, voiceInfo.Id, instructions, temperature, seed);
                results.Add(path);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{Current}/{Total}] FAILED voice {Voice}: {Error}", i + 1, voices.Count, voiceInfo.Id, ex.Message);
            }
        }

        _logger.LogInformation("Batch complete: {Count}/{Total} files in {ElapsedMs}ms", results.Count, voices.Count, totalSw.ElapsedMilliseconds);
        return results;
    }

    public async Task<List<string>> SynthesizeAllModelsAsync(
        string text,
        string? outputFileName = null,
        string? voice = null,
        string? instructions = null,
        double? temperature = null,
        int? seed = null)
    {
        ValidateApiKey();

        var modelService = CreateModelService();
        var models = await modelService.GetTtsModelsAsync();

        _logger.LogInformation("All-models synthesis: {Count} models, voice={Voice}", models.Count, voice ?? "(first per model)");

        var results = new List<string>();
        var baseName = outputFileName != null
            ? Path.GetFileNameWithoutExtension(outputFileName)
            : $"{DateTime.Now:yyyyMMdd-HHmmss}";
        var ext = $".{_settings.Audio.OutputFormat}";
        var totalSw = Stopwatch.StartNew();
        var totalSuccess = 0;

        for (var i = 0; i < models.Count; i++)
        {
            var ttsModel = models[i];
            _logger.LogInformation("[{Current}/{Total}] Model: {Model}", i + 1, models.Count, ttsModel.Id);

            var modelSlug = ttsModel.Id.Replace("/", "_");
            var modelInstructions = ttsModel.Id.StartsWith("openai/", StringComparison.OrdinalIgnoreCase) ? instructions : null;

            string effectiveVoice;
            if (!string.IsNullOrWhiteSpace(voice))
            {
                effectiveVoice = voice;
            }
            else
            {
                var voices = await modelService.GetVoicesForModelAsync(ttsModel.Id);
                effectiveVoice = voices.Count > 0 ? voices[0].Id : "alloy";
            }

            var fileName = $"{baseName}_{modelSlug}{ext}";
            try
            {
                var path = await SynthesizeToFileAsync(text, fileName, effectiveVoice, modelInstructions, temperature, seed, ttsModel.Id);
                results.Add(path);
                totalSuccess++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "FAILED model {Model}: {Error}", ttsModel.Id, ex.Message);
            }
        }

        _logger.LogInformation("All-models complete: {Success}/{Total} in {ElapsedMs}ms",
            totalSuccess, models.Count, totalSw.ElapsedMilliseconds);
        return results;
    }

    public async Task<List<string>> SynthesizeAllProvidersAsync(
        string text,
        string? outputFileName = null,
        string? instructions = null,
        double? temperature = null,
        int? seed = null)
    {
        ValidateApiKey();

        var modelService = CreateModelService();
        var models = await modelService.GetTtsModelsAsync();

        _logger.LogInformation("All-providers synthesis: {Count} models", models.Count);

        var results = new List<string>();
        var baseName = outputFileName != null
            ? Path.GetFileNameWithoutExtension(outputFileName)
            : $"{DateTime.Now:yyyyMMdd-HHmmss}";
        var ext = $".{_settings.Audio.OutputFormat}";
        var totalSw = Stopwatch.StartNew();
        var totalVoices = 0;
        var totalSuccess = 0;

        foreach (var ttsModel in models)
        {
            _logger.LogInformation("=== Provider: {Model} ({VoiceCount} voices) ===", ttsModel.Id, ttsModel.VoiceCount);

            var voices = await modelService.GetVoicesForModelAsync(ttsModel.Id);
            totalVoices += voices.Count;

            var modelSlug = ttsModel.Id.Replace("/", "_");
            var modelInstructions = ttsModel.Id.StartsWith("openai/", StringComparison.OrdinalIgnoreCase) ? instructions : null;

            for (var i = 0; i < voices.Count; i++)
            {
                var voiceInfo = voices[i];
                var fileName = $"{baseName}_{modelSlug}_{voiceInfo.Id}{ext}";
                var voiceNum = i + 1;
                _logger.LogInformation("[{Model}] [{VoiceCurrent}/{VoiceTotal}] {Voice}", ttsModel.Id, voiceNum, voices.Count, voiceInfo.Id);
                try
                {
                    var path = await SynthesizeToFileAsync(text, fileName, voiceInfo.Id, modelInstructions, temperature, seed, ttsModel.Id);
                    results.Add(path);
                    totalSuccess++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[{Model}] FAILED voice {Voice}: {Error}", ttsModel.Id, voiceInfo.Id, ex.Message);
                }
            }
        }

        _logger.LogInformation("All-providers complete: {Success}/{Total} voices across {ModelCount} models in {ElapsedMs}ms",
            totalSuccess, totalVoices, models.Count, totalSw.ElapsedMilliseconds);
        return results;
    }

    private static bool IsFormatError(Exception ex)
    {
        return ex is InvalidOperationException &&
               ex.Message.Contains("response_format", StringComparison.OrdinalIgnoreCase);
    }

    private ModelService CreateModelService()
    {
        var options = Options.Create(_settings);
        var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
        var logger = loggerFactory.CreateLogger<ModelService>();
        return new ModelService(_httpClientFactory, options, logger);
    }

    private void ValidateApiKey()
    {
        if (string.IsNullOrWhiteSpace(_settings.OpenRouter.ApiKey))
        {
            throw new InvalidOperationException(
                "OpenRouter API key is not configured. Set 'OpenRouter:ApiKey' in appsettings.Development.json.");
        }
    }

    private async Task<HttpResponseMessage> SendRequestAsync(HttpClient client, TtsRequest request)
    {
        HttpResponseMessage response;
        try
        {
            response = await client.PostAsJsonAsync(
                $"{_settings.OpenRouter.BaseUrl}/audio/speech",
                request,
                JsonOptions);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException(
                $"Unable to connect to OpenRouter at {_settings.OpenRouter.BaseUrl}. Check your network connection.",
                ex);
        }

        return response;
    }

    private async Task<byte[]> ReadAudioResponseAsync(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized ||
            response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new InvalidOperationException(
                "OpenRouter API key is invalid or unauthorized. Check your API key in appsettings.Development.json.");
        }

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new InvalidOperationException(
                "OpenRouter rate limit exceeded. Wait a moment and try again.");
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            var errorMessage = TryExtractErrorMessage(errorBody);
            throw new InvalidOperationException(
                $"TTS API request failed (HTTP {(int)response.StatusCode}): {errorMessage}");
        }

        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (contentType != null && contentType.Contains("json"))
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            var errorMessage = TryExtractErrorMessage(errorBody);
            throw new InvalidOperationException(
                $"TTS API returned an error response: {errorMessage}");
        }

        var bytes = await response.Content.ReadAsByteArrayAsync();
        if (bytes.Length == 0)
        {
            throw new InvalidOperationException(
                "TTS API returned an empty response. The service may be experiencing issues.");
        }

        return bytes;
    }

    private static string TryExtractErrorMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var errorEl) &&
                errorEl.TryGetProperty("message", out var messageEl))
            {
                return messageEl.GetString() ?? body;
            }
        }
        catch
        {
        }

        return body;
    }

    private string ResolveOutputPath()
    {
        var outputPath = _settings.Audio.OutputPath;
        if (Path.IsPathRooted(outputPath))
            return outputPath;

        var projectRoot = FindProjectRoot();
        return Path.GetFullPath(outputPath, projectRoot);
    }

    internal static string FindProjectRoot()
    {
        var dir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        while (!string.IsNullOrEmpty(dir))
        {
            if (Directory.GetFiles(dir, "*.csproj").Length > 0)
                return dir;

            var parent = Directory.GetParent(dir)?.FullName;
            if (parent == null || parent == dir)
                break;
            dir = parent;
        }

        throw new InvalidOperationException("Cannot find project root directory containing a .csproj file.");
    }
}
