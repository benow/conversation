using System.Reflection;
using benow_conversation.Configuration;
using benow_conversation.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace benow_conversation.Tests;

public class PersonaAllocatorTests
{
    private static IOptions<AppSettings> CreateOptions(
        Dictionary<string, VoicePersona> personas,
        Dictionary<string, PersonaUsageEntry>? usage = null,
        string ttsModel = "test-model")
    {
        var settings = new AppSettings
        {
            Personas = personas,
            PersonaUsage = usage ?? new(),
            OpenRouter = new OpenRouterSettings { TtsModel = ttsModel }
        };
        return Microsoft.Extensions.Options.Options.Create(settings);
    }

    private static Mock<ILogger<PersonaAllocator>> CreateLoggerMock()
    {
        return new Mock<ILogger<PersonaAllocator>>();
    }

    private static PersonaAllocator CreateAllocator(
        Dictionary<string, VoicePersona> personas,
        Dictionary<string, PersonaUsageEntry>? usage = null,
        string ttsModel = "test-model")
    {
        return new PersonaAllocator(CreateOptions(personas, usage, ttsModel), CreateLoggerMock().Object);
    }

    [Fact]
    public void RandomSelection_DifferentResultsPossible()
    {
        var personas = new Dictionary<string, VoicePersona>
        {
            ["p1"] = new VoicePersona { Model = "test-model", Gender = "F", Voice = "voice1" },
            ["p2"] = new VoicePersona { Model = "test-model", Gender = "F", Voice = "voice2" },
            ["p3"] = new VoicePersona { Model = "test-model", Gender = "F", Voice = "voice3" },
        };

        var results = new HashSet<string?>();
        for (int i = 0; i < 20; i++)
        {
            var allocator = CreateAllocator(personas);
            var key = allocator.AllocateForCharacter("Alice", "F");
            results.Add(key);
        }

        Assert.True(results.Count > 1, $"Expected multiple different personas but got only: {string.Join(", ", results)}");
    }

    [Fact]
    public void SameCharacter_SamePersona()
    {
        var personas = new Dictionary<string, VoicePersona>
        {
            ["p1"] = new VoicePersona { Model = "test-model", Gender = "F", Voice = "voice1" },
            ["p2"] = new VoicePersona { Model = "test-model", Gender = "F", Voice = "voice2" },
        };

        var allocator = CreateAllocator(personas);
        var first = allocator.AllocateForCharacter("Alice", "F");
        var second = allocator.AllocateForCharacter("Alice", "F");

        Assert.Equal(first, second);
    }

    [Fact]
    public void StalePersona_ReturnsToPool()
    {
        var personas = new Dictionary<string, VoicePersona>
        {
            ["p1"] = new VoicePersona { Model = "test-model", Gender = "F", Voice = "voice1" },
            ["p2"] = new VoicePersona { Model = "test-model", Gender = "F", Voice = "voice2" },
        };

        var allocator = CreateAllocator(personas);

        var key1 = allocator.AllocateForCharacter("Alice", "F");
        Assert.NotNull(key1);

        var usageField = typeof(PersonaAllocator).GetField("_usage", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var usageDict = (Dictionary<string, PersonaUsageEntry>)usageField.GetValue(allocator)!;
        usageDict[key1] = new PersonaUsageEntry { LastCharacter = "Alice", LastUsedUtc = DateTime.UtcNow - TimeSpan.FromHours(49) };

        allocator.AllocateForCharacter("Bob", "F");

        var persona = allocator.GetPersona(key1);
        Assert.NotNull(persona);
    }

    [Fact]
    public void Reset_ClearsAllMappings()
    {
        var personas = new Dictionary<string, VoicePersona>
        {
            ["p1"] = new VoicePersona { Model = "test-model", Gender = "F", Voice = "voice1" },
        };

        var allocator = CreateAllocator(personas);
        allocator.AllocateForCharacter("Alice", "F");
        allocator.Reset();

        var key1 = allocator.AllocateForCharacter("Alice", "F");
        Assert.NotNull(key1);
    }

    [Fact]
    public void MoreCharactersThanPersonas_ReuseWithWarning()
    {
        var personas = new Dictionary<string, VoicePersona>
        {
            ["p1"] = new VoicePersona { Model = "test-model", Gender = "F", Voice = "voice1" },
        };

        var allocator = CreateAllocator(personas);
        var key1 = allocator.AllocateForCharacter("Alice", "F");
        var key2 = allocator.AllocateForCharacter("Bob", "F");

        Assert.NotNull(key1);
        Assert.NotNull(key2);
    }

    [Fact]
    public void EmptyPool_WrongModel_ReturnsNull()
    {
        var personas = new Dictionary<string, VoicePersona>
        {
            ["p1"] = new VoicePersona { Model = "other-model", Gender = "F", Voice = "voice1" },
        };

        var allocator = CreateAllocator(personas, ttsModel: "test-model");
        var key = allocator.AllocateForCharacter("Alice", "F");

        Assert.Null(key);
    }

    [Fact]
    public void DefaultPersona_IncludedInPool()
    {
        var personas = new Dictionary<string, VoicePersona>
        {
            ["p1"] = new VoicePersona { Model = "test-model", Gender = "F", Voice = "voice1", IsDefault = true },
        };

        var allocator = CreateAllocator(personas, ttsModel: "test-model");
        var key = allocator.AllocateForCharacter("Alice", "F");

        Assert.Equal("p1", key);
    }

    [Fact]
    public void GetPersona_ReturnsCorrectPersona()
    {
        var personas = new Dictionary<string, VoicePersona>
        {
            ["p1"] = new VoicePersona { Model = "test-model", Gender = "F", Voice = "voice1" },
            ["p2"] = new VoicePersona { Model = "test-model", Gender = "M", Voice = "voice2" },
        };

        var allocator = CreateAllocator(personas);
        var persona = allocator.GetPersona("p2");

        Assert.NotNull(persona);
        Assert.Equal("voice2", persona.Voice);
        Assert.Equal("M", persona.Gender);
    }
}
