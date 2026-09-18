using System.IO;
using System.Text;

namespace Benow.Conversation.Voice;

/// <summary>
/// Wraps raw PCM bytes in a WAV container so it can be sent to Whisper.
/// Format: 44-byte RIFF/WAVE header + raw PCM payload.
/// Ported from NASTV nastv-player-core/Voice/WavWrapper.cs (2026-09-17, phase 1) —
/// the AI plugin has a byte-identical WrapAsWav; NASTV adoption (phase 4) collapses the two.
/// </summary>
public static class WavWrapper
{
    /// <summary>
    /// Returns a stream containing a valid WAV file wrapping the given PCM bytes.
    /// The caller owns the stream and must dispose it.
    /// </summary>
    public static Stream Wrap(byte[] pcm, int sampleRate, int channels, int bitsPerSample)
    {
        var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            var byteRate = sampleRate * channels * bitsPerSample / 8;
            var blockAlign = (short)(channels * bitsPerSample / 8);

            // RIFF header
            w.Write(Encoding.ASCII.GetBytes("RIFF"));
            w.Write(36 + pcm.Length);                 // ChunkSize
            w.Write(Encoding.ASCII.GetBytes("WAVE"));

            // fmt chunk
            w.Write(Encoding.ASCII.GetBytes("fmt "));
            w.Write(16);                              // Subchunk1Size
            w.Write((short)1);                        // AudioFormat = PCM
            w.Write((short)channels);
            w.Write(sampleRate);
            w.Write(byteRate);
            w.Write(blockAlign);
            w.Write((short)bitsPerSample);

            // data chunk
            w.Write(Encoding.ASCII.GetBytes("data"));
            w.Write(pcm.Length);
            w.Write(pcm);
        }
        ms.Position = 0;
        return ms;
    }
}
