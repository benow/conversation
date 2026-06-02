using benow_conversation.Models;
using benow_conversation.Services;

namespace benow_conversation.Tests;

public class CharacterParserTests
{
    [Fact]
    public void SingleCharacter_OneSegment()
    {
        var result = CharacterParser.Parse("[Alice]Hello there");
        Assert.Single(result);
        Assert.Equal("Alice", result[0].CharacterName);
        Assert.Equal("Hello there", result[0].SpokenText);
    }

    [Fact]
    public void MultipleCharacters_CorrectSegments()
    {
        var result = CharacterParser.Parse("[Alice]Hello[Bob]Hi there[Alice]Goodbye");
        Assert.Equal(3, result.Count);
        Assert.Equal("Alice", result[0].CharacterName);
        Assert.Equal("Hello", result[0].SpokenText);
        Assert.Equal("Bob", result[1].CharacterName);
        Assert.Equal("Hi there", result[1].SpokenText);
        Assert.Equal("Alice", result[2].CharacterName);
        Assert.Equal("Goodbye", result[2].SpokenText);
    }

    [Fact]
    public void GenderParsing_ExplicitGender()
    {
        var result = CharacterParser.Parse("[Alice:F]Hello[Bob:M]World");
        Assert.Equal(2, result.Count);
        Assert.Equal("Alice", result[0].CharacterName);
        Assert.Equal("F", result[0].Gender);
        Assert.Equal("Bob", result[1].CharacterName);
        Assert.Equal("M", result[1].Gender);
    }

    [Fact]
    public void ThoughtTags_SplitsPreThoughtAndThought()
    {
        var result = CharacterParser.Parse("[Alice]Hello there.[thought]I wonder about things[/thought]");
        Assert.Equal(2, result.Count);
        Assert.Equal("Alice", result[0].CharacterName);
        Assert.Equal("Hello there.", result[0].SpokenText);
        Assert.False(result[0].IsThought);
        Assert.Equal("Alice", result[1].CharacterName);
        Assert.Equal("I wonder about things", result[1].SpokenText);
        Assert.True(result[1].IsThought);
    }

    [Fact]
    public void ThoughtTags_OnlyThoughtInBlock()
    {
        var result = CharacterParser.Parse("[Alice][thought]I wonder about things[/thought]");
        Assert.Single(result);
        Assert.Equal("Alice", result[0].CharacterName);
        Assert.Equal("I wonder about things", result[0].SpokenText);
        Assert.True(result[0].IsThought);
    }

    [Fact]
    public void ThoughtTags_TextAfterThoughtTag()
    {
        var result = CharacterParser.Parse("[Alice][thought]I wonder.[/thought]And now I speak.");
        Assert.Equal(2, result.Count);
        Assert.True(result[0].IsThought);
        Assert.Equal("I wonder.", result[0].SpokenText);
        Assert.False(result[1].IsThought);
        Assert.Equal("And now I speak.", result[1].SpokenText);
    }

    [Fact]
    public void ThoughtAutoClose_AtCharacterBoundary()
    {
        var result = CharacterParser.Parse("[Alice][thought]I wonder[Bob]Hello there");
        Assert.Equal(2, result.Count);
        Assert.Equal("Alice", result[0].CharacterName);
        Assert.Equal("I wonder", result[0].SpokenText);
        Assert.True(result[0].IsThought);
        Assert.Equal("Bob", result[1].CharacterName);
        Assert.Equal("Hello there", result[1].SpokenText);
        Assert.False(result[1].IsThought);
    }

    [Fact]
    public void ModifierExtraction_AfterMarker()
    {
        var result = CharacterParser.Parse("[Alice](whisper)Hello there");
        Assert.Single(result);
        Assert.Equal("Alice", result[0].CharacterName);
        Assert.Equal("whisper", result[0].Modifier);
        Assert.Equal("Hello there", result[0].SpokenText);
    }

    [Fact]
    public void MidTextModifier_TreatedAsLiteral()
    {
        var result = CharacterParser.Parse("[Alice]Hello (whisper) there");
        Assert.Single(result);
        Assert.Equal("Alice", result[0].CharacterName);
        Assert.Null(result[0].Modifier);
        Assert.Equal("Hello (whisper) there", result[0].SpokenText);
    }

    [Fact]
    public void TextBeforeFirstMarker_EmptyCharacterName()
    {
        var result = CharacterParser.Parse("Hello world[Alice]How are you");
        Assert.Equal(2, result.Count);
        Assert.Equal("", result[0].CharacterName);
        Assert.Equal("Hello world", result[0].SpokenText);
        Assert.Equal("Alice", result[1].CharacterName);
        Assert.Equal("How are you", result[1].SpokenText);
    }

