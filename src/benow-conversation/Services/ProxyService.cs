using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using benow_conversation.Configuration;
using benow_conversation.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace benow_conversation.Services;

public interface IProxyService
{
    Task RunAsync(CancellationToken cancellationToken);
}

public class ProxyService : IProxyService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AppSettings _settings;
    private readonly ISpeechQueue _speechQueue;
    private readonly IModifierInjector _modifierInjector;
    private readonly ICharacterNormalizer _characterNormalizer;
    private readonly IPersonaAllocator _personaAllocator;
    private readonly ParallelTtsPlayer _parallelTtsPlayer;
    private readonly ProviderFormatCache _formatCache;
    private readonly ILogger<ProxyService> _logger;
    private CancellationTokenSource? _activePlaybackCts;
    private Task? _activePlaybackTask;

    private static readonly Regex MultiCharacterMarkerRegex = new(
        @"\[(?:[A-Z][a-zA-Z]*(?::[FM])?|Self|Narrator:[FM])\][\s\S]*",
        RegexOptions.Compiled);

    public ProxyService(
        IHttpClientFactory httpClientFactory,
        IOptions<AppSettings> settings,
        ISpeechQueue speechQueue,
        IModifierInjector modifierInjector,
        ICharacterNormalizer characterNormalizer,
        IPersonaAllocator personaAllocator,
        ParallelTtsPlayer parallelTtsPlayer,
        ProviderFormatCache formatCache,
        ILogger<ProxyService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _settings = settings.Value;
        _speechQueue = speechQueue;
        _modifierInjector = modifierInjector;
        _characterNormalizer = characterNormalizer;
        _personaAllocator = personaAllocator;
        _parallelTtsPlayer = parallelTtsPlayer;
        _formatCache = formatCache;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var port = _settings.Proxy.Port;
        var bindAddress = _settings.Proxy.BindAddress;

        _logger.LogInformation("Proxy listening on http://{BindAddress}:{Port}", bindAddress, port);
        _logger.LogInformation("Backend: {BackendUrl}", _settings.Proxy.BackendUrl);
        _logger.LogInformation("Backend model: {Model}", _settings.Proxy.BackendModel ?? "(passthrough)");
        _logger.LogInformation("TTS persona: {Persona}", _settings.Proxy.TtsPersona ?? "(default)");

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://{bindAddress}:{port}");
        builder.Services.AddHttpClient("ProxyBackend", client =>
        {
            client.Timeout = TimeSpan.FromMinutes(5);
        });

        builder.Logging.ClearProviders();

        var app = builder.Build();

        app.Use(async (context, next) =>
        {
            context.Response.Headers.Append("Access-Control-Allow-Origin", "*");
            context.Response.Headers.Append("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            context.Response.Headers.Append("Access-Control-Allow-Headers", "*");
            context.Response.Headers.Append("Access-Control-Max-Age", "86400");

            if (context.Request.Method == "OPTIONS")
            {
                context.Response.StatusCode = 204;
                return;
            }

            await next();
        });

        app.Map("/v1/chat/completions", HandleChatCompletions);
        app.Map("/chat/completions", HandleChatCompletions);
        app.Map("/v1/models", FullPassThrough);
        app.Map("/models", FullPassThrough);
        app.Map("/v1", HandleChatCompletions);
        app.MapFallback(FullPassThrough);

        await app.RunAsync(cancellationToken);
    }

    private async Task HandleChatCompletions(HttpContext context)
    {
        try
        {
            await HandleChatCompletionsCore(context);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TaskCanceledException)
        {
            _logger.LogDebug("Request canceled by client: {Path}", context.Request.Path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error in HandleChatCompletions: {Message}", ex.Message);
            try
            {
                context.Response.StatusCode = 502;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync($"{{\"error\":{{\"message\":\"{ex.Message}\"}}}}");
            }
            catch { }
        }
    }

    private async Task HandleChatCompletionsCore(HttpContext context)
    {
        _speechQueue.FlushAndCancel();

        if (_activePlaybackCts != null)
        {
            _activePlaybackCts.Cancel();
            if (_activePlaybackTask != null)
                await Task.WhenAny(_activePlaybackTask, Task.Delay(2000));
            _activePlaybackCts.Dispose();
            _activePlaybackCts = null;
            _activePlaybackTask = null;
        }

        var requestBody = await ReadBodyAsync(context);

        var lastUserMessage = ExtractLastUserMessage(requestBody);
        if (lastUserMessage == "/reset")
        {
            _personaAllocator.Reset();
            _formatCache.Clear();
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            await context.Response.WriteAsync("data: {\"choices\":[{\"delta\":{\"role\":\"assistant\"},\"index\":0}]}\n\n");
            await context.Response.WriteAsync("data: [DONE]\n");
            await context.Response.Body.FlushAsync();
            return;
        }
        if (lastUserMessage == "/replay")
        {
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            await context.Response.WriteAsync("data: {\"choices\":[{\"delta\":{\"role\":\"assistant\"},\"index\":0}]}\n\n");
            await context.Response.WriteAsync("data: [DONE]\n");
            await context.Response.Body.FlushAsync();
            await _parallelTtsPlayer.ReplayLastAsync(context.RequestAborted);
            return;
        }
        var ct = context.RequestAborted;

        var hasAuth = context.Request.Headers.TryGetValue("Authorization", out var authValue);
        _logger.LogInformation("Incoming request: path={Path}, auth={HasAuth} ({AuthPreview}), headers={Headers}",
            context.Request.Path,
            hasAuth,
            hasAuth ? authValue.ToString()[..Math.Min(authValue.ToString().Length, 20)] + "..." : "none",
            string.Join(", ", context.Request.Headers.Keys));

        if (context.Request.Method != "POST")
        {
            context.Response.StatusCode = 405;
            return;
        }

        var originalModel = ExtractModel(requestBody);
        var effectiveModel = originalModel;
        var isStream = requestBody.Contains("\"stream\":true") || requestBody.Contains("\"stream\": true");

        if (string.IsNullOrEmpty(originalModel) && !string.IsNullOrEmpty(_settings.Proxy.BackendModel))
        {
            requestBody = InjectModel(requestBody, _settings.Proxy.BackendModel);
            effectiveModel = _settings.Proxy.BackendModel;
        }

        if (_settings.Proxy.InjectCharacterFormat && !HasBracketInstructions(requestBody))
        {
            requestBody = InjectCharacterFormatPrompt(requestBody);
            _logger.LogInformation("Injected multi-character format prompt into system message");
        }

        _logger.LogInformation("Chat completion: model={Model}→{Effective}, stream={Stream}, size={Size}",
            originalModel ?? "(none)",
            effectiveModel ?? "?",
            isStream, requestBody.Length);

        if (_settings.Proxy.LogBodies)
            _logger.LogDebug("Request body: {Body}", requestBody.Length > 2000 ? requestBody[..2000] + "..." : requestBody);

        var client = _httpClientFactory.CreateClient("ProxyBackend");
        var backendUrl = $"{_settings.Proxy.BackendUrl}/chat/completions";

        var backendRequest = new HttpRequestMessage(HttpMethod.Post, backendUrl)
        {
            Content = new StringContent(requestBody, Encoding.UTF8, "application/json")
        };

        CopyRequestHeaders(context, backendRequest);

        if (!backendRequest.Headers.Contains("Authorization") && !string.IsNullOrEmpty(_settings.OpenRouter.ApiKey))
        {
            backendRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _settings.OpenRouter.ApiKey);
            _logger.LogDebug("Injected API key from config (client sent no Authorization header)");
        }

        var backendResponse = await client.SendAsync(backendRequest, HttpCompletionOption.ResponseHeadersRead, ct);

        CopyResponseHeaders(backendResponse, context.Response);

        if (isStream)
        {
            await StreamWithTtsAsync(backendResponse, context, ct);
        }
        else
        {
            await NonStreamWithTtsAsync(backendResponse, context, ct);
        }
    }

    private async Task FullPassThrough(HttpContext context)
    {
        try
        {
            await FullPassThroughCore(context);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TaskCanceledException)
        {
            _logger.LogDebug("Pass-through request canceled by client: {Path}", context.Request.Path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error in FullPassThrough: {Message}", ex.Message);
        }
    }

    private async Task FullPassThroughCore(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "/";
        var backendPath = path.StartsWith("/v1/") ? path[3..] : (path == "/v1" ? "/" : path);
        var backendUrl = $"{_settings.Proxy.BackendUrl}{backendPath}";

        _logger.LogInformation("Pass-through: {Method} {Path} → {Url}", context.Request.Method, path, backendUrl);

        var client = _httpClientFactory.CreateClient("ProxyBackend");
        var method = new HttpMethod(context.Request.Method);

        var backendRequest = new HttpRequestMessage(method, backendUrl);
        CopyRequestHeaders(context, backendRequest);

        if (!backendRequest.Headers.Contains("Authorization") && !string.IsNullOrEmpty(_settings.OpenRouter.ApiKey))
        {
            backendRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _settings.OpenRouter.ApiKey);
        }

        if (context.Request.ContentLength > 0 || context.Request.Headers.ContainsKey("Content-Type"))
        {
            var body = await ReadBodyAsync(context);
            backendRequest.Content = new StringContent(body, Encoding.UTF8,
                context.Request.ContentType ?? "application/json");
        }

        var backendResponse = await client.SendAsync(backendRequest, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);

        CopyResponseHeaders(backendResponse, context.Response);
        context.Response.StatusCode = (int)backendResponse.StatusCode;

        var contentType = backendResponse.Content.Headers.ContentType?.MediaType ?? "";

        if (contentType.Contains("text/event-stream") || contentType.Contains("text/plain"))
        {
            using var backendStream = await backendResponse.Content.ReadAsStreamAsync(context.RequestAborted);
            var buffer = new byte[8192];
            int bytesRead;
            while ((bytesRead = await backendStream.ReadAsync(buffer, context.RequestAborted)) > 0)
            {
                await context.Response.Body.WriteAsync(buffer.AsMemory(0, bytesRead), context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
            }
        }
        else
        {
            var responseBytes = await backendResponse.Content.ReadAsByteArrayAsync(context.RequestAborted);
            await context.Response.Body.WriteAsync(responseBytes, context.RequestAborted);
        }
    }

    private async Task StreamWithTtsAsync(
        HttpResponseMessage backendResponse,
        HttpContext context,
        CancellationToken ct)
    {
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Connection = "keep-alive";

        using var backendStream = await backendResponse.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(backendStream);

        var textBuffer = new StringBuilder();
        var multiCharacterText = false;
        ParagraphSplitter? splitter = null;

        var chunkedTts = _settings.Proxy.ChunkedTts;
        if (chunkedTts)
            splitter = new ParagraphSplitter(_settings.Proxy.MinParagraphLength, _settings.Proxy.MaxChunkLength);

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line == null) break;

            await context.Response.WriteAsync(line + "\n", ct);
            await context.Response.Body.FlushAsync(ct);

            if (line.StartsWith("data: "))
            {
                var data = line[6..];
                if (data == "[DONE]")
                {
                    await context.Response.WriteAsync("data: [DONE]\n", ct);
                    await context.Response.Body.FlushAsync(ct);
                    break;
                }

                try
                {
                    using var chunk = JsonDocument.Parse(data);
                    var choices = chunk.RootElement.GetProperty("choices");
                    if (choices.GetArrayLength() > 0)
                    {
                        var delta = choices[0].GetProperty("delta");
                        if (delta.TryGetProperty("content", out var contentEl))
                        {
                            var content = contentEl.GetString();
                            if (content != null)
                            {
                                textBuffer.Append(content);

                                if (!multiCharacterText && IsMultiCharacter(content))
                                {
                                    multiCharacterText = true;
                                    _logger.LogInformation("Multi-character markers detected — deferring to end-of-stream processing");
                                    _speechQueue.FlushAndCancel();
                                }

                                if (chunkedTts && !multiCharacterText && splitter != null)
                                {
                                    splitter.Append(content);
                                    while (splitter.TryDequeue(out var para))
                                    {
                                        if (IsMultiCharacter(para))
                                        {
                                            multiCharacterText = true;
                                            _logger.LogInformation("Multi-character markers in paragraph — deferring to end-of-stream");
                                            _speechQueue.FlushAndCancel();
                                            break;
                                        }
                                        _speechQueue.Enqueue(para, cancelCurrent: false);
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to parse SSE delta chunk");
                }
            }
        }

        var text = textBuffer.ToString().Trim();
        if (!string.IsNullOrEmpty(text))
        {
            if (_settings.Proxy.LogBodies)
                _logger.LogDebug("Streamed TTS text: {Text}", text.Length > 2000 ? text[..2000] + "..." : text);

            _logger.LogInformation("Raw LLM text ({Length} chars): {Preview}", text.Length, text.Length > 300 ? text[..300] + "..." : text);

            if (IsMultiCharacter(text))
            {
                _logger.LogInformation("Streamed response ({Length} chars) — multi-character TTS (markers detected)", text.Length);
                _speechQueue.FlushAndCancel();
                await ProcessMultiCharacterTextAsync(text, ct);
            }
            else if (IsLikelyProseWithDialogue(text))
            {
                _logger.LogInformation("Streamed response ({Length} chars) — prose with dialogue detected, normalizing", text.Length);
                _speechQueue.FlushAndCancel();
                var normalized = await _characterNormalizer.NormalizeAsync(text, ct);
                if (IsMultiCharacter(normalized))
                {
                    _logger.LogInformation("Normalized text: {Length} chars", normalized.Length);
                    await ProcessMultiCharacterTextAsync(normalized, ct);
                }
                else
                {
                    _logger.LogError("Normalization did not produce markers, falling back to paragraph-by-paragraph TTS");
                    EnqueueChunked(text);
                }
            }
            else
            {
                _logger.LogInformation("Streamed response ({Length} chars) — queuing TTS", text.Length);

                if (splitter != null)
                {
                    var remaining = splitter.Flush();
                    if (remaining != null)
                        _speechQueue.Enqueue(remaining, cancelCurrent: false);
                }
                else
                {
                    _speechQueue.Enqueue(text, cancelCurrent: true);
                }
            }
        }
    }

    private async Task NonStreamWithTtsAsync(
        HttpResponseMessage backendResponse,
        HttpContext context,
        CancellationToken ct)
    {
        var body = await backendResponse.Content.ReadAsStringAsync(ct);
        context.Response.StatusCode = (int)backendResponse.StatusCode;

        if (_settings.Proxy.LogBodies)
            _logger.LogDebug("Response body ({Status}): {Body}", (int)backendResponse.StatusCode, body.Length > 2000 ? body[..2000] + "..." : body);

        await context.Response.WriteAsync(body, ct);

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("choices", out var choices) &&
                choices.GetArrayLength() > 0)
            {
                var message = choices[0].GetProperty("message");
                if (message.TryGetProperty("content", out var contentEl))
                {
                    var text = contentEl.GetString();
                    if (!string.IsNullOrEmpty(text))
                    {
                        if (IsMultiCharacter(text))
                        {
                            _logger.LogInformation("Non-stream response ({Length} chars) — multi-character TTS", text.Length);
                            await ProcessMultiCharacterTextAsync(text, ct);
                        }
                        else if (IsLikelyProseWithDialogue(text))
                        {
                            _logger.LogInformation("Non-stream response ({Length} chars) — prose with dialogue, normalizing", text.Length);
                            var normalized = await _characterNormalizer.NormalizeAsync(text, ct);
                            if (IsMultiCharacter(normalized))
                                await ProcessMultiCharacterTextAsync(normalized, ct);
                            else
                            {
                                _logger.LogError("Non-stream normalization did not produce markers, falling back to paragraph-by-paragraph TTS");
                                EnqueueChunked(text);
                            }
                        }
                        else
                        {
                            _logger.LogInformation("Non-stream response ({Length} chars) — queuing TTS", text.Length);
                            _speechQueue.Enqueue(text, cancelCurrent: true);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to extract text from non-streaming response");
        }
    }

    private async Task ProcessMultiCharacterTextAsync(string text, CancellationToken ct)
    {
        var annotated = await _modifierInjector.InjectModifiersAsync(text, ct);

        var segments = CharacterParser.Parse(annotated);

        var segmentsWithPersonas = new List<CharacterSegment>();
        foreach (var seg in segments)
        {
            if (seg.CharacterName == "Self")
            {
                seg.PersonaKey = _settings.MultiCharacter.SelfPersona;
            }
            else if (seg.IsThought)
            {
                seg.PersonaKey = _settings.MultiCharacter.ThoughtPersona;
            }
            else if (seg.IsNarration)
            {
                seg.PersonaKey = _settings.MultiCharacter.NarratorPersona;
            }
            else if (!string.IsNullOrEmpty(seg.CharacterName))
            {
                seg.PersonaKey = _personaAllocator.AllocateForCharacter(seg.CharacterName, seg.Gender);
            }

            if (seg.PersonaKey == null && !seg.IsNarration)
            {
                var defaultPersona = _settings.Personas.FirstOrDefault(p => p.Value.IsDefault);
                if (defaultPersona.Key != null)
                    seg.PersonaKey = defaultPersona.Key;
            }

            segmentsWithPersonas.Add(seg);
        }

        if (segmentsWithPersonas.Count == 0)
        {
            _logger.LogWarning("No segments parsed from multi-character text, falling to single-char TTS");
            _speechQueue.Enqueue(text, cancelCurrent: true);
            return;
        }

        LogTextCoverage(segmentsWithPersonas, text);

        _activePlaybackCts = new CancellationTokenSource();
        using var playbackCt = CancellationTokenSource.CreateLinkedTokenSource(ct, _activePlaybackCts.Token);

        _activePlaybackTask = _parallelTtsPlayer.PlaySegmentsAsync(
            segmentsWithPersonas, playbackCt.Token,
            onSegmentStart: async seg =>
            {
                var persona = seg.PersonaKey != null ? _personaAllocator.GetPersona(seg.PersonaKey) : null;
                _logger.LogInformation("Speaking: [{Char}] {Text} (persona={Persona}, voice={Voice})",
                    seg.CharacterName,
                    seg.SpokenText.Length > 60 ? seg.SpokenText[..60] + "..." : seg.SpokenText,
                    seg.PersonaKey, persona?.Voice);
                await Task.CompletedTask;
            });
    }

    private void EnqueueChunked(string text)
    {
        if (_settings.Proxy.ChunkedTts)
        {
            var splitter = new ParagraphSplitter(_settings.Proxy.MinParagraphLength, _settings.Proxy.MaxChunkLength);
            splitter.Append(text);
            var remaining = splitter.Flush();
            while (splitter.TryDequeue(out var para))
                _speechQueue.Enqueue(para, cancelCurrent: false);
            if (remaining != null)
                _speechQueue.Enqueue(remaining, cancelCurrent: false);
        }
        else
        {
            _speechQueue.Enqueue(text, cancelCurrent: true);
        }
    }

    private void LogTextCoverage(List<CharacterSegment> segments, string originalText)
    {
        var spokenTotal = segments.Sum(s => s.SpokenText.Length);
        var coverage = originalText.Length > 0 ? (double)spokenTotal / originalText.Length * 100 : 0;
        _logger.LogInformation("Text coverage: {Spoken} of {Original} chars ({Coverage:F1}%) — {Dropped} chars potentially dropped",
            spokenTotal, originalText.Length, coverage, Math.Max(0, originalText.Length - spokenTotal));
        if (coverage < 85)
            _logger.LogWarning("Low text coverage ({Coverage:F1}%) — significant text may be lost in modifier injection or parsing", coverage);
        if (coverage < 50)
            _logger.LogError("Very low text coverage ({Coverage:F1}%) — check modifier injection and parser for dropped content", coverage);
    }

    private static bool IsMultiCharacter(string text)
    {
        if (text.Length < 10) return false;
        return MultiCharacterMarkerRegex.IsMatch(text);
    }

    private static bool IsLikelyProseWithDialogue(string text)
    {
        if (text.Length < 200) return false;
        var hasQuote = text.Contains('"') || text.Contains('\u201C');
        var hasDialogueVerb = Regex.IsMatch(text,
            @"\b(?:said|says|asked|replied|whispered|murmured|exclaimed|breathed|admitted|continued|speaks?|asks?|responds?|mutters?|sighs?|gasps?)\b",
            RegexOptions.IgnoreCase);
        return hasQuote && hasDialogueVerb;
    }

    private const string CharacterFormatPrompt = @"

## CRITICAL OUTPUT FORMAT — You MUST use bracket markers for every line

Every line of your response must start with one of these markers:
  [CharacterName:F] for female character dialogue or actions
  [CharacterName:M] for male character dialogue or actions
  [Self] for Andy's (the user's) actions, dialogue, and thoughts
  [Narrator:F] for all third-person narration

Actions and narration MUST be wrapped in *asterisks*.  
Inner thoughts MUST be wrapped in [thought]...[/thought].  
NEVER output prose like ""Name: dialogue"" or ""Name said, dialogue"".

Examples:
[Marina:F] *adjusts her glasses* Ah, excellent. I've been expecting you.
[Self] *You step inside, taking in the warm atmosphere.*
[Sofia:F] (excited) Mami! You're here!
[Narrator:F] *The room falls silent.*
[Marina:F] [thought]He's even more handsome than I imagined.[/thought] Please, come in.";

    private static bool HasBracketInstructions(string body)
    {
        return body.Contains("[CharacterName:F]") || body.Contains("[Narrator:F]")
            || body.Contains("bracket format") || body.Contains("multi-character script");
    }

    private static string InjectCharacterFormatPrompt(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (!root.TryGetProperty("messages", out var messages) || messages.GetArrayLength() == 0)
                return body;

            using var stream = new MemoryStream();
            using var writer = new Utf8JsonWriter(stream);
            writer.WriteStartObject();

            foreach (var prop in root.EnumerateObject())
            {
                if (prop.NameEquals("messages"))
                {
                    writer.WriteStartArray("messages");
                    var injected = false;

                    foreach (var msg in prop.Value.EnumerateArray())
                    {
                        writer.WriteStartObject();
                        foreach (var field in msg.EnumerateObject())
                        {
                            if (field.NameEquals("content") && TryGetRole(msg, out var role) && role == "system")
                            {
                                var existing = field.Value.GetString() ?? "";
                                if (!injected && !existing.Contains("[CharacterName:F]") && !existing.Contains("[Narrator:F]"))
                                {
                                    writer.WriteString("content", existing + CharacterFormatPrompt);
                                    injected = true;
                                }
                                else
                                {
                                    writer.WriteString("content", existing);
                                }
                            }
                            else
                            {
                                field.WriteTo(writer);
                            }
                        }
                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                }
                else
                {
                    prop.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
            writer.Flush();
            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch
        {
            return body;
        }
    }

    private static bool TryGetRole(JsonElement msg, out string role)
    {
        if (msg.TryGetProperty("role", out var roleEl))
        {
            role = roleEl.GetString() ?? "";
            return true;
        }
        role = "";
        return false;
    }

    private static async Task<string> ReadBodyAsync(HttpContext context)
    {
        using var reader = new StreamReader(context.Request.Body);
        return await reader.ReadToEndAsync();
    }

    private static string? ExtractLastUserMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("messages", out var messages))
            {
                var last = messages.EnumerateArray().LastOrDefault(m =>
                    m.TryGetProperty("role", out var r) && r.GetString() == "user");
                if (last.TryGetProperty("content", out var c))
                    return c.GetString()?.Trim();
            }
        }
        catch { }
        return null;
    }

    private static string? ExtractModel(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("model", out var modelEl))
                return modelEl.GetString();
        }
        catch { }
        return null;
    }

    private static string InjectModel(string body, string model)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            using var stream = new MemoryStream();
            using var writer = new Utf8JsonWriter(stream);
            writer.WriteStartObject();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.NameEquals("model"))
                {
                    writer.WriteString("model", model);
                }
                else
                {
                    prop.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
            writer.Flush();
            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch
        {
            return body;
        }
    }

    private static void CopyRequestHeaders(HttpContext context, HttpRequestMessage backendRequest)
    {
        foreach (var header in context.Request.Headers)
        {
            var value = header.Value.ToString();
            if (string.IsNullOrEmpty(value)) continue;

            switch (header.Key.ToLowerInvariant())
            {
                case "content-type":
                case "content-length":
                case "host":
                case "transfer-encoding":
                    break;
                case "authorization":
                    backendRequest.Headers.TryAddWithoutValidation("Authorization", value);
                    break;
                default:
                    backendRequest.Headers.TryAddWithoutValidation(header.Key, value);
                    break;
            }
        }
    }

    private static readonly HashSet<string> SuppressedResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Transfer-Encoding",
        "Access-Control-Allow-Origin",
        "Access-Control-Allow-Methods",
        "Access-Control-Allow-Headers",
        "Access-Control-Max-Age",
        "Access-Control-Expose-Headers",
        "Access-Control-Allow-Credentials",
    };

    private static void CopyResponseHeaders(HttpResponseMessage backendResponse, HttpResponse response)
    {
        foreach (var header in backendResponse.Headers)
        foreach (var value in header.Value)
        {
            if (SuppressedResponseHeaders.Contains(header.Key))
                continue;

            try
            {
                response.Headers.Append(header.Key, value);
            }
            catch { }
        }

        foreach (var header in backendResponse.Content.Headers)
        foreach (var value in header.Value)
        {
            if (header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                response.ContentType = value;
            }
            else if (header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
            }
            else
            {
                try
                {
                    response.Headers.Append(header.Key, value);
                }
                catch { }
            }
        }
    }

    internal static void CheckPendingRegressionTests(
        bool enforce, ILogger logger, string fixturesDir)
    {
        if (!enforce) return;

        if (!Directory.Exists(fixturesDir))
        {
            logger.LogInformation("No fixtures directory at {Path} — skipping enforcement", fixturesDir);
            return;
        }

        var pendingFiles = Directory.GetFiles(fixturesDir, "*.pending");
        if (pendingFiles.Length > 0)
        {
            logger.LogError("{Count} pending regression test fixture(s) found — blocking startup:", pendingFiles.Length);
            foreach (var f in pendingFiles)
                logger.LogError("  {File}", Path.GetFileName(f));
            throw new InvalidOperationException(
                $"{pendingFiles.Length} pending regression test fixture(s) found. " +
                "Run tests to generate the missing golden master files, then restart.");
        }
    }
}
