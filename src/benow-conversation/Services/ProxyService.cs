using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using benow_conversation.Configuration;
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
    private readonly ILogger<ProxyService> _logger;

    public ProxyService(
        IHttpClientFactory httpClientFactory,
        IOptions<AppSettings> settings,
        ISpeechQueue speechQueue,
        ILogger<ProxyService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _settings = settings.Value;
        _speechQueue = speechQueue;
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
        var requestBody = await ReadBodyAsync(context);
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
                                textBuffer.Append(content);
                        }
                    }
                }
                catch { }
            }
        }

        var text = textBuffer.ToString().Trim();
        if (!string.IsNullOrEmpty(text))
        {
            if (_settings.Proxy.LogBodies)
                _logger.LogDebug("Streamed TTS text: {Text}", text.Length > 2000 ? text[..2000] + "..." : text);
            _logger.LogInformation("Streamed response ({Length} chars) — queuing TTS", text.Length);
            _speechQueue.Enqueue(text);
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
                        _logger.LogInformation("Non-stream response ({Length} chars) — queuing TTS", text.Length);
                        _speechQueue.Enqueue(text);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to extract text from non-streaming response");
        }
    }

    private static async Task<string> ReadBodyAsync(HttpContext context)
    {
        using var reader = new StreamReader(context.Request.Body);
        return await reader.ReadToEndAsync();
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
}
