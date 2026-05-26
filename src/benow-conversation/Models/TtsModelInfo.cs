namespace benow_conversation.Models;

public class TtsModelInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public double PromptPricePerMillionChars { get; set; }
    public double CompletionPricePerMillionChars { get; set; }
    public int VoiceCount { get; set; }
    public int ContextLength { get; set; }
}

public class VoiceInfo
{
    public string Id { get; set; } = "";
    public string Description { get; set; } = "";
}
