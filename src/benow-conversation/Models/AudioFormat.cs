using System.Text.Json.Serialization;

namespace benow_conversation.Models;

/// <summary>Declares the audio format parameters for a TTS provider or pipeline target.</summary>
public readonly record struct AudioFormat(
    string Codec,
    int SampleRate,
    int Channels,
    int BitsPerSample)
{
    public static AudioFormat Pcm24000Mono => new("pcm", 24000, 1, 16);
    public static AudioFormat Wav24000Mono => new("wav", 24000, 1, 16);
    public static AudioFormat Mp3_24000Mono => new("mp3", 24000, 1, 16);

    public int BytesPerSecond => SampleRate * Channels * (BitsPerSample / 8);

    public bool IsCompatibleWith(AudioFormat target) =>
        Codec == target.Codec &&
        SampleRate == target.SampleRate &&
        Channels == target.Channels &&
        BitsPerSample == target.BitsPerSample;

    public bool IsSameCodec(AudioFormat target) => Codec == target.Codec;

    /// <summary>Whether the PCM parameters match the target (ignoring codec).</summary>
    public bool IsPcmCompatibleWith(AudioFormat target) =>
        SampleRate == target.SampleRate &&
        Channels == target.Channels &&
        BitsPerSample == target.BitsPerSample;
}
