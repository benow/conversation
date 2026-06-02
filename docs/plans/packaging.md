# Packaging & Distribution Plan

## Goal

Define how benow-conversation will be packaged, deployed, and run across environments:
- **Desktop Linux** (primary, current target) — always-running background daemon
- **NAS / Docker** (Synology, home server) — reliable 24/7 service for multiple machines
- **Desktop Windows** (future) — same always-running experience
- **Desktop macOS** (future) — same always-running experience

## Current State

| Aspect | Status |
|---|---|
| Application type | .NET 10 console app (`Microsoft.NET.Sdk.Web`, `OutputType: Exe`) |
| Modes | CLI TTS, daemon proxy, STT, STT+daemon |
| Platform support | Linux/Wayland only (PipeWire, evdev, ydotool, wl-clipboard) |
| External deps | ffmpeg, ffplay, ydotool, ydotool daemon, wl-clipboard (manual apt install) |
| Startup | Manual: `dotnet run --project src/benow-conversation -- --stt --daemon` |
| Docker | One Dockerfile exists but only for CosyVoice TTS backend (not the .NET app) |
| CI/CD | None |
| Web UI | None — all configuration via CLI flags and hand-editing JSON |
| Self-contained publish | Not currently used |

## Architecture: Two-Component Split

The current monolith runs all modes in a single process. For packaging, split into two components that communicate over HTTP:

```
┌─────────────────────────────────┐     HTTP      ┌──────────────────────────────┐
│         benow-agent             │◄─────────────►│       benow-service           │
│   (thin desktop client)         │               │   (backend, can run remotely) │
│                                 │               │                              │
│  • Keyboard trigger (hotkey)    │               │  • TTS synthesis (OpenRouter) │
│  • Audio capture (mic)          │  audio upload  │  • Transcription (Groq)       │
│  • Clipboard integration        │  request ────► │  • LLM text transformation   │
│  • Keyboard simulation (paste)  │◄──── transcript│  • Multi-character pipeline   │
│  • Audio playback (local)       │  ──── audio ──►│  • Proxy mode (OpenAI API)    │
│                                 │◄── stream      │  • Configuration web UI      │
│                                 │               │  • Persona management         │
│  Platform-specific impls:       │               │                              │
│    Linux: evdev/PipeWire/       │               │  Platform-agnostic:           │
│           ydotool/wl-clipboard  │               │    Pure HTTP + external tools │
│    Windows: WASAPI/SendInput/   │               │                              │
│             Win32 clipboard     │               │  Deployment:                  │
│    macOS: AudioToolbox/         │               │    Docker, systemd, bare metal│
│           CGEvent/NSPasteboard  │               │                              │
└─────────────────────────────────┘               └──────────────────────────────┘
```

### Component Roles

**benow-agent** (thin, platform-specific):
- Lightweight, minimal dependencies beyond platform audio/keyboard APIs
- Captures mic audio when hotkey pressed, sends to service for transcription
- Optionally handles local TTS audio playback (streamed from service)
- Paste transcript into focused application
- Must run on the desktop machine (needs mic, keyboard, clipboard access)

**benow-service** (heavy, platform-agnostic):
- All the current TTS, STT transcription, proxy, multi-character logic
- OpenAI-compatible proxy on port 8080
- REST API for agents to submit audio, receive transcripts
- Web configuration UI (Blazor or minimal SPA)
- Can run: locally (same machine), on NAS (Docker), or cloud VPS
- No platform-specific hardware dependencies (pure HTTP + external API calls)

### Communication Model

The agent talks to the service via REST API:

| Endpoint | Method | Purpose |
|---|---|---|
| `/api/stt/transcribe` | POST | Upload audio, receive transcript |
| `/api/config` | GET/PUT | Read/write configuration |
| `/api/personas` | GET/PUT | Manage TTS personas |
| `/api/status` | GET | Service health and status |
| `/v1/chat/completions` | POST | Existing proxy endpoint |
| `/v1/*` | * | Existing proxy pass-through |

Co-located mode: Both run on the same machine, agent connects to `localhost:8080`.
Remote mode: Service runs on NAS/Docker, agent connects to `http://nas:8080`.

## Distribution Formats

### 1. Docker (Primary for NAS / Headless)

**benow-service** as a Docker image:

```
docker-compose.yml
├── benow-service         # .NET app, port 8080
│   volumes:
│     - ./config:/config   # appsettings.json, persona data
│     - ./logs:/logs       # rotating logs
│   environment:
│     - DOTNET_ENVIRONMENT=Production
│   ports:
│     - "8080:8080"
```

