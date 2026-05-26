using System.Text.Json.Serialization;

namespace benow_conversation.Models;

public class TtsRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("input")]
    public string Input { get; set; } = "";

    [JsonPropertyName("voice")]
    public string Voice { get; set; } = "alloy";

    [JsonPropertyName("response_format")]
    public string ResponseFormat { get; set; } = "mp3";

    [JsonPropertyName("temperature")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Temperature { get; set; }

    [JsonPropertyName("seed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Seed { get; set; }

    [JsonPropertyName("provider")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ProviderOptions? Provider { get; set; }
}

public class ProviderOptions
{
    [JsonPropertyName("options")]
    public Dictionary<string, Dictionary<string, string>> Options { get; set; } = new();
}
