using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using benow_conversation.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace benow_conversation.Services;

/// <summary>Normalizes prose-style multi-character dialogue into [Name:F] bracket format by calling OpenRouter chat completions.</summary>
public class CharacterNormalizer : ICharacterNormalizer
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AppSettings _settings;
    private readonly ILogger<CharacterNormalizer> _logger;

    public static readonly string DefaultSystemPrompt = @"You are a script formatter that converts narrative prose into a structured multi-character script for text-to-speech. Your job is to identify who is speaking, what is narration, and what is inner thought, then output everything in the proper format.

## Output Format

[Narrator:F] *narration text describing the scene, actions of others, and atmosphere*
[Self] *narration describing your actions, movements, and sensory experience*
[Self] dialogue spoken aloud by you (the primary participant)
[Self] [thought]your inner thoughts, realizations, and unspoken reactions[/thought]
[OtherName:F] dialogue spoken aloud by that character
[OtherName:F] *actions or gestures performed by that character*
[OtherName:F] [thought]that character's inner thoughts[/thought]

## Character Classes

Narrator:F — Female narrator for all third-person narration, scene-setting, and descriptions of other characters' actions.
Self — Primary participant. YOU. Any action, dialogue, or thought attributed to ""you"" or ""your"". No gender suffix — gender is determined by your self persona configuration.

## Rules

1. Self identification: Any text describing what ""you"" do, say, think, feel, or experience goes under [Self]. This includes:
   - Your physical actions: ""you stand up"", ""you reach out"", ""you walk across the room"" → [Self] *you stand up*
   - Your spoken words: ""you say"", ""you ask"", ""you whisper"", ""you reply"" → [Self] the spoken words
   - Your inner thoughts: ""you think"", ""you wonder"", ""you realize"" → [Self] [thought]...[/thought]
   - Your body/sensations: ""your cock"", ""your hand"", ""you feel"" → [Self] *you feel* (narration) or [Self] [thought]you feel[/thought] (internal)
   - Your dialogue in quotes: ""I think..."", ""Come here"" when you are speaking → [Self] the dialogue

2. Narration: All other descriptive text, scene-setting, and other characters' actions goes under [Narrator:F] wrapped in *asterisks*.

3. Direct dialogue: Other characters' quoted speech gets [CharacterName:F] marker. Extract the character name from the attribution (e.g., she says, Sarah replies).

4. Dialogue without quotes: If a character says or speaks something without explicit quotation marks, identify what they said and format it as dialogue under their name. For you (Self), always use [Self].

5. Actions from a character's perspective: If narration describes a specific character's actions before/after their dialogue, group them under that character. For you, use [Self].

6. Character identification: Extract names from the text. Default gender to :F unless the text clearly indicates male. Use the exact name.

7. Inner thoughts: Text describing what a character thinks, wonders, realizes, or feels internally goes in [thought]...[/thought] under that character.

8. Dialogue attribution: When text says ""she says, her voice hesitant"" followed by dialogue, the attribution becomes narration under that character's name.

9. Character name as subject: When a character name appears as the subject of a sentence in the original text, KEEP that name in the narrative text — do not replace it with a pronoun. The bracket marker already identifies the character, but the spoken text must include the name when the original uses it. Example: ""Rachel smiles and answers"" → [Rachel:F] *Rachel smiles and answers*, NOT [Rachel:F] *smiles and answers*. Only use pronouns when the original text uses pronouns.

10. PRESERVE ALL TEXT: Every word from the original must appear somewhere. Do not summarize, add, or remove content.

11. Multiple characters in one block: When the narrative shifts between characters, create separate segments for each. Switch between Narrator:F, Self, and other characters as needed.

## Examples

Input:
You stand up from the chair and walk toward Sarah. She looks up at you with a mix of excitement and nervousness. She takes a deep breath and begins to speak. ""I've been thinking about this for a while,"" she admits, her cheeks flushing.

Output:
[Self] *You stand up from the chair and walk toward Sarah*
[Narrator:F] *She looks up at you with a mix of excitement and nervousness*
[Narrator:F] *She takes a deep breath and begins to speak*
[Sarah:F] I've been thinking about this for a while
[Sarah:F] *her cheeks flushing*
[Narrator:F] *she admits*

Input:
The remaining girls look at each other, some of them blushing or giggling at the request. One of them, Sarah, speaks up. ""Okay, I'll try,"" she says, her voice a little hesitant.

Output:
[Narrator:F] *The remaining girls look at each other, some of them blushing or giggling at the request*
[Narrator:F] *One of them, Sarah, speaks up*
[Sarah:F] Okay, I'll try
[Narrator:F] *she says, her voice a little hesitant*

Input:
You lay Sarah down on the bench and lift her legs. She looks up at you with a mix of excitement and nervousness. She can feel your cock pushing against her entrance, and she knows that this is a moment of truth. You whisper in her ear, ""Relax and let it happen.""

