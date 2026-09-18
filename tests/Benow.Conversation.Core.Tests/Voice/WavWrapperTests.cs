using System.IO;
using System.Text;
using Benow.Conversation.Voice;
using Xunit;

namespace Benow.Conversation.Core.Tests.Voice;

public class WavWrapperTests
{
    [Fact]
    public void Wrap_ProducesValidWavHeader()
    {
        var pcm = new byte[3200];  // 100ms of 16kHz mono 16-bit audio
        var wav = WavWrapper.Wrap(pcm, 16000, 1, 16);

        using var br = new BinaryReader(wav, Encoding.UTF8);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(br.ReadBytes(4)));
        Assert.Equal(36 + 3200, br.ReadInt32());
        Assert.Equal("WAVE", Encoding.ASCII.GetString(br.ReadBytes(4)));
        Assert.Equal("fmt ", Encoding.ASCII.GetString(br.ReadBytes(4)));
        Assert.Equal(16, br.ReadInt32());              // PCM fmt chunk size
        Assert.Equal((short)1, br.ReadInt16());        // PCM format
        Assert.Equal((short)1, br.ReadInt16());        // mono
        Assert.Equal(16000, br.ReadInt32());           // sample rate
        Assert.Equal(32000, br.ReadInt32());           // byte rate (16000 × 1 × 2)
        Assert.Equal((short)2, br.ReadInt16());        // block align
        Assert.Equal((short)16, br.ReadInt16());       // bits per sample
        Assert.Equal("data", Encoding.ASCII.GetString(br.ReadBytes(4)));
        Assert.Equal(3200, br.ReadInt32());            // data size
    }

    [Fact]
    public void Wrap_EmptyPcm_StillProducesValidHeader()
    {
        var wav = WavWrapper.Wrap(Array.Empty<byte>(), 16000, 1, 16);
        Assert.Equal(44, wav.Length);  // header only, no data
    }
}
