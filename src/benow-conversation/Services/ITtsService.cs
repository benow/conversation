namespace benow_conversation.Services;

public interface ITtsService
{
    Task<string> SynthesizeToFileAsync(string text, string? outputFileName = null, string? voice = null, string? instructions = null, double? temperature = null, int? seed = null, string? model = null);
    Task<(Stream AudioStream, string Format)> SynthesizeToStreamAsync(string text, string? voice = null, string? instructions = null, double? temperature = null, int? seed = null, string? model = null);
    Task<List<string>> SynthesizeAllVoicesAsync(string text, string? outputFileName = null, string? instructions = null, double? temperature = null, int? seed = null);
    Task<List<string>> SynthesizeAllModelsAsync(string text, string? outputFileName = null, string? voice = null, string? instructions = null, double? temperature = null, int? seed = null);
    Task<List<string>> SynthesizeAllProvidersAsync(string text, string? outputFileName = null, string? instructions = null, double? temperature = null, int? seed = null);
}
