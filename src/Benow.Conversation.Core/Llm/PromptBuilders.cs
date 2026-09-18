using System.Text;
using System.Text.RegularExpressions;

namespace Benow.Conversation.Llm;

/// <summary>
/// Pure prompt/message builders for the extractor/speaker turn strategy — ported from NASTV
/// VoiceEndpoints statics (2026-09-17, phase 1). Invariants guarded by tests:
///   D3/F4 — the persona/system prompt NEVER reaches the extractor (tool discipline is absolute).
///   D6    — the speaker NEVER sees tool schemas or the MCP bootstrap (only the compacted data block).
///   Order — persona first (identity), grounding anchor LAST (recency bias).
/// </summary>
public static class PromptBuilders
{
    /// <summary>
    /// Dynamically generated description of the MCP resources available to the model —
    /// the tool schemas shipped with the request. Emitted as its OWN system message BEFORE
    /// the system prompt (bootstrap or custom), so the tool inventory is always present no
    /// matter what prompt the user configured. Returns null when no tools were supplied.
    /// </summary>
    public static string? BuildMcpPrompt(List<ToolSchemaDto>? tools)
    {
        if (tools is not { Count: > 0 }) return null;

        var sb = new StringBuilder();
        sb.AppendLine("Available tools — call them via function calling when the user asks about or wants to do these things:");
        foreach (var t in tools)
        {
            sb.Append("- ").Append(t.Name).Append(": ").AppendLine(t.Description);
        }
        return sb.ToString();
    }

