using System.Text.Json;
using benow_conversation.Configuration;

namespace benow_conversation.Tests;

public class MultiCharacterConfigTests
{
    private static readonly string AppSettingsPath = GetAppSettingsPath();

    private static string GetAppSettingsPath()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir != null && !Directory.Exists(Path.Combine(dir, "src", "benow-conversation")))
        {
            dir = Directory.GetParent(dir)?.FullName;
        }

        if (dir is null)
            throw new InvalidOperationException("Could not find solution root from " + Directory.GetCurrentDirectory());

        return Path.Combine(dir, "src", "benow-conversation", "appsettings.json");
    }

    private static AppSettings LoadSettings()
    {
        var json = File.ReadAllText(AppSettingsPath);
        return JsonSerializer.Deserialize<AppSettings>(json)
               ?? throw new InvalidOperationException("Failed to deserialize appsettings.json");
    }

    [Fact]
    public void AppSettings_DeserializesWithoutError()
    {
        var settings = LoadSettings();
        Assert.NotNull(settings);
    }

    [Fact]
    public void Personas_All13FemalePersonasPresent()
    {
        var settings = LoadSettings();

        for (var i = 1; i <= 13; i++)
        {
            var key = $"female-{i}";
            Assert.True(settings.Personas.ContainsKey(key), $"Persona '{key}' should exist");
        }
    }

    [Fact]
    public void Personas_AllHaveGenderF_ExceptSelf()
    {
        var settings = LoadSettings();

        foreach (var kvp in settings.Personas)
        {
            if (kvp.Value.IsSelf) continue;
            Assert.Equal("F", kvp.Value.Gender);
        }
    }

    [Fact]
    public void Personas_AllHaveThoughtInstructions()
    {
        var settings = LoadSettings();

        foreach (var kvp in settings.Personas)
        {
            Assert.NotNull(kvp.Value.ThoughtInstructions);
            Assert.NotEmpty(kvp.Value.ThoughtInstructions);
        }
    }

    [Fact]
    public void MultiCharacter_DeserializesWithCorrectDefaults()
    {
        var settings = LoadSettings();

        Assert.NotNull(settings.MultiCharacter);
        Assert.Equal("deepseek/deepseek-v4-flash", settings.MultiCharacter.ModifierModel);
        Assert.Equal(8000, settings.MultiCharacter.ModifierTimeoutMs);
        Assert.True(settings.MultiCharacter.AutoInjectModifiers);
        Assert.NotEmpty(settings.MultiCharacter.ModifierSystemPrompt);
        Assert.Equal("female-3", settings.MultiCharacter.ThoughtPersona);
        Assert.Equal("female-13", settings.MultiCharacter.NarratorPersona);
    }

    [Fact]
    public void MultiCharacter_ModifierModelIsDeepSeek()
    {
        var settings = LoadSettings();
        Assert.Equal("deepseek/deepseek-v4-flash", settings.MultiCharacter.ModifierModel);
    }

    [Fact]
    public void MultiCharacter_AutoInjectModifiersIsTrue()
    {
        var settings = LoadSettings();
        Assert.True(settings.MultiCharacter.AutoInjectModifiers);
    }

    [Fact]
    public void MultiCharacter_ModifierTimeoutMsIs8000()
    {
        var settings = LoadSettings();
        Assert.Equal(8000, settings.MultiCharacter.ModifierTimeoutMs);
    }

    [Fact]
    public void Personas_Female1IsDefault()
    {
        var settings = LoadSettings();
        Assert.True(settings.Personas["female-1"].IsDefault);
    }

    [Fact]
    public void PersonaUsage_IsEmpty()
    {
        var settings = LoadSettings();
        Assert.Empty(settings.PersonaUsage);
    }
}
