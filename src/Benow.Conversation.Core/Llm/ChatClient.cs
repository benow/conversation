using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Benow.Conversation.Llm;

/// <summary>Provider + model selection for a chat turn.</summary>
public sealed record ChatOptions
{
    public string ProviderTypeId { get; init; } = "openrouter";
    public string ApiKey { get; init; } = "";
    public string BaseUrl { get; init; } = "";
    /// <summary>The speaker (answer) model.</summary>
    public string SpeakerModel { get; init; } = "";
    /// <summary>The extractor (tool dispatcher) model, or null/empty for the raw-LLM path.</summary>
    public string? ExtractorModel { get; init; }
    /// <summary>Master tool switch — tools require this AND an extractor (both must be true).</summary>
    public bool UseTools { get; init; } = true;
    /// <summary>The persona/system prompt (never reaches the extractor).</summary>
    public string? SystemPrompt { get; init; }
    /// <summary>OpenRouter provider pins, e.g. {"model-id":"deepinfra"}.</summary>
    public string? ModelProvidersJson { get; init; }
    public string? Timezone { get; init; }
    /// <summary>Log the full response text (diagnostics — reconstructable conversations).</summary>
    public bool LogConversationDebug { get; init; }
}

public sealed record ChatRequest
{
    public required string Transcript { get; init; }
    public List<ToolSchemaDto>? Tools { get; init; }
    public List<ChatHistoryMessage>? History { get; init; }
    public string UserId { get; init; } = "";
    public string DeviceId { get; init; } = "";
}

/// <summary>Outcome of one chat turn.</summary>
public sealed record ChatResult(string Text, int Chunks, long TotalMs, long ExtractorMs, long SpeakerMs, bool UsedTools);

/// <summary>Where a tool call came from (identity for identity-dependent tools like play_channel).</summary>
public sealed record ToolExecutionContext(string UserId, string DeviceId, string? Timezone);

/// <summary>
/// Tool execution seam: NASTV implements this over the host's /api/tools/execute (phase 4);
/// the desktop app over its local tool set. When null, a turn with tool calls degrades to
/// the no-data path instead of failing.
/// </summary>
public interface IToolExecutor
{
    Task<string> ExecuteAsync(string name, string argumentsJson, ToolExecutionContext context, CancellationToken ct);
}

/// <summary>
/// The conversation LLM engine — port of NASTV's chat turn strategy (2026-09-17, phase 2).
/// Modes (derived, never a mode enum — W14):
///   - Extractor+speaker (extractor configured + UseTools): the extractor calls tools
///     (non-streaming, no persona — F4), results compact into a "Current data" block, the
///     speaker streams the answer (no tool schemas — D6).
///   - Raw LLM (no extractor or UseTools=false): guidance + persona + temporal + history +
///     turn, streamed. The chat model NEVER receives tools in this mode (the 2026-08-22
///     master-switch design: a persona model with tools attached refuses sensitive content).
/// Degradations kept from NASTV: tools-unsupported extractor → honest "(none)" data turn;
/// empty pass-2 → one nudge retry, then "Done — anything else?" instead of silence.
/// </summary>
public sealed class ChatClient
{
    private readonly ILogger<ChatClient> _logger;
    private readonly ChatOptions _options;
    private readonly Func<HttpClient> _httpFactory;
    private readonly IToolExecutor? _tools;

    public ChatClient(ILogger<ChatClient> logger, ChatOptions options, Func<HttpClient> httpFactory, IToolExecutor? tools = null)
    {
        _logger = logger;
        _options = options;
        _httpFactory = httpFactory;
        _tools = tools;
    }

    /// <summary>
    /// Called when a turn fails in a way that produces no reply (unreachable provider, rejected
    /// key, refused model). Without this the failure is only a log line and the caller sees an
    /// empty result — which reads as "the assistant had nothing to say" instead of "it broke".
    ///
    /// Settable rather than a constructor option because the sink that reports to the UI attaches
    /// after this client is built; a captured sink would go stale the moment a host attached.
    /// </summary>
    public Action<string>? OnError { get; set; }