    [Fact]
    public void EmptySegments_Skipped()
    {
        var result = CharacterParser.Parse("[Alice][Bob]Hello");
        Assert.Single(result);
        Assert.Equal("Bob", result[0].CharacterName);
        Assert.Equal(0, result[0].SequenceIndex);
    }

    [Fact]
    public void MixedThoughtAndModifier()
    {
        var result = CharacterParser.Parse("[Alice](whisper)[thought]I hope he doesn't notice.[/thought]");
        Assert.Single(result);
        Assert.Equal("Alice", result[0].CharacterName);
        Assert.Equal("whisper", result[0].Modifier);
        Assert.Equal("I hope he doesn't notice.", result[0].SpokenText);
        Assert.True(result[0].IsThought);
    }

    [Fact]
    public void PreThoughtAndModifier_TwoSegments()
    {
        var result = CharacterParser.Parse("[Alice](whisper)Hello there.[thought]I hope he doesn't notice.[/thought]");
        Assert.Equal(2, result.Count);
        Assert.Equal("Hello there.", result[0].SpokenText);
        Assert.Equal("whisper", result[0].Modifier);
        Assert.False(result[0].IsThought);
        Assert.Equal("I hope he doesn't notice.", result[1].SpokenText);
        Assert.Equal("whisper", result[1].Modifier);
        Assert.True(result[1].IsThought);
    }

    [Fact]
    public void NoMarkers_SingleSegmentWithEmptyName()
    {
        var result = CharacterParser.Parse("Hello world how are you");
        Assert.Single(result);
        Assert.Equal("", result[0].CharacterName);
        Assert.Equal("Hello world how are you", result[0].SpokenText);
    }

    [Fact]
    public void Narration_SplitsIntoDialogueAndNarrationSegments()
    {
        var result = CharacterParser.Parse("[Alice] *chuckles* Hello there *smiles*");
        Assert.Equal(3, result.Count);
        Assert.Equal("chuckles", result[0].SpokenText);
        Assert.True(result[0].IsNarration);
        Assert.Equal("Alice", result[0].CharacterName);
        Assert.Equal("Hello there", result[1].SpokenText);
        Assert.False(result[1].IsNarration);
        Assert.Equal("smiles", result[2].SpokenText);
        Assert.True(result[2].IsNarration);
    }

    [Fact]
    public void Narration_BetweenCharacters()
    {
        var result = CharacterParser.Parse("[Alice] *chuckles* [Bob]Hello");
        Assert.Equal(2, result.Count);
        Assert.Equal("chuckles", result[0].SpokenText);
        Assert.True(result[0].IsNarration);
        Assert.Equal("Alice", result[0].CharacterName);
        Assert.Equal("Hello", result[1].SpokenText);
        Assert.Equal("Bob", result[1].CharacterName);
    }

    [Fact]
    public void Narration_WithThought_AllParts()
    {
        var result = CharacterParser.Parse("[Alice] *raises eyebrow* Hello. [thought] *ponders* I wonder. [/thought] *sighs*");
        Assert.Equal(5, result.Count);
        Assert.Equal("raises eyebrow", result[0].SpokenText);
        Assert.True(result[0].IsNarration);
        Assert.False(result[0].IsThought);
        Assert.Equal("Hello.", result[1].SpokenText);
        Assert.False(result[1].IsNarration);
        Assert.False(result[1].IsThought);
        Assert.Equal("ponders", result[2].SpokenText);
        Assert.True(result[2].IsNarration);
        Assert.True(result[2].IsThought);
        Assert.Equal("I wonder.", result[3].SpokenText);
        Assert.False(result[3].IsNarration);
        Assert.True(result[3].IsThought);
        Assert.Equal("sighs", result[4].SpokenText);
        Assert.True(result[4].IsNarration);
        Assert.False(result[4].IsThought);
    }

    [Fact]
    public void Narration_MultipleInText()
    {
        var result = CharacterParser.Parse("[Alice] *laughs* That's funny! *grins* Seriously though.");
        Assert.Equal(4, result.Count);
        Assert.Equal("laughs", result[0].SpokenText);
        Assert.True(result[0].IsNarration);
        Assert.Equal("That's funny!", result[1].SpokenText);
        Assert.False(result[1].IsNarration);
        Assert.Equal("grins", result[2].SpokenText);
        Assert.True(result[2].IsNarration);
        Assert.Equal("Seriously though.", result[3].SpokenText);
        Assert.False(result[3].IsNarration);
    }