Output:
[Self] *You lay Sarah down on the bench and lift her legs*
[Narrator:F] *She looks up at you with a mix of excitement and nervousness*
[Sarah:F] [thought]I can feel his cock pushing against my entrance. This is a moment of truth.[/thought]
[Self] *You whisper in her ear*
[Self] Relax and let it happen

Input:
You feel a surge of excitement as you enter the room. The girls are already there, waiting. You walk over to Rachel and take her hand. She gasps and looks at you with wide eyes. ""I've been hoping you'd come,"" she breathes.

Output:
[Self] [thought]A surge of excitement rushes through you[/thought]
[Narrator:F] *The girls are already there, waiting*
[Self] *You walk over to Rachel and take her hand*
[Narrator:F] *She gasps and looks at you with wide eyes*
[Rachel:F] I've been hoping you'd come
[Narrator:F] *she breathes*

Output ONLY the formatted script. No preamble, no explanation. Every line MUST start with a marker: [Narrator:F], [Self], [Name:F], or [Name:M].";

    public CharacterNormalizer(IHttpClientFactory httpClientFactory, IOptions<AppSettings> settings, ILogger<CharacterNormalizer> logger)
    {
        _httpClientFactory = httpClientFactory;
        _settings = settings.Value;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<string> NormalizeAsync(string text, CancellationToken ct)
    {
        if (!_settings.MultiCharacter.AutoNormalize)
        {
            _logger.LogInformation("Character normalization skipped (disabled)");
            return text;
        }

        var modelId = _settings.MultiCharacter.NormalizerModel;
        if (string.IsNullOrEmpty(modelId))
            modelId = _settings.Proxy.BackendModel;
        if (string.IsNullOrEmpty(modelId))
        {
            _logger.LogWarning("No normalizer model configured");
            return text;
        }

        var timeout = TimeSpan.FromMilliseconds(_settings.MultiCharacter.NormalizerTimeoutMs);
        var systemPrompt = string.IsNullOrEmpty(_settings.MultiCharacter.NormalizerSystemPrompt)
            ? DefaultSystemPrompt
            : _settings.MultiCharacter.NormalizerSystemPrompt;

        return await TryModelAsync(modelId, text, systemPrompt, timeout, ct);
    }

    private async Task<string> TryModelAsync(string modelId, string text, string systemPrompt, TimeSpan timeout, CancellationToken ct)
    {
        HttpClient client;
        try
        {
            client = _httpClientFactory.CreateClient("CharacterNormalizer");
        }
        catch
        {
            client = _httpClientFactory.CreateClient("OpenRouter");
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            var sw = Stopwatch.StartNew();
            var requestBody = new JsonObject
            {
                ["model"] = modelId,
                ["temperature"] = 0.1,
                ["messages"] = new JsonArray
                {
                    new JsonObject { ["role"] = "system", ["content"] = systemPrompt },
                    new JsonObject { ["role"] = "user", ["content"] = text }
                }
            };

            var request = new HttpRequestMessage(HttpMethod.Post, _settings.OpenRouter.BaseUrl + "/chat/completions")
            {
                Content = new StringContent(requestBody.ToJsonString(), System.Text.Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.OpenRouter.ApiKey);

            using var response = await client.SendAsync(request, cts.Token);
            response.EnsureSuccessStatusCode();

            var responseBody = await response.Content.ReadAsStringAsync(cts.Token);
            var responseNode = JsonNode.Parse(responseBody);
            var content = responseNode?["choices"]?[0]?["message"]?["content"]?.ToString();

            if (string.IsNullOrEmpty(content))
            {
                _logger.LogWarning("Normalizer model {Model} returned empty content", modelId);
                return text;
            }

            if (content.Length < text.Length * 0.3)
            {
                _logger.LogWarning("Normalizer model {Model} returned suspiciously short output ({Len} vs {Orig}), skipping", modelId, content.Length, text.Length);
                return text;
            }

            var originalAlpha = text.Count(char.IsLetterOrDigit);
            var resultAlpha = content.Count(char.IsLetterOrDigit);
            if (originalAlpha > 0 && (double)resultAlpha / originalAlpha < 0.50)
            {
                _logger.LogWarning("Normalizer model {Model} dropped significant text (alpha ratio {Ratio:F2}), skipping", modelId, (double)resultAlpha / originalAlpha);
                return text;
            }

            _logger.LogInformation("Character normalization succeeded with model {Model} ({Ms}ms)", modelId, sw.ElapsedMilliseconds);
            return content;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogError(ex, "Normalizer model {Model} timed out after {TimeoutMs}ms", modelId, timeout.TotalMilliseconds);
            return text;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Normalizer model {Model} failed: {Error}", modelId, ex.Message);
            return text;
        }
    }
}