    public async Task<ChatResult?> ChatAsync(ChatRequest request, Action<string>? onChunk, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var transcript = (request.Transcript ?? "").Trim();
        if (transcript.Length == 0) return null;

        var extractor = PromptBuilders.ResolveExtractorModel(_options.ExtractorModel);
        var toolsEnabled = _options.UseTools && extractor != null && request.Tools is { Count: > 0 };
        if (!toolsEnabled)
        {
            _logger.LogInformation("[llm] tools disabled (UseTools={UseTools}, extractor='{Extractor}') — raw LLM path",
                _options.UseTools, extractor ?? "<none>");
        }

        try
        {
            using var http = _httpFactory();
            http.Timeout = TimeSpan.FromSeconds(120);
            http.BaseAddress = new Uri(string.IsNullOrWhiteSpace(_options.BaseUrl) ? "https://openrouter.ai/api/v1/" : _options.BaseUrl);
            http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {_options.ApiKey}");
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", LlmProtocol.BrowserUserAgent);

            var temporal = PromptBuilders.BuildTemporalContext(_options.Timezone);

            if (!toolsEnabled)
            {
                var messages = BuildRawMessages(transcript, request.History, _options.SystemPrompt, temporal);
                var (chunks, text) = await StreamSpeakerAsync(http, _options.SpeakerModel, messages, onChunk, ct);
                return Finish(chunks, text, sw, 0, 0, usedTools: false);
            }

            return await ExtractorSpeakerAsync(http, request, extractor!, temporal, sw, onChunk, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[llm] turn failed — {Error}. Fix: check the LLM provider key/model and network", ex.Message);
            OnError?.Invoke($"provider call failed: {ex.Message}");
            return null;
        }
    }

    // ---- Multi-model path (W14) ----

    private async Task<ChatResult?> ExtractorSpeakerAsync(
        HttpClient http, ChatRequest request, string extractor, string? temporal,
        System.Diagnostics.Stopwatch sw, Action<string>? onChunk, CancellationToken ct)
    {
        var transcript = request.Transcript.Trim();
        var toolResults = new List<(string Tool, string Result)>();

        var toolsArray = request.Tools!.Select(t => new
        {
            type = "function",
            function = new
            {
                name = t.Name,
                description = t.Description,
                parameters = JsonSerializer.Deserialize<JsonElement>(t.ParametersJson)
            }
        }).ToArray();

        var extractorMessages = PromptBuilders.BuildExtractorMessages(transcript, request.Tools, request.History);
        var extractorRoute = PromptBuilders.BuildProviderRoute(_options.ModelProvidersJson, extractor);

        if (_options.LogConversationDebug)
        {
            var promptJson = JsonSerializer.Serialize(extractorMessages, ConversationJson.Options);
            _logger.LogInformation("[llm-debug] extractor prompt ({Count} messages, {Chars}c): {Prompt}",
                extractorMessages.Count, promptJson.Length, promptJson[..Math.Min(2000, promptJson.Length)]);
        }

        object body = extractorRoute != null
            ? new { model = extractor, messages = extractorMessages, stream = false, max_tokens = 2048, tools = toolsArray, tool_choice = "auto", provider = extractorRoute }
            : new { model = extractor, messages = extractorMessages, stream = false, max_tokens = 2048, tools = toolsArray, tool_choice = "auto" };

        using var content = JsonContent.Create(body, options: ConversationJson.Options);
        using var response = await http.PostAsync("chat/completions", content, ct);
        var extractorMs = sw.ElapsedMilliseconds;

        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(ct);
            if (LlmProtocol.IsToolsUnsupportedError((int)response.StatusCode, err))
            {
                // D5: the extractor has no tool-capable endpoint (e.g. a Route pin to a provider
                // that doesn't serve it). Degrade to the honest no-data turn rather than
                // handing tools to the persona-bearing speaker (its tool discipline is weak).
                _logger.LogWarning("[llm] extractor '{Extractor}' has no tool-capable endpoint ({Status}: {Error}) — answering without tool data. " +
                    "Fix: pick an extractor/route that supports tool use", extractor, (int)response.StatusCode, err[..Math.Min(200, err.Length)]);
            }
            else
            {
                _logger.LogError("[llm] extractor pass failed {Status}: {Error}", (int)response.StatusCode, err[..Math.Min(300, err.Length)]);
                return null;
            }
        }
        else
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var choice = doc.RootElement.GetProperty("choices")[0];
            var hasMessage = choice.TryGetProperty("message", out var msg);
            JsonElement tc = default;
            var hasToolCalls = hasMessage && msg.TryGetProperty("tool_calls", out tc) && tc.ValueKind == JsonValueKind.Array;

            if (hasToolCalls)
            {
                _logger.LogInformation("[llm] extractor: {Count} tool call(s)", tc.GetArrayLength());
                foreach (var call in tc.EnumerateArray())
                {
                    var name = call.GetProperty("function").GetProperty("name").GetString()!;
                    var args = call.GetProperty("function").GetProperty("arguments").GetString() ?? "{}";
                    _logger.LogInformation("[llm] extractor tool call: {Name}({Args})", name, args[..Math.Min(200, args.Length)]);

                    var result = _tools != null
                        ? await ExecuteToolAsync(name, args, request, ct)
                        : $"Tool '{name}' is unavailable in this client (no tool executor configured).";
                    toolResults.Add((name, result));
                    if (_options.LogConversationDebug)
                        _logger.LogInformation("[llm-debug] tool result {Name}: {Result}", name, result[..Math.Min(2000, result.Length)]);
                }
            }
            else
            {
                // No tool calls — chitchat ("none") or a capability question ("capabilities").
                var reply = hasMessage && msg.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
                    ? c.GetString() ?? "" : "";
                var capabilityTurn = reply.Contains("capabilities", StringComparison.OrdinalIgnoreCase)
                    || PromptBuilders.LooksLikeCapabilityQuestion(transcript);
                if (capabilityTurn)
                {
                    _logger.LogInformation("[llm] capability question — injecting real tool inventory into the speaker's data block");
                    toolResults.Add(("capabilities", PromptBuilders.BuildToolInventory(request.Tools)));
                }
                else
                {
                    _logger.LogInformation("[llm] extractor returned no tool calls (chitchat turn)");
                }
            }
        }

