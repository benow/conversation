# External Tools

benow-conversation shells out to several system utilities for audio I/O, clipboard, and keyboard simulation. This document covers installation, verification, and usage details for each.

## Summary

| Tool | Package | Used By | Purpose |
|---|---|---|---|
| `ffmpeg` | `ffmpeg` | `AudioConverter` | PCM-to-MP3 transcoding |
| `ffplay` | `ffmpeg` | `AudioPlayer` | Audio playback (file and streaming) |
| `aplay` | `alsa-utils` | `AudioPlayer` | Enumerate ALSA audio output devices |
| `pw-record` | `pipewire-bin` | `SttService` (planned) | Microphone audio capture |
| `wl-copy` | `wl-clipboard` | `ClipboardService` (planned) | Wayland clipboard write |
| `ydotool` | `ydotool` | `KeyboardService` (planned) | Wayland keyboard simulation (paste, enter) |

## Installation

```sh
# Audio tools (likely already installed)
sudo apt install ffmpeg alsa-utils

# PipeWire audio capture (likely already installed)
sudo apt install pipewire-bin

# Wayland clipboard
sudo apt install wl-clipboard

# Wayland keyboard simulation
sudo apt install ydotool
```

## ffmpeg / ffplay

**Package:** `ffmpeg` (both `ffmpeg` and `ffplay` are included)

**Used by:** `AudioConverter` (format conversion), `AudioPlayer` (playback)

**How the app uses it:**

- `AudioConverter.ConvertPcmToMp3Async()` — wraps raw PCM in a WAV header, then runs:
  ```
  ffmpeg -y -i <temp.wav> -b:a 128k <temp.mp3>
  ```
- `AudioPlayer.PlayAsync()` — plays a file:
  ```
  ffplay -nodisp -autoexit -loglevel quiet [-volume N] [-audiodevice "device"] -i "path"
  ```
- `AudioPlayer.PlayStreamAsync()` — pipes audio to ffplay stdin:
  ```
  ffplay -nodisp -autoexit -loglevel quiet -f <format> [-volume N] [-audiodevice "device"] -i pipe:0
  ```

**Verification:**
```sh
ffmpeg -version | head -1
ffplay -version | head -1
```

**Fallback behavior:** If ffmpeg/ffplay are not available, the app logs a warning. File saves still work but PCM-to-MP3 conversion is skipped (saved as raw PCM). Playback is disabled. Use `--output <file>` to save audio without playback.

## aplay

**Package:** `alsa-utils`

**Used by:** `AudioPlayer.ListDevices()`

**How the app uses it:** Runs `aplay -L` to enumerate named ALSA PCM devices. Parses non-indented lines as device IDs. Works through PipeWire's ALSA plugin (`pipewire-alsa`), so devices like `pipewire` and `default` appear.

**Verification:**
```sh
aplay -L
```

## pw-record

**Package:** `pipewire-bin`

**Used by:** `SttService` (planned, for `--stt` mode)

**How the app will use it:** Records microphone audio to a WAV file for STT transcription:
```
pw-record --format s16 --rate 16000 --channels 1 <output.wav>
```

The process runs until killed (user stops recording). The resulting WAV is sent directly to Groq Whisper — no transcoding needed.

**Verification:**
```sh
pw-record --version
```

**Quick test (record 3 seconds, then play back):**
```sh
pw-record --format s16 --rate 16000 --channels 1 /tmp/test-mic.wav &
PID=$!
sleep 3
kill $PID
ffplay -nodisp -autoexit /tmp/test-mic.wav
```

## wl-copy / wl-paste

**Package:** `wl-clipboard`

**Used by:** `ClipboardService` (planned, for `--stt` mode)

**How the app will use it:**
```sh
echo -n "cleaned text" | wl-copy
```

The `-n` flag prevents a trailing newline in the clipboard. `wl-copy` writes to the Wayland clipboard, replacing its contents. The text is then pasted into the target application via ydotool.

**Verification:**
```sh
echo "test" | wl-copy
wl-paste
```

**Note:** Only works in Wayland sessions. Check with `echo $XDG_SESSION_TYPE` — should be `wayland`.

## ydotool

**Package:** `ydotool`

**Used by:** `KeyboardService` (planned, for `--stt` mode)

**How the app will use it:**
```sh
# Simulate Ctrl+V (paste)
YDOTOOL_SOCKET=/tmp/.ydotool_socket ydotool key 29:1 47:1 47:0 29:0

# Simulate Enter
YDOTOOL_SOCKET=/tmp/.ydotool_socket ydotool key 28:1 28:0
```

**Setup (verified on this system):**

The daemon (`ydotoold`) must run as root to access `/dev/uinput`:

```sh
sudo ydotoold &
```

The daemon creates its socket at `/tmp/.ydotool_socket`, but the `ydotool` client searches for it at `/run/user/<uid>/.ydotool_socket` by default. Set the environment variable to point to the correct location:

```sh
export YDOTOOL_SOCKET=/tmp/.ydotool_socket
```

Add this to `~/.bashrc` or `~/.profile` for persistence.

**Auto-start ydotoold at boot** — create `/etc/systemd/system/ydotoold.service`:

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

**Linux input event key codes** (from `<linux/input-event-codes.h>`):

| Key | Code |
|---|---|
| Enter | 28 |
| Left Ctrl | 29 |
| V | 47 |

Syntax: `ydotool key <code>:<value>` where `1` = press, `0` = release.

**Verification:**
```sh
YDOTOOL_SOCKET=/tmp/.ydotool_socket ydotool key 28:1 28:0
```

This should trigger an Enter keypress in whatever window has focus.

**Troubleshooting:**
- `failed to open uinput device: Permission denied` — ydotoold must run as root (`sudo ydotoold`)
- `failed to connect socket ... No such file or directory` — set `YDOTOOL_SOCKET=/tmp/.ydotool_socket`
- Keys have no effect — check that the target window has focus and that ydotoold is running (`pgrep ydotoold`)