    /// <summary>
    /// W3 temporal anchor: builds a "current local time" system message from the client's IANA
    /// timezone. Without it the model guesses the date (it once answered "tonight is July 12th" —
    /// a month stale, FM-5). Tool results are already local, so the model never does timezone math.
    /// </summary>
    public static string? BuildTemporalContext(string? timezone)
    {
        var tz = ResolveTimeZone(timezone);
        var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz);
        // Negative offsets need an explicit sign — TimeSpan.ToString(@"hh\:mm") drops it.
        var sign = now.Offset < TimeSpan.Zero ? "-" : "+";
        var offsetLabel = sign + now.Offset.ToString(@"hh\:mm");
        return "Current local time: " + now.ToString("yyyy-MM-dd HH:mm") + " (" + tz.Id + ", UTC" + offsetLabel + "). " +
               "The user speaks in local time — interpret 'tonight', 'now', 'tomorrow' against this. " +
               "All times in tool results are already in this local timezone; never convert or guess times.";
    }

    private static TimeZoneInfo ResolveTimeZone(string? timezone)
    {
        if (!string.IsNullOrWhiteSpace(timezone))
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(timezone!); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        return TimeZoneInfo.Utc;
    }

    /// <summary>
    /// Builds a textual tool catalog describing the available tools and the envelope convention
    /// for models that don't support native function calling.
    /// </summary>
    public static string BuildTextualToolCatalog(List<ToolSchemaDto>? tools)
    {
        if (tools is not { Count: > 0 }) return "";

        var sb = new StringBuilder();
        sb.AppendLine("You have access to tools. To call a tool, respond with a JSON envelope on its own line:");
        sb.AppendLine("```");
        sb.AppendLine("{\"tool\": \"<tool_name>\", \"arguments\": {<arguments as JSON>}}");
        sb.AppendLine("```");
        sb.AppendLine("Available tools:");
        foreach (var t in tools)
        {
            sb.AppendLine($"- {t.Name}: {t.Description}");
            sb.AppendLine($"  Parameters: {t.ParametersJson}");
        }
        sb.AppendLine("After receiving tool results, answer the user's original question using those results.");
        return sb.ToString();
    }

    /// <summary>
    /// Builds a plain-text inventory of the tools available to the assistant (name + description),
    /// injected into the speaker's "Current data" block when the user asks what tools the assistant
    /// has (extractor replies "capabilities"). The speaker answers from the REAL schemas instead of
    /// hallucinating capabilities (2026-08-12).
    /// </summary>
    public static string BuildToolInventory(List<ToolSchemaDto>? tools)
    {
        if (tools is not { Count: > 0 })
            return "No tools are currently available.";

        var sb = new StringBuilder("Available tools:\n");
        foreach (var t in tools)
        {
            sb.Append("- ").Append(t.Name).Append(": ").AppendLine(t.Description);
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Guardrail: detects when a transcript asks about the assistant's own tools/capabilities
    /// ("what tools do you have", "what can you do") even if the extractor model misclassifies
    /// the turn as chitchat. Paired with the extractor's "capabilities" marker so a wrong "none"
    /// reply still lands the real tool inventory in the speaker's data block.
    /// </summary>
    private static readonly Regex CapabilityQuestionRegex = new(
        @"(what|which|list|show|tell|name|describe)\b.{0,40}\b(tools?|functions?|capabilit\w+|skills|commands|things you can do)\b|what can you do",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool LooksLikeCapabilityQuestion(string transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript)) return false;
        return CapabilityQuestionRegex.IsMatch(transcript);
    }

    /// <summary>
    /// W14: message list for the extractor pass (multi-model mode): the dispatcher system
    /// prompt + the MCP/tool bootstrap + (last ≤ExtractorHistoryLimit validated user/assistant
    /// history messages) + the user turn. The persona/system prompt is deliberately NOT
    /// included — it collapses tool discipline (F4).
    /// </summary>
    public static List<object> BuildExtractorMessages(
        string transcript, List<ToolSchemaDto>? tools, List<ChatHistoryMessage>? history)
    {
        var messages = new List<object>
        {
            new { role = "system", content = PromptConstants.ExtractorSystemPrompt }
        };
        var mcpPrompt = BuildMcpPrompt(tools);
        if (mcpPrompt != null)
        {
            messages.Add(new { role = "system", content = mcpPrompt });
        }

        if (history is { Count: > 0 })
        {
            foreach (var h in history.TakeLast(PromptConstants.ExtractorHistoryLimit))
            {
                if (h.Role is not ("user" or "assistant")) continue;
                var content = (h.Content ?? "").Trim();
                if (content.Length == 0) continue;
                if (content.Length > PromptConstants.MaxHistoryCharsPerMessage)
                    content = content[..PromptConstants.MaxHistoryCharsPerMessage] + "…";
                messages.Add(new { role = h.Role, content });
            }
        }
        messages.Add(new { role = "user", content = transcript });
        return messages;
    }

    /// <summary>
    /// W14: message list for the speaker pass (multi-model mode): speaker-guidance + persona (if
    /// set, identity/voice first) + temporal anchor + validated history + the user turn + a
    /// "Current data:" system block with compacted tool results + grounding anchor (last system
    /// message, recency-biased to reinforce data fidelity). Tool schemas/MCP bootstrap are NEVER
    /// included — the speaker never calls tools.
    ///
    /// Ordering principle (prompt construction, 2026-08-12): LLMs weight the last system message
    /// most heavily. Persona sets tone early; grounding anchor lands after the data block so the
    /// model's final framing instruction is "use the data, stay in character, don't fabricate."
    /// </summary>
    public static List<object> BuildSpeakerMessages(
        string transcript,
        List<(string Tool, string Result)> toolResults,
        List<ChatHistoryMessage>? history,
        string? systemPrompt,
        string? temporal)
    {
        var messages = new List<object>
        {
            new { role = "system", content = PromptConstants.VoiceSpeakerGuidanceBlock }
        };
        // Persona first — sets voice/tone/identity before anything else.
        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            messages.Add(new { role = "system", content = systemPrompt });
        }
        if (!string.IsNullOrWhiteSpace(temporal))
        {
            messages.Add(new { role = "system", content = temporal });
        }

        if (history is { Count: > 0 })
        {
            foreach (var h in history.Take(PromptConstants.MaxHistoryMessages))
            {
                if (h.Role is not ("user" or "assistant")) continue;
                var content = (h.Content ?? "").Trim();
                if (content.Length == 0) continue;
                if (content.Length > PromptConstants.MaxHistoryCharsPerMessage)
                    content = content[..PromptConstants.MaxHistoryCharsPerMessage] + "…";
                messages.Add(new { role = h.Role, content });
            }
        }

        messages.Add(new { role = "user", content = transcript });
        messages.Add(new { role = "system", content = "Current data:\n" + CompactToolResults(toolResults) });
        // Grounding anchor: LAST system message, ONLY when tool results are present.
        // Recency bias makes this the model's final framing instruction — the model stays in
        // character but cannot ignore or fabricate around the tool results. Chitchat turns (no
        // tool results) skip this so the persona owns the conversation freely.
        if (toolResults.Count > 0)
        {
            messages.Add(new { role = "system", content = PromptConstants.SpeakerGroundingAnchor });
        }
        return messages;
    }

    /// <summary>
    /// W14: compacts executed tool results into a single "Current data" block for the speaker
    /// model. Each result becomes a "[tool]: …" line; the whole block is capped so the
    /// speaker's context stays small — the tools already filtered the data via their query
    /// params (FM-9 lesson). Empty list → a "(none…)" marker so the speaker knows no data was
    /// retrieved this turn and never fabricates a schedule.
    /// </summary>
    public static string CompactToolResults(List<(string Tool, string Result)> results)
    {
        if (results is not { Count: > 0 })
            return "(none — no tools were called this turn.)";

        var sb = new StringBuilder();
        foreach (var (tool, result) in results)
        {
            var line = $"[{tool}]: {(result ?? "").Trim()}\n";
            if (sb.Length + line.Length > PromptConstants.CompactDataBlockCap)
            {
                var room = PromptConstants.CompactDataBlockCap - sb.Length;
                if (room > 1) sb.Append(line[..Math.Min(line.Length, room)]);
                break;
            }
            sb.Append(line);
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>W14: the configured extractor model id, or null in single-model mode.
    /// Empty/"None" means tool calling is disabled (raw LLM path).</summary>
    public static string? ResolveExtractorModel(string? extractorModel)
    {
        var model = extractorModel?.Trim();
        return string.IsNullOrWhiteSpace(model) ? null : model;
    }

    /// <summary>
    /// OpenRouter provider-routing pin for a model (ModelProviders config, e.g.
    /// {"meta-llama/llama-3.3-70b-instruct":"deepinfra"}). Returns the `provider` request object
    /// OpenRouter expects — order:[slug], no fallbacks — or null for default routing. Only
    /// meaningful for OpenRouter models; harmless to send for others (they ignore it).
    /// </summary>
    public static object? BuildProviderRoute(string? modelProvidersJson, string model)
    {
        var raw = modelProvidersJson;
        if (string.IsNullOrWhiteSpace(raw) || raw == "{}") return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty(model, out var slug) && slug.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var s = slug.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                    return new { order = new[] { s }, allow_fallbacks = false };
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Unparseable routing config — fall back to default routing.
        }
        return null;
    }
}