        // Speaker pass — streaming, no tools, persona + compacted data.
        var speakerMessages = PromptBuilders.BuildSpeakerMessages(transcript, toolResults, request.History, _options.SystemPrompt, temporal);
        if (_options.LogConversationDebug)
        {
            var promptJson = JsonSerializer.Serialize(speakerMessages, ConversationJson.Options);
            _logger.LogInformation("[llm-debug] speaker prompt ({Count} messages, {Chars}c): {Prompt}",
                speakerMessages.Count, promptJson.Length, promptJson[..Math.Min(2000, promptJson.Length)]);
        }

        var speakerMsStart = sw.ElapsedMilliseconds;
        var (chunks, speakerText) = await StreamSpeakerAsync(http, _options.SpeakerModel, speakerMessages, onChunk, ct);
        if (chunks == 0)
        {
            // Empty pass-2 (observed: qwen returns 200 with 0 chunks occasionally) — nudge once.
            _logger.LogWarning("[llm] speaker returned no chunks — retrying once with a nudge");
            speakerMessages.Add(new { role = "system", content = "Reply to the user now, in one or two spoken sentences." });
            (chunks, speakerText) = await StreamSpeakerAsync(http, _options.SpeakerModel, speakerMessages, onChunk, ct);
        }

        var speakerMs = sw.ElapsedMilliseconds - speakerMsStart;
        _logger.LogInformation("[llm] multi-model turn: extractor={Extractor} speaker={Speaker} extractorMs={Ex}ms speakerMs={Sp}ms",
            extractor, _options.SpeakerModel, extractorMs, speakerMs);