- Multi-arch image: `linux/amd64`, `linux/arm64` (for Synology NAS, Raspberry Pi)
- Published to GitHub Container Registry (`ghcr.io/benow/conversation-service`)
- Docker Compose for one-command deployment
- Volume mounts for persistent config (persona assignments, API keys, voice mappings)

**benow-agent** is NOT Dockerized — must run on bare metal for hardware access.

### 2. Self-Contained Binary (Linux Desktop)

.NET 10 single-file publish:

```sh
dotnet publish src/benow-conversation -c Release \
  -r linux-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:PublishTrimmed=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -o publish/linux-x64/
```

- Single binary: `benow-conversation` (~60-80 MB trimmed)
- Bundles .NET runtime — no SDK required on target
- Includes both agent and service modes in one binary
- Mode selected by CLI: `benow-conversation --stt --daemon` (co-located) or `benow-conversation agent --service-url http://nas:8080`

### 3. systemd User Service (Auto-Start)

systemd user unit for automatic startup on login:

```ini
# ~/.config/systemd/user/benow-conversation.service
[Unit]
Description=Benow Conversation (STT + Daemon)
After=network-online.target pipewire.service

[Service]
Type=simple
ExecStart=/usr/local/bin/benow-conversation --stt --daemon
Restart=on-failure
RestartSec=5
Environment=DOTNET_ENVIRONMENT=Production

[Install]
WantedBy=default.target
```

Commands:
```sh
systemctl --user enable benow-conversation
systemctl --user start benow-conversation
```

### 4. Debian/Ubuntu Package (.deb)

For system-wide installation with systemd service:

```
benow-conversation_1.0.0_amd64.deb
├── /usr/bin/benow-conversation     # self-contained binary
├── /etc/benow-conversation/
│   └── appsettings.json            # default config template
├── /usr/lib/systemd/user/
│   └── benow-conversation.service  # user systemd unit
└── DEBIAN/
    ├── control                     # dep: ffmpeg, ydotool, wl-clipboard
    └── postinst                    # systemd enable, input group check
```

### 5. Manual Tarball

Fallback for any Linux distro:

```sh
curl -fsSL https://github.com/benow/conversation/releases/latest/download/benow-conversation-linux-x64.tar.gz | tar xz
./benow-conversation --stt --daemon
```

## Platform Strategy

### Linux (Current, Primary)

| Concern | Current State | Packaging Plan |
|---|---|---|
| Audio capture | PipeWire via ffmpeg `-f pulse` | Keep; add native PipeWire client as stretch |
| Keyboard trigger | evdev `/dev/input/eventX` | Keep; P/Invoke works on all Linux |
| Clipboard | `wl-copy` (Wayland) | Add `xclip` for X11, detect compositor |
| Keyboard sim | ydotool | Keep for Wayland; add `xdotool` for X11 |
| Audio playback | ffplay (from ffmpeg) | Keep; bundle ffmpeg or document dependency |
| Bluetooth | bluez + AVRCP | Keep |
| Package mgmt | None | .deb + tarball |
| Auto-start | Manual | systemd user service |
| Updates | None | GitHub Releases + self-update check |

### Windows

| Concern | Approach |
|---|---|
| Audio capture | Windows Audio Session API (WASAPI) via **NAudio** NuGet package (`NAudio.Wasapi`) |
| Keyboard trigger | Global hotkey via `RegisterHotKey` P/Invoke (`user32.dll`) |
| Clipboard | `System.Windows.Forms.Clipboard` or `TextCopy` NuGet for .NET native |
| Keyboard sim | `SendInput` P/Invoke (`user32.dll`) for Ctrl+V and Enter |
| Audio playback | NAudio or bundled ffplay (ffmpeg for Windows) |
| Package mgmt | MSI installer via WiX Toolset, or `winget` package |
| Auto-start | Windows Task Scheduler or Startup folder shortcut |

Windows agent needs a new .NET project: `src/benow-agent-windows`.

### macOS

| Concern | Approach |
|---|---|
| Audio capture | AudioToolbox / AVAudioEngine via .NET interop |
| Keyboard trigger | `CGEvent` tap via P/Invoke (`Carbon.framework`) |
| Clipboard | `NSPasteboard` via P/Invoke |
| Keyboard sim | `CGEventPost` via P/Invoke |
| Audio playback | `afplay` (built-in) or bundled ffplay |
| Package mgmt | `.app` bundle, Homebrew formula |
| Auto-start | LaunchAgent plist in `~/Library/LaunchAgents/` |

