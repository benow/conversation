using System.Text.Json;
using System.Text.RegularExpressions;

namespace Benow.Conversation.Llm;

/// <summary>
/// Wire-protocol helpers for the LLM turn loop — ported from NASTV VoiceEndpoints statics
/// (2026-09-17, phase 1).
/// </summary>
public static class LlmProtocol
{
    /// <summary>
    /// Browser-like User-Agent for outbound AI-provider HTTP calls. Groq/OpenRouter sit
    /// behind Cloudflare, which scores clients with no recognizable User-Agent (.NET's
    /// HttpClient sends NONE by default) as bots and can IP-block after burst patterns.
    /// </summary>
    public const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

    /// <summary>
    /// Detects provider errors meaning "this model has no endpoint that supports function calling"
    /// (OpenRouter 404 "No endpoints found that support tool use" and similar). These are
    /// model-capability mismatches — not API-key or network failures — so the pipeline degrades to
    /// a no-tools turn instead of surfacing a confusing error.
    /// </summary>
    public static bool IsToolsUnsupportedError(int status, string body)
    {
        if (status is not (400 or 404 or 422)) return false;
        var lower = body.ToLowerInvariant();
        return lower.Contains("no endpoints found that support tool")
            || lower.Contains("tool use")
            || lower.Contains("function calling")
            || (lower.Contains("tools") && (lower.Contains("not support") || lower.Contains("unsupported")));
    }

    /// <summary>
    /// Extracts a tool-calling envelope {"tool":"...","arguments":{...}} from LLM output using
    /// brace-walking. Returns null if no valid envelope is found.
    /// </summary>
    public static (string toolName, string argumentsJson)? ExtractToolEnvelope(string text)
    {
        var search = "{\"tool\"";
        var idx = text.IndexOf(search, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;

        // Walk braces to find the matching closing brace
        var start = idx;
        var depth = 0;
        for (var i = start; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}') depth--;

            if (depth == 0)
            {
                try
                {
                    var json = text[start..(i + 1)];
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    if (!root.TryGetProperty("tool", out var toolProp)
                        || toolProp.ValueKind != JsonValueKind.String)
                        return null;

                    var toolName = toolProp.GetString()!;
                    var arguments = root.TryGetProperty("arguments", out var argsProp)
                        ? argsProp.GetRawText()
                        : "{}";

                    return (toolName, arguments);
                }
                catch (JsonException)
                {
                    // Not valid JSON — keep scanning after this position
                    continue;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Splits an LLM content piece for SSE streaming: ≤200-char pieces, preferring a real
    /// sentence boundary (min 40 chars into the piece), then a space.
    /// </summary>
    public static List<string> SplitForStreaming(string content)
    {
        const int maxPiece = 200;
        var pieces = new List<string>();
        var remaining = content;
        while (remaining.Length > 0)
        {
            if (remaining.Length <= maxPiece)
            {
                pieces.Add(remaining);
                break;
            }

            var window = remaining[..maxPiece];
            var cut = maxPiece;
            // Prefer a sentence boundary inside the window (needs to be a real sentence — min 40 chars).
            var term = window.LastIndexOfAny(new[] { '.', '!', '?', '\n' });
            if (term >= 40) cut = term + 1;
            else
            {
                var space = window.LastIndexOf(' ');
                if (space > 0) cut = space;
            }
            pieces.Add(remaining[..cut]);
            remaining = remaining[cut..].TrimStart();
        }
        return pieces;
    }

    /// <summary>
    /// Extracts the "content" string from an anonymous message object built by the prompt
    /// builders (messages are `new { role, content }`).
    /// </summary>
    public static string GetContentFromMessage(object msg)
    {
        var type = msg.GetType();
        var prop = type.GetProperty("content");
        return prop?.GetValue(msg) as string ?? "";
    }

    private static readonly Regex ReplicateModelShape = new(@"^[^/\s]+/[^:\s]+:[A-Za-z0-9]{6,}$", RegexOptions.Compiled);

    /// <summary>
    /// Detects TTS provider/model config mismatches (a Replicate version-hash model on a
    /// non-Replicate provider or vice versa) so synthesis can fall back instead of 400ing.
    /// Returns the provider the model SHAPE belongs to, or null when they match.
    /// </summary>
    public static string? ClassifyModelProviderMismatch(string providerTypeId, string model)
    {
        var replicateShaped = ReplicateModelShape.IsMatch(model);
        if (providerTypeId != "replicate" && replicateShaped) return "replicate";
        if (providerTypeId == "replicate" && !replicateShaped) return "openrouter";
        return null;
    }
}