        if (chunks == 0)
        {
            const string fallback = "Done — anything else?";
            onChunk?.Invoke(fallback);
            return new ChatResult(fallback, 1, sw.ElapsedMilliseconds, extractorMs, speakerMs, toolResults.Count > 0);
        }
        return Finish(chunks, speakerText, sw, extractorMs, speakerMs, toolResults.Count > 0);
    }

    private async Task<string> ExecuteToolAsync(string name, string argsJson, ChatRequest request, CancellationToken ct)
    {
        try
        {
            return await _tools!.ExecuteAsync(name, argsJson, new ToolExecutionContext(request.UserId, request.DeviceId, _options.Timezone), ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[llm] tool '{Name}' failed — {Error}. Fix: check the tool implementation / host tool endpoint", name, ex.Message);
            return $"Error executing tool '{name}': {ex.Message}";
        }
    }

    // ---- Raw LLM path (no tools) ----

    private List<object> BuildRawMessages(string transcript, List<ChatHistoryMessage>? history, string? persona, string? temporal)
    {
        var messages = new List<object> { new { role = "system", content = PromptConstants.VoiceGuidanceBlock } };
        // Persona BEFORE anything operational (identity first, tools second — 2026-08-12 ordering).
        if (!string.IsNullOrWhiteSpace(persona)) messages.Add(new { role = "system", content = persona });
        if (!string.IsNullOrWhiteSpace(temporal)) messages.Add(new { role = "system", content = temporal });

        if (history is { Count: > 0 })
        {
            foreach (var h in history.Take(PromptConstants.MaxHistoryMessages))
            {
                if (h.Role is not ("user" or "assistant")) continue;
                var text = (h.Content ?? "").Trim();
                if (text.Length == 0) continue;
                if (text.Length > PromptConstants.MaxHistoryCharsPerMessage)
                    text = text[..PromptConstants.MaxHistoryCharsPerMessage] + "…";
                messages.Add(new { role = h.Role, content = text });
            }
        }
        messages.Add(new { role = "user", content = transcript });
        return messages;
    }

    // ---- Streaming pass ----

    private async Task<(int Chunks, string Text)> StreamSpeakerAsync(
        HttpClient http, string model, List<object> messages, Action<string>? onChunk, CancellationToken ct)
    {
        var route = PromptBuilders.BuildProviderRoute(_options.ModelProvidersJson, model);
        object body = route != null
            ? new { model, messages, stream = true, max_tokens = 2048, provider = route }
            : new { model, messages, stream = true, max_tokens = 2048 };

        using var content = JsonContent.Create(body, options: ConversationJson.Options);
        using var response = await http.PostAsync("chat/completions", content, ct);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("[llm] speaker pass failed {Status}: {Error}. Fix: check model '{Model}' and the API key",
                (int)response.StatusCode, err[..Math.Min(300, err.Length)], model);
            OnError?.Invoke($"speaker pass {Describe((int)response.StatusCode)}: {Clip(err)}");
            return (0, "");
        }

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        var chunks = 0;
        var text = new StringBuilder();

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            if (!line.StartsWith("data: ")) continue;
            var data = line["data: ".Length..];
            if (data == "[DONE]") break;

            try
            {
                using var doc = JsonDocument.Parse(data);
                if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                {
                    var choice = choices[0];
                    if (choice.TryGetProperty("delta", out var delta)
                        && delta.TryGetProperty("content", out var contentEl)
                        && contentEl.ValueKind == JsonValueKind.String)
                    {
                        var chunk = contentEl.GetString();
                        if (!string.IsNullOrEmpty(chunk))
                        {
                            chunks++;
                            text.Append(chunk);
                            onChunk?.Invoke(chunk);
                        }
                    }
                }
                else if (doc.RootElement.TryGetProperty("error", out var errEl))
                {
                    var errMsg = errEl.TryGetProperty("message", out var m) ? m.GetString() : errEl.GetRawText();
                    _logger.LogError("[llm] provider error mid-stream: {Error}. Fix: check model '{Model}'", errMsg, model);
                    break;
                }
            }
            catch (JsonException)
            {
                // Unparseable SSE line — skip (keep-alives, partial frames).
            }
        }

        if (text.Length > 0 && _options.LogConversationDebug)
            _logger.LogInformation("[llm-debug] response text ({Chars}c): {Text}", text.Length, text.ToString());
        return (chunks, text.ToString());
    }

    private static ChatResult Finish(int chunks, string text, System.Diagnostics.Stopwatch sw, long extractorMs, long speakerMs, bool usedTools)
        => new(text, chunks, sw.ElapsedMilliseconds, extractorMs, speakerMs, usedTools);

    /// <summary>Turns an HTTP status into a phrase that names the likely fix.</summary>
    private static string Describe(int status) => status switch
    {
        401 or 403 => $"{status} — the API key was rejected (check the key in Settings)",
        404 => "404 — the model id does not exist for this provider",
        429 => "429 — rate limited (retry shortly, or pick a less busy model)",
        _ => status.ToString()
    };

    /// <summary>Provider error bodies are JSON/HTML blobs; keep the surfaced message short.</summary>
    private static string Clip(string body) =>
        body.Length <= 200 ? body : body[..200] + "…";
}
