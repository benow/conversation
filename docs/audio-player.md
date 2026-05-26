# Audio Player

**Source:** `src/benow-conversation/Services/AudioPlayer.cs`

## Overview

Handles audio playback and device enumeration using system audio tools. Uses **ffplay** (from ffmpeg) for playback and **aplay** (ALSA) for device listing on Linux.

## Interface

```csharp
public interface IAudioPlayer
{
    Task PlayAsync(string filePath, ...);
    Task PlayStreamAsync(Stream audioStream, string format, ...);
    bool IsAvailable { get; }
    IReadOnlyList<AudioDevice> ListDevices();
}
```

## Playback Methods

### PlayAsync

Plays an audio file from disk via ffplay. Constructs arguments with:
- `-nodisp -autoexit -loglevel quiet` (headless, auto-quit)
- `-volume N` (0--100)
- `-audiodevice "..."` (ALSA device name)
- `-i "filepath"`

### PlayStreamAsync

Streams raw audio data to ffplay via stdin (`pipe:0`). Detects the format and sets appropriate ffplay input flags:
- **PCM** → `-f s16le -ar 24000` (raw 16-bit little-endian, 24kHz)
- **WAV** → `-f wav`
- **MP3** → `-f mp3`

The input stream is copied to ffplay's stdin asynchronously. On cancellation, the process tree is killed.

## Device Enumeration

`ListDevices()` runs `aplay -L` and parses the output to extract ALSA device IDs and descriptions. Only top-level (non-indented) entries are included.

## Availability Check

`IsAvailable` checks for ffplay at common paths (`ffplay`, `/usr/bin/ffplay`, `/usr/local/bin/ffplay`) using `which`. The result is cached statically for the process lifetime.

## AudioDevice

```csharp
public class AudioDevice
{
    public string Id { get; init; }          // ALSA device name
    public string Description { get; init; } // Human-readable description
}
```
