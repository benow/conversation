# External Tools

benow-conversation shells out to several system utilities for audio I/O, clipboard, and keyboard simulation. This document covers installation, verification, and usage details for each.

## Summary

| Tool | Package | Used By | Purpose |
|---|---|---|---|
| `ffmpeg` | `ffmpeg` | `AudioConverter`, `PipeWireRecorder` | Format conversion, audio recording (`-f pulse`), silence detection, beep generation |
| `ffplay` | `ffmpeg` | `AudioPlayer`, `SttRunner` | Audio playback (file and streaming), beep tone playback |
| `aplay` | `alsa-utils` | `AudioPlayer` | Enumerate ALSA audio output devices |
| `wl-copy` | `wl-clipboard` | `WaylandClipboardService` | Wayland clipboard write (STT pipeline) |
| `ydotool` | `ydotool` | `YdotoolKeyboardSimulator` | Wayland keyboard simulation (paste, enter) |
| `pw-cli` | `pipewire-bin` | `SttSetup` | PipeWire device listing (setup mode only) |
| `bluetoothctl` | `bluez` | `SttSetup` | Bluetooth device discovery (setup mode only) |

## Installation

```sh
# Audio tools (likely already installed)
sudo apt install ffmpeg alsa-utils

# PipeWire (likely already installed)
sudo apt install pipewire-bin

# Wayland clipboard
sudo apt install wl-clipboard

# Wayland keyboard simulation
sudo apt install ydotool

# User must be in input group for evdev keyboard trigger
sudo usermod -aG input $USER
# (requires re-login)
```

## ffmpeg / ffplay

**Package:** `ffmpeg` (both `ffmpeg` and `ffplay` are included)

**Used by:**
- `AudioConverter` — PCM-to-MP3 transcoding
- `AudioPlayer` — audio playback (file and streaming)
- `PipeWireRecorder` — microphone audio recording
- `SttRunner` — beep tone generation and playback

### TTS usage (AudioConverter + AudioPlayer)

- `AudioConverter.ConvertPcmToMp3Async()`:
  ```
  ffmpeg -y -i <temp.wav> -b:a 128k <temp.mp3>
  ```
- `AudioPlayer.PlayAsync()`:
  ```
  ffplay -nodisp -autoexit -loglevel quiet [-volume N] [-audiodevice "device"] -i "path"
  ```
- `AudioPlayer.PlayStreamAsync()`:
  ```
  ffplay -nodisp -autoexit -loglevel quiet -f <format> [-volume N] [-audiodevice "device"] -i pipe:0
  ```

### STT recording usage (PipeWireRecorder)

Records microphone audio via PulseAudio compat layer:

```
ffmpeg -y -f pulse -i default -ac 1 -ar 16000 -acodec libmp3lame -b:a 32k "/tmp/stt_<guid>.mp3"
```

The `-f pulse` flag uses PipeWire's PulseAudio compat layer. System ffmpeg (8.0.1) was compiled without native PipeWire support, so `-f pipewire` is not available.

**Process termination:**
1. SIGTERM via `kill -TERM <pid>` when recording is stopped
2. Wait up to 5 seconds for ffmpeg to finalize (flush + close MP3 container)
3. Force kill if it doesn't exit in time
4. Orphan cleanup at startup kills leftover ffmpeg processes

**Silence detection (optional, disabled by default):**
```
ffmpeg -y -f pulse -i default -ac 1 -ar 16000 -af "silencedetect=n=-30dB:d=4" -acodec libmp3lame -b:a 32k "/tmp/stt_<guid>.mp3"
```

When enabled (`SilenceTimeoutSeconds > 0`), monitors stderr for `silence_start` lines and sends SIGTERM to stop recording automatically.

### Beep tone generation and playback (SttRunner)

```
# Generate beep (cached to /tmp/stt_beep_<freq>.mp3)
ffmpeg -y -f lavfi -i "sine=frequency=880:duration=0.15" -ac 1 -ar 16000 -acodec libmp3lame -b:a 32k "/tmp/stt_beep_880.mp3"

# Play beep
ffplay -nodisp -autoexit -volume 80 "/tmp/stt_beep_880.mp3"
```

- Start recording: 880Hz, 0.15s (fire-and-forget)
- Stop recording: 440Hz, 0.2s (awaited)

**Verification:**
```sh
ffmpeg -version | head -1
ffplay -version | head -1
```

## aplay

**Package:** `alsa-utils`

**Used by:** `AudioPlayer.ListDevices()`

