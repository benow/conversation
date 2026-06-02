using System.Text.RegularExpressions;
using benow_conversation.Models;

namespace benow_conversation.Services;

/// <summary>Stateless parser that splits multi-character scripts into ordered <see cref="CharacterSegment"/> instances.</summary>
public static partial class CharacterParser
{
    [GeneratedRegex(@"\[(?!/?thought\b)[^\]]+\]", RegexOptions.IgnoreCase)]
    private static partial Regex CharacterMarkerRegex();

    [GeneratedRegex(@"[:\-]\s*([FMfm])(?:emale|ale)?\s*$")]
    private static partial Regex GenderSuffixRegex();

    [GeneratedRegex(@"\s+([FMfm])(?:emale|ale)?\s*$")]
    private static partial Regex GenderSpaceRegex();

    [GeneratedRegex(@"\[[^\]]*\]")]
    private static partial Regex AnyBracketRegex();

    private static readonly HashSet<string> KnownModifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        "whisper", "laughing", "thoughtful", "angry", "sad", "excited",
        "sigh", "quiet", "narrate", "flirtatious", "teasing", "sudden", "thirsty"
    };

    /// <summary>Parses a multi-character script string into a list of character segments.</summary>
    public static List<CharacterSegment> Parse(string text)
    {
        var segments = new List<CharacterSegment>();
        if (string.IsNullOrWhiteSpace(text)) return segments;

        var matches = CharacterMarkerRegex().Matches(text);

        if (matches.Count == 0)
        {
            ExtractSegmentsFromBlock(text, "", "F", extractModifier: false, segments);
            Renumber(segments);
            return segments;
        }

        var preText = text[..matches[0].Index];
        ExtractSegmentsFromBlock(preText, "", "F", extractModifier: false, segments);

        for (int i = 0; i < matches.Count; i++)
        {
            var rawContent = matches[i].Value[1..^1].Trim();
            var (name, gender) = ParseNameAndGender(rawContent);

            int start = matches[i].Index + matches[i].Length;
            int end = (i + 1 < matches.Count) ? matches[i + 1].Index : text.Length;
            var block = text[start..end];

            ExtractSegmentsFromBlock(block, name, gender, extractModifier: true, segments);
        }

        Renumber(segments);
        return segments;
    }

    internal static (string Name, string Gender) ParseNameAndGender(string raw)
    {
        var m = GenderSuffixRegex().Match(raw);
        if (m.Success)
            return (raw[..m.Index].Trim(), char.ToUpperInvariant(m.Groups[1].Value[0]).ToString());

        m = GenderSpaceRegex().Match(raw);
        if (m.Success)
            return (raw[..m.Index].Trim(), char.ToUpperInvariant(m.Groups[1].Value[0]).ToString());

        return (raw.Trim(), "F");
    }

    private static void ExtractSegmentsFromBlock(string text, string characterName, string gender, bool extractModifier, List<CharacterSegment> segments)
    {
        text = text.Trim();
        if (string.IsNullOrEmpty(text)) return;

        string? modifier = null;

        if (extractModifier)
        {
            var modMatch = Regex.Match(text, @"^\(([^)]+)\)");
            if (modMatch.Success)
            {
                var candidate = modMatch.Groups[1].Value.Trim();
                if (candidate.Length <= 30 && KnownModifiers.Contains(candidate))
                {
                    modifier = candidate;
                    text = text[modMatch.Length..].Trim();
                }
            }
        }

        if (string.IsNullOrEmpty(text)) return;

        var thoughtStart = text.IndexOf("[thought]", StringComparison.OrdinalIgnoreCase);
        if (thoughtStart < 0)
        {
            AddNarratedParts(text, characterName, gender, modifier, isThought: false, segments);
            return;
        }

        var preThought = text[..thoughtStart].Trim();
        if (!string.IsNullOrWhiteSpace(preThought))
            AddNarratedParts(preThought, characterName, gender, modifier, isThought: false, segments);

        var contentStart = thoughtStart + "[thought]".Length;
        var thoughtEnd = text.IndexOf("[/thought]", contentStart, StringComparison.OrdinalIgnoreCase);

        string thoughtText;
        string? postThought = null;
        if (thoughtEnd >= 0)
        {
            thoughtText = text[contentStart..thoughtEnd].Trim();
            postThought = text[(thoughtEnd + "[/thought]".Length)..].Trim();
        }
        else
        {
            thoughtText = text[contentStart..].Trim();
        }

        if (!string.IsNullOrWhiteSpace(thoughtText))
            AddNarratedParts(thoughtText, characterName, gender, modifier, isThought: true, segments);

        if (!string.IsNullOrWhiteSpace(postThought))
            AddNarratedParts(postThought, characterName, gender, modifier, isThought: false, segments);
    }

    private static void AddNarratedParts(string text, string characterName, string gender, string? modifier, bool isThought, List<CharacterSegment> segments)
    {
        text = StripStrayMarkers(text);
        if (string.IsNullOrWhiteSpace(text)) return;

        foreach (var (partText, isNarration) in SplitNarrationParts(text))
        {
            var cleaned = partText.Trim();
            if (string.IsNullOrEmpty(cleaned)) continue;
            segments.Add(new CharacterSegment
            {
                CharacterName = characterName,
                Gender = gender,
                SpokenText = cleaned,
                Modifier = modifier,
                IsThought = isThought,
                IsNarration = isNarration
            });
        }
    }

    internal static string StripStrayMarkers(string text)
    {
        var charMarkerMatches = CharacterMarkerRegex().Matches(text);
        if (charMarkerMatches.Count == 0) return text.Trim();

        var cleaned = text;
        for (int i = charMarkerMatches.Count - 1; i >= 0; i--)
        {
            cleaned = cleaned.Remove(charMarkerMatches[i].Index, charMarkerMatches[i].Length);
        }
        return cleaned.Trim();
    }

    private static List<(string Text, bool IsNarration)> SplitNarrationParts(string text)
    {
        var result = new List<(string Text, bool IsNarration)>();
        var parts = text.Split('*');

        for (int i = 0; i < parts.Length; i++)
        {
            var part = parts[i].Trim();
            if (string.IsNullOrEmpty(part)) continue;
            result.Add((part, i % 2 == 1));
        }

        return result;
    }

    private static void Renumber(List<CharacterSegment> segments)
    {
        for (int i = 0; i < segments.Count; i++)
        {
            segments[i] = segments[i] with { SequenceIndex = i };
        }
    }
}
