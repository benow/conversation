using benow_conversation.Services;

namespace benow_conversation.Tests;

public class VoiceDescriptionTests
{
    [Theory]
    [InlineData("af_bella", "American female (af_bella)")]
    [InlineData("am_adam", "American male (am_adam)")]
    [InlineData("bf_alice", "British female (bf_alice)")]
    [InlineData("bm_daniel", "British male (bm_daniel)")]
    [InlineData("ef_dora", "English female (ef_dora)")]
    [InlineData("em_alex", "English male (em_alex)")]
    [InlineData("ff_siwis", "French female (ff_siwis)")]
    [InlineData("jf_alpha", "Japanese female (jf_alpha)")]
    [InlineData("zm_yunjian", "Chinese male (zm_yunjian)")]
    [InlineData("hf_alpha", "Hindi female (hf_alpha)")]
    [InlineData("hm_omega", "Hindi male (hm_omega)")]
    [InlineData("if_sara", "Italian female (if_sara)")]
    [InlineData("pf_dora", "Portuguese female (pf_dora)")]
    public void InfersGenderAndLanguage(string voiceId, string expected)
    {
        Assert.Equal(expected, ModelService.InferVoiceDescription(voiceId));
    }

    [Fact]
    public void InfersEmotionFromVoxtralName()
    {
        Assert.Equal("English Paul, happy", ModelService.InferVoiceDescription("en_paul_happy"));
        Assert.Equal("British Jane, sarcasm", ModelService.InferVoiceDescription("gb_jane_sarcasm"));
        Assert.Equal("French Marie, angry", ModelService.InferVoiceDescription("fr_marie_angry"));
    }

    [Fact]
    public void ReturnsEmptyForUnknownPattern()
    {
        Assert.Equal("", ModelService.InferVoiceDescription("alloy"));
        Assert.Equal("", ModelService.InferVoiceDescription("tara"));
    }

    [Fact]
    public void InfersAmericanBritishPatterns()
    {
        Assert.Equal("American female", ModelService.InferVoiceDescription("american_female"));
        Assert.Equal("British male", ModelService.InferVoiceDescription("british_male"));
    }

    [Fact]
    public void InfersConversationalPatterns()
    {
        Assert.Equal("conversational a", ModelService.InferVoiceDescription("conversational_a"));
        Assert.Equal("read speech a", ModelService.InferVoiceDescription("read_speech_a"));
    }
}