macOS agent needs a new .NET project: `src/benow-agent-mac`.

### Platform Abstraction in Code

The existing `Services/Abstractions/` interfaces already support this:

```csharp
public interface IAudioRecorder { ... }        // PipeWireRecorder | WasapiRecorder | CoreAudioRecorder
public interface IRecordingTrigger { ... }     // EvdevKeyboardTrigger | Win32HotkeyTrigger | CGEventTrigger
public interface IClipboardService { ... }     // WaylandClipboardService | Win32ClipboardService | MacClipboardService
public interface IKeyboardSimulator { ... }    // YdotoolKeyboardSimulator | SendInputSimulator | CGEventSimulator
```

When co-located (agent + service in same process), implementations are direct. When split (agent remote), the agent uses HTTP-backed implementations that call the service.

## Web Configuration UI

A web frontend for managing configuration without editing JSON by hand:

**Approach**: Minimal ASP.NET Core-hosted Blazor or SPA (single-page app) served from the daemon process.

**Pages**:
| Page | Purpose |
|---|---|
| Dashboard | Service status, uptime, log tail |
| API Keys | OpenRouter key, Groq key, Mistral key |
| Personas | Create/edit TTS personas (model, voice, instructions, gender) |
| Voice Cloning | Upload/record 3-second samples, manage voice IDs |
| Audio Devices | Select output device, test playback, set volume |
| STT Settings | Hotkey, silence detection, beep feedback |
| Model Selection | Choose backend LLM model, TTS model |
| Proxy Settings | Bind address, port, TTS persona for proxied chats |
| Logs | View/search recent logs |

**Architecture**:
- Served by the same ASP.NET Core host that runs the proxy (port 8080)
- Static files for SPA (React/Vue/lit) or Blazor WASM
- REST API endpoints for all CRUD operations (`/api/config`, `/api/personas`, etc.)
- Configuration changes auto-persist to `appsettings.json`

**Path routing**:
| Path | Handler |
|---|---|
| `/` | Web UI (SPA shell) |
| `/api/*` | Configuration REST API |
| `/v1/*` | OpenAI-compatible proxy |
| `/` (when no UI built) | Redirect to `/status` |

## CI/CD Pipeline (GitHub Actions)

### Workflow 1: Build & Test
Trigger: every push, every PR
- Restore .NET dependencies
- Build solution (`dotnet build`)
- Run tests (`dotnet test` with coverage)
- Lint check

### Workflow 2: Release
Trigger: tag push (`v*`)
- Build self-contained binaries for:
  - `linux-x64` (tarball + .deb)
  - `linux-arm64` (tarball — for Raspberry Pi)
  - `win-x64` (when Windows agent exists, .zip)
  - `osx-arm64` (when macOS agent exists, .tar.gz)
- Build Docker image:
  - `linux/amd64`
  - `linux/arm64`
- Push Docker image to `ghcr.io/benow/conversation-service:${{ github.ref_name }}`
- Create GitHub Release with all artifacts
- Generate changelog from conventional commits

### Workflow 3: Docker Nightly
Trigger: daily cron
- Build and push Docker image tagged `:latest` and `:nightly-YYYYMMDD`

### GitHub Actions File Structure

```yaml
# .github/workflows/build.yml
name: Build & Test
on: [push, pull_request]
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: '10.0.x' }
      - run: dotnet restore
      - run: dotnet build --no-restore
      - run: dotnet test --no-build --collect:"XPlat Code Coverage"

# .github/workflows/release.yml
name: Release
on:
  push:
    tags: ['v*']
jobs:
  build-binaries:
    strategy:
      matrix:
        rid: [linux-x64, linux-arm64]
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: '10.0.x' }
      - run: dotnet publish src/benow-conversation -c Release -r ${{ matrix.rid }} --self-contained -p:PublishSingleFile=true -p:PublishTrimmed=true -o publish/${{ matrix.rid }}
      - uses: actions/upload-artifact@v4
        with:
          name: benow-conversation-${{ matrix.rid }}
          path: publish/${{ matrix.rid }}

  build-docker:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: docker/setup-qemu-action@v3
      - uses: docker/setup-buildx-action@v3
      - uses: docker/login-action@v3
        with:
          registry: ghcr.io
          username: ${{ github.actor }}
          password: ${{ secrets.GITHUB_TOKEN }}
      - uses: docker/build-push-action@v5
        with:
          context: .
          file: ./src/benow-conversation/Dockerfile
          platforms: linux/amd64,linux/arm64
          push: true
          tags: ghcr.io/benow/conversation-service:${{ github.ref_name }}

  create-release:
    needs: [build-binaries, build-docker]
    runs-on: ubuntu-latest
    steps:
      - uses: actions/download-artifact@v4
      - run: # create .deb packages
      - uses: softprops/action-gh-release@v1
        with:
          files: artifacts/*
          generate_release_notes: true
```

