using System.Text.Json;

namespace Benow.Conversation;

/// <summary>
/// Shared wire-JSON options for every external payload (provider APIs, stored config blobs,
/// host ↔ plugin bodies): camelCase serialization + case-insensitive deserialization.
/// Mirrors NastvShared.JsonDefaults — the case-sensitivity bug class this prevents is
/// documented in NASTV AGENTS.md ("JSON Conventions"): default options are case-SENSITIVE,
/// so camelCase wire fields silently fail to bind to PascalCase DTOs.
/// </summary>
public static class ConversationJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };
}
