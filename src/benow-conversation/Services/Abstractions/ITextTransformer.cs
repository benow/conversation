namespace benow_conversation.Services.Abstractions;

public interface ITextTransformer
{
    Task<string> TransformAsync(string input, CancellationToken ct = default);
}
