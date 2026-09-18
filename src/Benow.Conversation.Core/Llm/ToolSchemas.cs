namespace Benow.Conversation.Llm;

/// <summary>Wire DTO: one conversation history entry (role whitelisted user/assistant upstream).</summary>
public record ChatHistoryMessage(string Role, string Content);

/// <summary>Wire DTO: a tool schema as shipped by the host/tool registry with the chat request.</summary>
public record ToolSchemaDto(string Name, string Description, string ParametersJson);
