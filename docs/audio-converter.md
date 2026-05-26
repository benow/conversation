# Audio Converter

**Source:** `src/benow-conversation/Services/AudioConverter.cs`

## Overview

Converts raw PCM audio data to MP3 (or other formats) by shelling out to **ffmpeg**. Used as a fallback when a TTS model doesn't support the requested output format directly.

## Interface

```csharp
public interface IAudioConverter
{
    bool IsFfmpegAvailable();
    Task<byte[]> ConvertPcmToMp3Async(byte[] pcmData, int sampleRate = 24000, int channels = 1);
}
```

## How It Works

1. **PCM to WAV wrapping** -- Raw PCM bytes are wrapped in a WAV file header using `WriteWavFile()`, which writes the standard RIFF/WAVE headers (16-bit PCM, configurable sample rate and channel count).
2. **ffmpeg transcoding** -- The WAV file is written to a temp file, then ffmpeg is invoked with `-y -i <temp.wav> -b:a 128k <temp.mp3>`.
3. **Cleanup** -- Both temp files are deleted in a `finally` block.

## WAV Header Construction

`WriteWavFile()` constructs a valid WAV file in memory:

| Field | Value |
|---|---|
| Format | PCM (codec 1) |
| Sample rate | 24000 Hz (default) |
| Channels | 1 (mono, default) |
| Bits per sample | 16 |

## Availability Check

`IsFfmpegAvailable()` runs `ffmpeg -version` with a 5-second timeout and caches the result statically.