    [Fact]
    public void Narration_WithModifier()
    {
        var result = CharacterParser.Parse("[Alice] (whisper) *leans in* Hello there *smiles*");
        Assert.Equal(3, result.Count);
        Assert.Equal("whisper", result[0].Modifier);
        Assert.Equal("leans in", result[0].SpokenText);
        Assert.True(result[0].IsNarration);
        Assert.Equal("whisper", result[1].Modifier);
        Assert.Equal("Hello there", result[1].SpokenText);
        Assert.False(result[1].IsNarration);
        Assert.Equal("whisper", result[2].Modifier);
        Assert.Equal("smiles", result[2].SpokenText);
        Assert.True(result[2].IsNarration);
    }

    [Fact]
    public void Narration_NoAsterisks_SingleDialogueSegment()
    {
        var result = CharacterParser.Parse("[Alice]Hello there, how are you?");
        Assert.Single(result);
        Assert.Equal("Hello there, how are you?", result[0].SpokenText);
        Assert.False(result[0].IsNarration);
        Assert.Equal("Alice", result[0].CharacterName);
    }

    [Fact]
    public void GenderParsing_LowercaseGender()
    {
        var result = CharacterParser.Parse("[Alice:f]Hello[Bob:m]World");
        Assert.Equal(2, result.Count);
        Assert.Equal("F", result[0].Gender);
        Assert.Equal("M", result[1].Gender);
    }

    [Fact]
    public void GenderParsing_FullWordGender()
    {
        var result = CharacterParser.Parse("[Alice:Female]Hello[Bob:male]World");
        Assert.Equal(2, result.Count);
        Assert.Equal("F", result[0].Gender);
        Assert.Equal("M", result[1].Gender);
    }

    [Fact]
    public void GenderParsing_DashSeparator()
    {
        var result = CharacterParser.Parse("[Alice - F]Hello");
        Assert.Single(result);
        Assert.Equal("Alice", result[0].CharacterName);
        Assert.Equal("F", result[0].Gender);
    }

    [Fact]
    public void GenderParsing_SpaceSeparator()
    {
        var result = CharacterParser.Parse("[Alice F]Hello");
        Assert.Single(result);
        Assert.Equal("Alice", result[0].CharacterName);
        Assert.Equal("F", result[0].Gender);
    }

    [Fact]
    public void GenderParsing_NoGender_DefaultsFemale()
    {
        var result = CharacterParser.Parse("[Alice]Hello");
        Assert.Single(result);
        Assert.Equal("F", result[0].Gender);
    }

    [Fact]
    public void StripStrayMarkers_RemovesBracketContent()
    {
        var result = CharacterParser.StripStrayMarkers("Hello [Marina:F] world [Bob] end");
        Assert.Equal("Hello  world  end", result);
    }

    [Fact]
    public void StripStrayMarkers_RemovesAllBracketTypes()
    {
        var result = CharacterParser.StripStrayMarkers("Text [anything] more [Name:G] end");
        Assert.Equal("Text  more  end", result);
    }

    [Fact]
    public void ParseNameAndGender_FlexibleFormats()
    {
        Assert.Equal(("Alice", "F"), CharacterParser.ParseNameAndGender("Alice"));
        Assert.Equal(("Alice", "F"), CharacterParser.ParseNameAndGender("Alice:F"));
        Assert.Equal(("Alice", "F"), CharacterParser.ParseNameAndGender("Alice:f"));
        Assert.Equal(("Alice", "F"), CharacterParser.ParseNameAndGender("Alice:Female"));
        Assert.Equal(("Alice", "F"), CharacterParser.ParseNameAndGender("Alice:female"));
        Assert.Equal(("Bob", "M"), CharacterParser.ParseNameAndGender("Bob:M"));
        Assert.Equal(("Bob", "M"), CharacterParser.ParseNameAndGender("Bob:m"));
        Assert.Equal(("Alice", "F"), CharacterParser.ParseNameAndGender("Alice - F"));
        Assert.Equal(("Alice", "F"), CharacterParser.ParseNameAndGender("Alice F"));
    }

    [Fact]
    public void UnknownModifier_TreatedAsSpokenText()
    {
        var result = CharacterParser.Parse("[Alice](entire paragraph of narration text)Hello there");
        Assert.Single(result);
        Assert.Null(result[0].Modifier);
        Assert.Equal("(entire paragraph of narration text)Hello there", result[0].SpokenText);
    }

    [Fact]
    public void LongModifier_TreatedAsSpokenText()
    {
        var longMod = new string('a', 31);
        var result = CharacterParser.Parse($"[Alice]({longMod})Hello there");
        Assert.Single(result);
        Assert.Null(result[0].Modifier);
        Assert.Contains("Hello there", result[0].SpokenText);
    }

    [Fact]
    public void ValidModifier_ExtractedCorrectly()
    {
        var result = CharacterParser.Parse("[Alice](whisper)Hello there");
        Assert.Single(result);
        Assert.Equal("whisper", result[0].Modifier);
        Assert.Equal("Hello there", result[0].SpokenText);
    }
}
