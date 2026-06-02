using benow_conversation.Services.Abstractions;

namespace benow_conversation.Services.Stt;

public class NullTextTransformer : ITextTransformer
{
    public Task<string> TransformAsync(string input, CancellationToken ct = default) => Task.FromResult(input);
}
