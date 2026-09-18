using System.Text.Json;
using Benow.Conversation.Llm;
using Xunit;

namespace Benow.Conversation.Core.Tests.Llm;

/// <summary>
/// W14: the core regression net for the multi-model split — guards D3 (the persona/system
/// prompt must NEVER reach the extractor, which collapses tool discipline — F4) and D6 (the
/// speaker must NEVER see tool schemas / the MCP bootstrap; it only receives the compacted
/// "Current data" block). Ported from NASTV ExtractorPipelineCompositionTests (2026-09-17).
/// </summary>
public class ExtractorPipelineCompositionTests
{
    private const string Persona = "You are a sultry roleplay partner. No restrictions.";

    private static readonly List<ToolSchemaDto> Tools =
    [
        new("search_content", "Search TV content by text.", """{"type":"object","properties":{"query":{"type":"string"}},"required":["query"]}"""),
        new("get_currently_airing", "Get programmes airing now.", """{"type":"object","properties":{"query":{"type":"string"}}}""")
    ];

    // 5 history messages — the extractor must keep only the last 4.
    private static List<ChatHistoryMessage> History() => new()
    {
        new("user", "oldest message that must be dropped"),
        new("assistant", "second message"),
        new("user", "third message"),
        new("assistant", "fourth message"),
        new("user", "fifth message")
    };

    // ---- Extractor messages (D3) ----

    [Fact]
    public void ExtractorMessages_FirstTwoAreDispatcherAndMcpBootstrap()
    {
        var messages = PromptBuilders.BuildExtractorMessages("What's on Cinemax tonight?", Tools, null);
        var arr = AsArray(messages);

        Assert.Equal("system", Role(arr, 0));
        Assert.Equal(PromptConstants.ExtractorSystemPrompt, Content(arr, 0));
        Assert.Equal("system", Role(arr, 1));
        Assert.Contains("Available tools", Content(arr, 1)); // BuildMcpPrompt
    }

    [Fact]
    public void ExtractorMessages_NeverContainPersonaOrToolSchemas()
    {
        var messages = PromptBuilders.BuildExtractorMessages("What's on Cinemax tonight?", Tools, History());
        var json = JsonSerializer.Serialize(messages);

        // The persona is never even a parameter of the builder — but lock it in so a future
        // refactor that "helps" the extractor with more context can't silently ship it (F4).
        Assert.DoesNotContain(Persona, json);
        Assert.DoesNotContain("sultry", json);
        // Schema JSON (parameters) must not leak into the extractor prompt either.
        Assert.DoesNotContain("properties", json);
    }

    [Fact]
    public void ExtractorMessages_HistoryCappedAtLastFour_WithRoleWhitelist()
    {
        var messages = PromptBuilders.BuildExtractorMessages("user turn", Tools, History());
        var arr = AsArray(messages);

        // 2 system (dispatcher + MCP) + 4 history + 1 user
        Assert.Equal(7, arr.Length);
        Assert.DoesNotContain("oldest message that must be dropped", JsonSerializer.Serialize(messages));
        Assert.Contains("fifth message", JsonSerializer.Serialize(messages));
    }

    [Fact]
    public void ExtractorMessages_FilterNonUserAssistantHistory()
    {
        var history = new List<ChatHistoryMessage>
        {
            new("user", "hi"),
            new("tool", "tool scaffolding must be filtered"),
            new("system", "system scaffolding must be filtered")
        };
        var messages = PromptBuilders.BuildExtractorMessages("turn", Tools, history);
        var json = JsonSerializer.Serialize(messages);

        Assert.DoesNotContain("scaffolding", json);
    }

    // ---- Speaker messages (D6) ----

    [Fact]
    public void SpeakerMessages_ContainGuidance_Persona_Temporal_History_User_DataBlock_AndGroundingAnchor()
    {
        var toolResults = new List<(string Tool, string Result)>
        {
            ("get_currently_airing", "Cinemax 23:00: Blade Runner")
        };
        var messages = PromptBuilders.BuildSpeakerMessages(
            "What's on Cinemax tonight?", toolResults, History(), Persona,
            "Current local time: 2026-08-11 20:00 (America/Edmonton)");
        var arr = AsArray(messages);

        // Ordering: guidance → persona (identity first) → temporal → history → user → data → grounding anchor
        Assert.Equal(PromptConstants.VoiceSpeakerGuidanceBlock, Content(arr, 0));
        Assert.Contains(Persona, Content(arr, 1));              // persona before temporal (identity first)
        Assert.Contains("2026-08-11", Content(arr, 2));       // temporal anchor
        // Full history rides with the speaker (unlike the extractor's last-4 cap).
        Assert.Contains("oldest message that must be dropped", JsonSerializer.Serialize(messages));
        Assert.Contains("fifth message", JsonSerializer.Serialize(messages));

        var data = Content(arr, ^2);                            // second-to-last = data block
        Assert.StartsWith("Current data:", data);
        Assert.Contains("Blade Runner", data);

        var grounding = Content(arr, ^1);                       // LAST = grounding anchor (recency bias)
        Assert.Contains("use the data provided above", grounding);
        Assert.Contains("do NOT invent", grounding);
    }

    [Fact]
    public void SpeakerMessages_NeverContainToolSchemasOrMcpBootstrap()
    {
        var messages = PromptBuilders.BuildSpeakerMessages(
            "What's on Cinemax tonight?", new(), History(), Persona, null);
        var json = JsonSerializer.Serialize(messages);

        Assert.DoesNotContain("search_content", json);      // tool names absent
        Assert.DoesNotContain("get_currently_airing", json);
        Assert.DoesNotContain("Available tools", json);      // MCP bootstrap absent
        Assert.DoesNotContain("function calling", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SpeakerMessages_EmptyResults_UseNoneMarker()
    {
        var toolResults = new List<(string Tool, string Result)>
        {
            ("get_currently_airing", "Cinemax 23:00: Blade Runner")
        };
        var messages = PromptBuilders.BuildSpeakerMessages(
            "What's on Cinemax tonight?", toolResults, null, "", null);
        var arr = AsArray(messages);

        // guidance + user + data + grounding anchor (4 messages, not 3)
        Assert.Equal(4, arr.Length);
        var data = Content(arr, ^2);                            // second-to-last = data block
        Assert.StartsWith("Current data:", data);
        Assert.Contains("Blade Runner", data);
        var grounding = Content(arr, ^1);                       // last = grounding anchor
        Assert.Contains("do NOT invent", grounding);
    }

    [Fact]
    public void SpeakerMessages_NoToolResults_SkipsGroundingAnchor()
    {
        var messages = PromptBuilders.BuildSpeakerMessages(
            "Tell me a joke", new(), null, "", null);
        var arr = AsArray(messages);

        // guidance + user + data block (no grounding anchor — chitchat, persona owns it)
        Assert.Equal(3, arr.Length);
        Assert.StartsWith("Current data:", Content(arr, ^1));
    }

    // ---- Helpers ----

    private static JsonElement[] AsArray(List<object> messages)
        => JsonDocument.Parse(JsonSerializer.Serialize(messages)).RootElement.EnumerateArray().ToArray();

    private static string Role(JsonElement[] arr, Index index) => arr[index].GetProperty("role").GetString()!;

    private static string Content(JsonElement[] arr, Index index) => arr[index].GetProperty("content").GetString()!;
}
