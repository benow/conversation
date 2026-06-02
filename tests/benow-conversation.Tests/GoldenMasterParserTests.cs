using System.Text.Json;
using System.Text.Json.Serialization;
using benow_conversation.Models;
using benow_conversation.Services;

namespace benow_conversation.Tests;

public class GoldenMasterParserTests
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    public static IEnumerable<object[]> GetFixtures()
    {
        var fixturesDir = Path.Combine(AppContext.BaseDirectory, "fixtures");
        if (!Directory.Exists(fixturesDir))
            throw new DirectoryNotFoundException($"Fixtures directory not found: {fixturesDir}");

        foreach (var file in Directory.GetFiles(fixturesDir, "*.json"))
        {
            var fixture = JsonSerializer.Deserialize<ParsingFixture>(File.ReadAllText(file), JsonOpts);
            if (fixture != null)
                yield return new object[] { fixture };
        }
    }

    [Theory]
    [MemberData(nameof(GetFixtures))]
    public void Parse_MatchesGoldenMaster(ParsingFixture fixture)
    {
        Assert.NotNull(fixture.Input);
        Assert.NotNull(fixture.ExpectedSegments);

        var actual = CharacterParser.Parse(fixture.Input);

        if (fixture.ExpectedCoverage?.MinPercent is > 0)
        {
            var spokenTotal = actual.Sum(s => s.SpokenText.Length);
            var coverage = fixture.Input.Length > 0 ? (double)spokenTotal / fixture.Input.Length * 100 : 0;
            Assert.True(coverage >= fixture.ExpectedCoverage.MinPercent,
                $"{fixture.Name}: text coverage {coverage:F1}% < expected {fixture.ExpectedCoverage.MinPercent}%");
        }

        Assert.Equal(fixture.ExpectedSegments.Count, actual.Count);

        for (int i = 0; i < fixture.ExpectedSegments.Count; i++)
        {
            var expected = fixture.ExpectedSegments[i];
            var actualSeg = actual[i];

            Assert.Equal(expected.CharacterName, actualSeg.CharacterName);

            if (expected.Gender != null)
                Assert.Equal(expected.Gender, actualSeg.Gender);

            Assert.Equal(expected.IsNarration, actualSeg.IsNarration);
            Assert.Equal(expected.IsThought, actualSeg.IsThought);
            Assert.Equal(expected.Modifier, actualSeg.Modifier);

            if (!string.IsNullOrEmpty(expected.SpokenTextContains))
                Assert.Contains(expected.SpokenTextContains, actualSeg.SpokenText);
        }
    }
}

public class ParsingFixture
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Source { get; set; } = "";
    public string Input { get; set; } = "";
    public CoverageExpectation? ExpectedCoverage { get; set; }
    public List<ExpectedSegment> ExpectedSegments { get; set; } = new();
}

public class CoverageExpectation
{
    public int MinPercent { get; set; }
}

public class ExpectedSegment
{
    public string CharacterName { get; set; } = "";
    public string? Gender { get; set; }
    public bool IsNarration { get; set; }
    public bool IsThought { get; set; }
    public string? Modifier { get; set; }

    /// <summary>Substring that must appear in the spoken text. Use this instead of exact matching for longer texts.</summary>
    public string? SpokenTextContains { get; set; }
}