## Project Structure (Post-Split)

```
benow-conversation/
├── benow-conversation.slnx
├── src/
│   ├── benow-conversation/
│   │   ├── benow-conversation.csproj       # Main project (service + co-located agent)
│   │   ├── Program.cs
│   │   ├── Dockerfile                       # Multi-stage Docker build for service
│   │   ├── appsettings.json
│   │   ├── Services/
│   │   │   ├── Abstractions/                # Platform abstraction interfaces
│   │   │   ├── Stt/                         # STT pipeline (cross-platform orchestrator)
│   │   │   │   ├── Linux/                   # evdev, PipeWire, ydotool, wl-clipboard impls
│   │   │   │   ├── Windows/                 # WASAPI, SendInput, Win32 clipboard impls
│   │   │   │   └── Mac/                     # CGEvent, AudioToolbox, NSPasteboard impls
│   │   │   ├── TtsService.cs               # TTS engine
│   │   │   ├── ProxyService.cs             # OpenAI-compatible proxy
│   │   │   ├── SpeechQueue.cs              # Queued playback
│   │   │   └── ...
│   │   ├── Web/                             # Web UI (Blazor or SPA static files)
│   │   └── wwwroot/                         # Static web assets
│   ├── benow-agent-linux/                   # (future: thin Linux agent only)
│   ├── benow-agent-windows/                 # (future: thin Windows agent)
│   └── benow-agent-mac/                     # (future: thin macOS agent)
├── tests/
│   └── benow-conversation.Tests/
├── scripts/
│   ├── Dockerfile                           # Service Dockerfile
│   ├── docker-compose.yml                   # Docker Compose for NAS deployment
│   ├── benow-conversation.service           # systemd user unit
│   ├── install.sh                           # One-command install script
│   ├── build-deb.sh                         # .deb packaging script
│   └── (existing TTS server scripts)
├── .github/
│   └── workflows/
│       ├── build.yml                        # CI: build + test
│       ├── release.yml                      # CD: build binaries, Docker, create release
│       └── docker-nightly.yml               # Nightly Docker image
└── docs/
    └── plans/
        └── packaging.md                     # This document
```

## Implementation Roadmap

### Phase 1: Docker + Self-Contained Binary (2-3 days)
**Goal**: Run on NAS via Docker, run locally as self-contained binary. Current functionality unchanged.

1. **Dockerfile for the .NET service** — multi-stage build, slim base image (`mcr.microsoft.com/dotnet/runtime:10.0-noble-chiseled` or `alpine`), expose port 8080, volume for config
2. **docker-compose.yml** — service container + config volume + log volume
3. **Self-contained publish** — single-file trimmed binary for `linux-x64`
4. **systemd user unit file** — auto-start on login
5. **install.sh** — one-command setup: download binary, place in `/usr/local/bin`, install systemd unit, check deps
6. **GitHub Actions build workflow** — build + test on push/PR

**Deliverable**: `docker compose up -d` on NAS runs the proxy. `install.sh` sets up desktop auto-start.

### Phase 2: Web Configuration UI (2-3 days)
**Goal**: Manage all settings through a browser instead of editing JSON.

1. Static file serving from daemon process (existing ASP.NET Core host)
2. REST API endpoints (`/api/config`, `/api/personas`, `/api/status`)
3. Minimal SPA frontend (vanilla JS or lit-element) with pages for:
   - API key management
   - Persona editor
   - Model/voice settings
   - Audio device selection
4. Serve on `/` (root path) when daemon is running

**Deliverable**: Open `http://localhost:8080` to configure everything visually.

### Phase 3: Remote Agent Mode (3-4 days)
**Goal**: Agent connects to service running elsewhere (NAS, another machine).

1. REST API on service for audio upload → transcription
2. `ISttRunner` variant that calls service HTTP endpoints instead of local implementations
3. Agent CLI: `benow-conversation agent --service-url http://nas:8080`
4. Agent handles only: keyboard trigger, audio capture/playback, clipboard, keyboard simulation
5. Service handles: transcription, TTS, proxy, multi-character

