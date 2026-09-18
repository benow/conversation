using System.Text;

namespace Benow.Conversation.Llm;

/// <summary>Decoded WAV: normalized 16-bit mono PCM + the source format facts.</summary>
public sealed record DecodedWav(byte[] Pcm, int SampleRate, int Channels, int BitsPerSample);

/// <summary>
/// Full RIFF/WAVE walker producing normalized 16-bit mono PCM: handles PCM + IEEE float,
/// 8/16/24/32-bit depths (others take the top 16 bits), and downmixes any channel count by
/// averaging. Port of NASTV VoiceEndpoints.ParseWav (2026-09-17, phase 1).
/// </summary>
public static class WavDecoder
{
    public static DecodedWav? ParseWav(byte[] wav)
    {
        if (wav.Length < 44) return null;
        if (Encoding.ASCII.GetString(wav, 0, 4) != "RIFF" ||
            Encoding.ASCII.GetString(wav, 8, 4) != "WAVE") return null;

        var format = 1;      // 1 = PCM, 3 = IEEE float
        var channels = 1;
        var sampleRate = 24000;
        var bits = 16;
        var dataOffset = -1;
        var dataLen = 0;

        var pos = 12; // past RIFF + size + WAVE
        while (pos + 8 <= wav.Length)
        {
            var id = Encoding.ASCII.GetString(wav, pos, 4);
            var size = BitConverter.ToInt32(wav, pos + 4);
            if (id == "fmt " && size >= 16)
            {
                format = BitConverter.ToUInt16(wav, pos + 8);
                channels = BitConverter.ToUInt16(wav, pos + 10);
                sampleRate = BitConverter.ToInt32(wav, pos + 12);
                bits = BitConverter.ToUInt16(wav, pos + 22);
            }
            else if (id == "data")
            {
                dataOffset = pos + 8;
                dataLen = size;
                break;
            }
            pos += 8 + size + (size % 2); // chunks are word-aligned
        }

        if (dataOffset < 0 || dataLen <= 0 || sampleRate <= 0 || channels <= 0 || bits <= 0)
            return null;

        var src = new byte[dataLen];
        Array.Copy(wav, dataOffset, src, 0, Math.Min(dataLen, wav.Length - dataOffset));
        var bytesPerSample = Math.Max(bits, 1) / 8;
        var frameCount = src.Length / (bytesPerSample * channels);
        var sampleCount = frameCount; // one output sample per source frame (channels averaged)

        var pcm = new byte[sampleCount * 2];
        for (var i = 0; i < frameCount; i++)
        {
            // Average all channels into one sample.
            double sum = 0;
            for (var ch = 0; ch < channels; ch++)
            {
                var byteIndex = (i * channels + ch) * bytesPerSample;
                long s;
                if (format == 3) // IEEE float (32-bit typically, but handle 64 too)
                {
                    if (bits == 64)
                        s = (long)Math.Clamp(BitConverter.ToDouble(src, byteIndex) * 32768.0, -32768.0, 32767.0);
                    else
                        s = (long)Math.Clamp(BitConverter.ToSingle(src, byteIndex) * 32768.0, -32768.0, 32767.0);
                }
                else if (bits == 8)
                {
                    s = (src[byteIndex] - 128) * 256; // unsigned 8-bit → signed 16
                }
                else if (bits == 16)
                {
                    s = BitConverter.ToInt16(src, byteIndex);
                }
                else if (bits == 24)
                {
                    s = src[byteIndex] | (src[byteIndex + 1] << 8) | (src[byteIndex + 2] << 16);
                    if ((s & 0x800000) != 0) s |= ~0xFFFFFF; // sign-extend 24-bit
                }
                else
                {
                    // Unsupported bit depth (e.g. 32-bit int) — take the top 16 bits.
                    s = 0;
                    for (var b = bytesPerSample - 1; b >= 0; b--)
                        s = (s << 8) | src[byteIndex + b];
                    s >>= bits - 16;
                }
                sum += s;
            }
            var avg = (short)Math.Clamp(sum / channels, short.MinValue, short.MaxValue);
            pcm[i * 2] = (byte)(avg & 0xFF);
            pcm[i * 2 + 1] = (byte)((avg >> 8) & 0xFF);
        }

        return new DecodedWav(pcm, sampleRate, channels, bits);
    }

    /// <summary>Reference voice as a data: URI for provider payloads (Replicate xtts input).</summary>
    public static string BuildDataUri(string filePath)
    {
        var bytes = File.ReadAllBytes(filePath);
        var b64 = Convert.ToBase64String(bytes);
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        var mime = ext switch
        {
            ".wav" => "audio/wav",
            ".mp3" => "audio/mpeg",
            _ => "audio/wav"
        };
        return $"data:{mime};base64,{b64}";
    }
}