Runs `aplay -L` to enumerate named ALSA PCM devices. Works through PipeWire's ALSA plugin (`pipewire-alsa`).

**Verification:**
```sh
aplay -L
```

## wl-copy / wl-paste

**Package:** `wl-clipboard`

**Used by:** `WaylandClipboardService` (STT pipeline)

**How the app uses it:**

The app writes text to `wl-copy` stdin (avoids shell injection):

```csharp
var psi = new ProcessStartInfo
{
    FileName = "wl-copy",
    RedirectStandardInput = true,
    UseShellExecute = false,
};
using var process = Process.Start(psi);
await process.StandardInput.WriteAsync(text);
process.StandardInput.Close();
await process.WaitForExitAsync(ct);
```

**No `--foreground` flag:** Without `--foreground`, `wl-copy` forks to background and serves clipboard data asynchronously. This is intentional — `--foreground` blocks until another app takes clipboard ownership, which would prevent paste+Enter from ever executing. Instead, a 100ms delay between copy and paste ensures the clipboard is populated.

**Verification:**
```sh
echo "test" | wl-copy
wl-paste
```

**Note:** Only works in Wayland sessions. Check with `echo $XDG_SESSION_TYPE` — should be `wayland`.

## ydotool

**Package:** `ydotool`

**Used by:** `YdotoolKeyboardSimulator` (STT pipeline)

**How the app uses it:**

```sh
# Simulate Ctrl+V (paste)
ydotool key 29:1 47:1 47:0 29:0

# Simulate Enter
ydotool key 28:1 28:0
```

The `YDOTOOL_SOCKET` environment variable is set explicitly on each `ProcessStartInfo.Environment` — the app does NOT rely on the shell environment.

### Setup

The daemon (`ydotoold`) must run as root to access `/dev/uinput`:

```sh
sudo ydotoold &
```

**Socket path:** The daemon creates its socket at `/tmp/.ydotool_socket`, but the `ydotool` client searches for it at `/run/user/<uid>/.ydotool_socket` by default. The app uses `/run/user/1000/.ydotool_socket` (configurable via `Stt.YdotoolSocketPath`).

This is configured automatically in `appsettings.json`:
```json
{
  "Stt": {
    "YdotoolSocketPath": "/run/user/1000/.ydotool_socket"
  }
}
```

### Auto-start ydotoold at boot

Create `/etc/systemd/system/ydotoold.service`:

```ini
[Unit]
Description=ydotool daemon
After=local-fs.target

[Service]
ExecStart=/usr/bin/ydotoold
Restart=on-failure

[Install]
WantedBy=multi-user.target
```

Then:
```sh
sudo systemctl enable --now ydotoold
```

### Linux input event key codes

From `<linux/input-event-codes.h>`. The app also uses these for the evdev keyboard trigger:

| Key | Code | Key | Code |
|---|---|---|---|
| Enter | 28 | Left Ctrl | 29 |
| Right Ctrl | 97 | V | 47 |
| Space | 57 | Left Shift | 42 |
| Right Shift | 54 | Left Alt | 56 |
| Right Alt | 100 | Super/Meta | 125 |

Syntax: `ydotool key <code>:<value>` where `1` = press, `0` = release.

**Verification:**
```sh
YDOTOOL_SOCKET=/run/user/1000/.ydotool_socket ydotool key 28:1 28:0
```

This should trigger an Enter keypress in whatever window has focus.

**Troubleshooting:**
- `failed to open uinput device: Permission denied` — ydotoold must run as root (`sudo ydotoold`)
- `failed to connect socket ... No such file or directory` — check socket path matches `Stt.YdotoolSocketPath`
- Keys have no effect — check that the target window has focus and that ydotoold is running (`pgrep ydotoold`)

## pw-cli

**Package:** `pipewire-bin`

**Used by:** `SttSetup` (interactive setup mode only), `PipeWireRecorder.EnsureAvrcpDevice()`

Lists PipeWire objects for device discovery:
```sh
pw-cli list-objects
```

Used during `--stt-setup` to find Bluetooth audio device MACs and during Bluetooth reconnection attempts.

## bluetoothctl

**Package:** `bluez`

**Used by:** `SttSetup` (interactive setup mode only), `PipeWireRecorder.ReconnectBluetooth()`

For Bluetooth device discovery and reconnection:
```sh
bluetoothctl disconnect 28:6F:40:44:54:52
bluetoothctl connect 28:6F:40:44:54:52
```

Used when the AVRCP trigger device is not found (Bluetooth stuck in HFP mode) to force a reconnect that restores A2DP/AVRCP profiles.