**Deliverable**: Desktop machine runs thin agent, NAS runs full service. Dictation works across network.

### Phase 4: Windows Agent (3-4 days)
**Goal**: Same dictation experience on Windows.

1. New project: `src/benow-agent-windows` — Windows-targeted agent
2. Implement platform abstractions:
   - `WasapiRecorder` (NAudio)
   - `Win32HotkeyTrigger` (global hotkey)
   - `Win32ClipboardService` (System.Windows.Forms or P/Invoke)
   - `SendInputKeyboardSimulator`
   - `WasapiAudioPlayer` or ffplay for Windows
3. MSI installer via WiX
4. Windows service or tray app for auto-start

### Phase 5: macOS Agent (2-3 days)
**Goal**: Same dictation experience on macOS.

1. New project: `src/benow-agent-mac`
2. Implement platform abstractions:
   - `CoreAudioRecorder`
   - `CGEventHotkeyTrigger`
   - `NSPasteboardClipboardService`
   - `CGEventKeyboardSimulator`
3. `.app` bundle + Homebrew formula

### Phase 6: Polish & Distribution (ongoing)
- .deb package generation
- AppImage for portable Linux
- `winget` package for Windows
- Auto-update mechanism (check GitHub Releases)
- Tray icon with status indicator, recording feedback
- Send desktop notifications for errors, status changes

## Prioritization

For a single Linux user wanting it "just working":

| Priority | Item | Why |
|---|---|---|
| **1** | systemd user service | Auto-start on login, no manual `dotnet run` |
| **2** | Self-contained binary | No .NET SDK needed, just download and run |
| **3** | Docker service | Run proxy on NAS, reliable 24/7 uptime |
| **4** | GitHub Actions CI | Automated builds, tests, catch regressions |
| **5** | Web configuration UI | Stop hand-editing JSON, visual persona management |
| **6** | Remote agent mode | Desktop ⇄ NAS split architecture |
| **7** | Cross-platform agents | Windows, macOS support |

For immediate-next steps (most practical impact):

1. Write a systemd user unit file and an install script
2. Add a `Dockerfile` for the .NET service and `docker-compose.yml`
3. Set up GitHub Actions for build/test

## Key Technical Decisions

### Docker Base Image
- **Recommendation**: `mcr.microsoft.com/dotnet/runtime:10.0-noble-chiseled` (Ubuntu-based, minimal, ~100MB compressed)
- Requires `--self-contained` publish for trimming compatibility
- No shell, no package manager — but we don't need either (all logic is .NET, external tools only needed on the agent side)

### File System Layout (Linux)
```
/usr/local/bin/benow-conversation     # self-contained binary
/etc/benow-conversation/              # config (symlink or copy from ~/.config)
~/.config/benow-conversation/         # user config (appsettings.json, personas)
~/.local/share/benow-conversation/    # data (voice assignments, persona usage)
~/.cache/benow-conversation/          # temp files
```

### Config File Strategy
- `appsettings.json` ships with defaults (empty API keys)
- User fills in keys via web UI or editing `~/.config/benow-conversation/appsettings.json`
- Docker: mount `./config` volume as `/config`, set `DOTNET_ENVIRONMENT` to `Production`
- Never bundle actual secrets in any package

### Security
- API keys in user home directory, not world-readable
- Docker secrets via environment variables (not committed to git)
- Proxy binds `0.0.0.0:8080` — users should firewall externally or bind `127.0.0.1` in config
- HTTPS not needed for local/proxy traffic; add if exposing to internet

## Open Questions

1. **Agent ↔ Service auth**: If service is on NAS, should agent require an API key? Probably not for local network, but document firewall implications.
2. **Multi-machine persona sync**: If using same service from multiple desktops, persona/voice assignments should be consistent. Stored on service side (in config volume).
3. **GPU acceleration for local TTS**: If running Kokoro/CosyVoice locally on NAS with GPU, Docker needs GPU passthrough (`--device /dev/dri` for Intel, NVIDIA runtime for nvidia). Separate concern from core packaging.
4. **Blazor vs SPA for web UI**: Blazor WASM requires fewer build tools but slower initial load. SPA (lit-element) is simpler and lighter. Decision deferred to Phase 2.
5. **Windows store / winget**: Worth investigating for Phase 4, but not blocking.
6. **ydotool daemon requirement**: On Linux, `ydotoold` must run as root. The install script should check for it and provide setup instructions. This is an unavoidable Wayland limitation.
