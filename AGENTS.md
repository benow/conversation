# AGENTS.md — Project Context

## Build & Test
```bash
dotnet build          # Build the project
dotnet test           # Run all tests (~184 pass, 4 skipped)
dotnet test --filter "GoldenMaster"  # Run regression tests only
```

## Key Files
- `src/benow-conversation/Program.cs` — Entry point, DI setup, pending-test enforcement
- `src/benow-conversation/Services/ProxyService.cs` — LLM proxy + TTS pipeline
- `src/benow-conversation/Services/ParagraphSplitter.cs` — Eager first-sentence + paragraph TTS chunker
- `src/benow-conversation/Services/ModifierInjector.cs` — Injects (modifier) tags (single LLM, integrity checked)
- `src/benow-conversation/Services/CharacterParser.cs` — Script→segments parser (golden-master tested)
- `src/benow-conversation/Services/ParallelTtsPlayer.cs` — TTS synthesis + playback (2 retries)
- `src/benow-conversation/appsettings.json` — Production: personas, voices, model, prompts
- `src/benow-conversation/appsettings.Development.json` — Dev overrides (EnforceRegressionTests: true)
- `src/benow-conversation/Configuration/AppSettings.cs` — Config types + defaults

## Documentation
- `docs/debugging.md` — Debugging (log patterns, pipeline flow, testing, runtime enforcement)
- `docs/voice-cloning.md` — Voice cloning attempts + stability fixes
- `docs/overview.md` — Architecture overview
- `docs/configuration.md` — Config reference
- `docs/proxy-service.md` — Proxy/TTS pipeline details
- `docs/plans/` — Design history (local-tts, optimization, multi-character)

## Pipeline Notes
- **AudioFormatConverter**: converts all TTS audio to PCM before piping. Providers declare format via `ITtsProvider.OutputFormat`. Pipeline (ffplay) is always `-f s16le -ar 24000`. WAV→PCM strips RIFF header; MP3→PCM uses persistent ffmpeg subprocess; PCM→PCM passthrough.
- **ProviderFormatCache**: persists detected audio formats per model/provider to `appsettings.json` (`ProviderFormats`). Cleared on `/reset`.
- **ParagraphSplitter**: emits first sentence eagerly, then groups by paragraph for voice tone consistency
- **ModifierInjector**: validates alpha-numeric ratio (0.60–2.5) after stripping (modifier) tags. Falls back to original on failure.
- **CharacterNormalizer**: validates α ratio ≥50% of input. Falls back on failure.
- **StripStrayMarkers**: regex-matches known markers only — no aggressive bracket stripping.
- **ParallelTtsPlayer**: 2 retries, 500ms/1000ms backoff on TTS failure. Segments skipped only if all retries exhausted.
- **Text coverage**: warns <85%, errors <50%. At <50%, emits `.pending` fixture.
- **Runtime enforcement**: `.pending` fixtures in `tests/.../fixtures/` block daemon startup when `EnforceRegressionTests: true` (dev).
- **Persona persistence**: saved to `appsettings.json`. Reset with `/reset` in chat.

## Simplification (2026-05-31)
- Model arrays → single strings (`ModifierModel`, `NormalizerModel` — DeepSeek Flash)
- Removed `HoldResponseForAudio` (~170 lines)
- Removed `IsMultiCharacterProse` + prose normalizer path + `suppressedChunkedTts`
- Removed `PersistentPipeline: false` dead branch
- Fixed `TranscriptCleanupOff` → `TranscriptCleanup` JSON key (was never running)
- Chunking: paragraph groups replace sentence-by-sentence (tone consistency)

## TTS Backend
- Default: OpenRouter (`openai/gpt-4o-mini-tts-2025-12-15`)
- Local: Kokoro-82M on port 50001 (12 voices, no cloning)
- Config: `TtsBackend` → `"openrouter"` | `"kokoro"`

## Agent Instructions
- Keep this file concise. Compress when adding. Remove stale info rather than appending.
- Prefer references to `docs/` files over inline explanations.
- After every non-trivial operation (feature, bug fix, refactor), once `dotnet test` passes with 0 failures, run `git add -A && git commit -m "<summary>"`. Never leave tested code uncommitted.

## Conversation V2 phase 1 — core extraction (started 2026-09-17)

Plan: `docs/plans/conversation-v2.md` in the nastv repo. `src/Benow.Conversation.Core` is the
shared engine (NuGet on nuget.benow.ca — anonymous pull; publish from tags via
`.github/workflows/publish-nuget.yml`). Ported so far (tranche 1, from NASTV
`nastv-player-core/Voice/`, namespaces `Benow.Conversation.Voice`): VoiceVAD (energy VAD,
auto-calibration, sentence-paced segment minimums), SentenceAccumulator + SentenceSegment
(sentence-boundary flush, run-on cap), TtsChunkPacer (3-stage progressive-TTS pacing),
WavWrapper (RIFF wrap — NASTV's plugin-side WrapAsWav is a duplicate; collapses in phase 4),
and the service seams (ITranscriptionService/TranscriptionResult, ITtsService/TtsAudio,
IVoiceLlmService/ChatTurn). Tests: `tests/Benow.Conversation.Core.Tests` (20, ported from
nastv-player-core.tests). Dependency ceiling: Logging.Abstractions only.
Tranche 2 (provider layer, `Benow.Conversation.Providers`): IAiProvider + AiCapability +
ModelInfo with a NEUTRAL ProviderConfigField/SelectOption (plugin maps to SDK types at
phase-4 adoption); Groq/OpenRouter/Replicate/OpenAI-compatible providers (behavior verbatim —
incl. the known shared-HttpClient Authorization wart, nastv finding
ai-plugin/provider-httpclient-auth-accumulation, fix at adoption); AiProviderInstances catalog
helpers over ILegacyProviderConfig (PluginConfig/desktop settings implement it); ModelCapabilityCache.
ConversationJson.Options mirrors NastvShared.JsonDefaults (camelCase + case-insensitive — the
case-sensitivity bug class). 27 tests green.
Tranche 3 (LLM turn strategy, `Benow.Conversation.Llm`): PromptBuilders (extractor/speaker
message builders — F4/D6 invariants guarded by ported ExtractorPipelineCompositionTests;
persona NEVER reaches the extractor, speaker NEVER sees tool schemas; persona-first ordering
with grounding anchor last) + PromptConstants; LlmProtocol (tool-envelope brace-walker,
SplitForStreaming, tools-unsupported classification, provider/model mismatch classify,
BrowserUserAgent); ProviderPacing (SttPacer 600ms + SttGate, TtsReplicateGate — the
Groq/Cloudflare and Replicate-serial-GPU lessons); WavDecoder (full RIFF walk, float/8/16/24-bit,
downmix; BuildProviderRoute/ResolveExtractorModel take primitives, no config-object coupling).
Tranche 4 (V1 text pipeline, `Benow.Conversation.Tts`): CharacterParser + CharacterSegment
(golden-master fixtures ported — Core parser reproduces V1 outputs EXACTLY), SentenceSplitter,
ParagraphSplitter. CharacterNormalizer/PersonaAllocator stay V1-side (AppSettings/HttpClient
coupling — seams designed in phase 2 with the service).
PHASE 1 EXIT (2026-09-17): 0.1.0 tagged; full suite 108 Core + 184 V1 (4 skip baseline) green —
V1 CLI behavior unchanged. Package on nuget.benow.ca via tag-driven publish-nuget.yml.

## Conversation V2 phase 2 — service + local loop (2026-09-18)

`Benow.Conversation.Core` now carries the runnable engine, not just ports:
- **Audio** (`Benow.Conversation.Audio`): `PcmPlaybackPipeline` (persistent ffplay fed s16le PCM;
  orphan sweep, idle restart, broken-pipe restart-retry, interrupt), `SpeechQueue` (serial speak
  queue over ITtsService; **chunks of one reply enqueue with cancelCurrent:false** — the
  per-chunk cancel bug was caught by the live e2e run and is regression-tested), `FfmpegAudioRecorder`
  (cross-platform: Linux pulse / Windows dshow / macOS avfoundation; SIGTERM-then-kill finalize),
  `AudioDeviceEnumerator` (pactl / ffmpeg -list_devices; monitors filtered).
- **Stt/**: `WhisperSttClient` — Groq Whisper with WAV wrap, temperature=0, SttGate + 600ms pacing,
  browser UA, direct HTTPS only.
- **Llm/**: `ChatClient` — extractor/speaker (W14), raw-LLM when no extractor or UseTools=false,
  D5 tools-unsupported degradation, empty-pass-2 nudge retry, IToolExecutor seam.
- **Tts/**: `OpenAiTtsClient` (audio/speech PCM), `ReplicateTtsClient` (xtts cloning; versioned vs
  versionless URL rule; gate-serialized create+poll; empty-voice auto-pick), `KokoroTtsClient`
  (local offline server; **run CPU-only**: `HIP_VISIBLE_DEVICES=""` — the ROCm torch build fails
  with "HIP error: invalid device function" on this GPU).
- **Engine/**: `ConversationEngine` — frames → VAD → STT (partials + ONE corrected final via
  full-audio re-transcription) and text → LLM → sentence accumulator → 3-stage pacer → speech queue.
  **Muted = engine-level skip: the TTS provider is never called** (plan §4.4 contract).
- **Config/**: `ConfigStore` (JSON at ~/.config/conversation/config.json, atomic write, 0600).
- **src/Benow.Conversation.Lab**: headless verification harness (`--devices | --tts | --dictate |
  --converse | --text [--play] [--mute]`).

Verified live 2026-09-18 (laptop, real providers): 14-min dictation via Groq Whisper; full turn
Groq STT → OpenRouter LLM → Replicate xtts (cloned emma-stone voice) → ffplay with progressive
pacing (59c first chunk → 336c steady state). Live-run bugs found and fixed: per-chunk speech
cancellation; `ChannelReader.Count` throws on unbounded+SingleReader channels (explicit counter now).

### Phase-2 live-run findings (2026-09-18, all fixed + regression-tested)

1. **Progressive TTS chunks cancelled each other** — SpeechQueue.Enqueue defaulted to
   cancelCurrent:true; chunks of ONE reply must queue in sequence (`cancelCurrent:false`), only a
   new turn cancels. Symptom: only the last chunk was ever heard.
2. **`ChannelReader.Count` throws** on unbounded channels with SingleReader=true — SpeechQueue
   tracks queue depth with its own Interlocked counter.
3. **Full-audio correction TRUNCATED long turns** — the 30s correction window (a NASTV turn
   constant) replaced the whole assembled transcript with its first 30 seconds; a 5-minute
   dictation came back as 496 chars. Now: over-cap turns skip correction (keep assembled), and a
   "correction" under 80% of the assembled length is rejected as a provider hiccup.
4. **Groq 429s during long dictation dropped segments silently** — 97/154 segments of a 14-minute
   run were lost. The 600ms gate protects against Cloudflare 403 BURSTS (403) but a continuous
   dictation exhausts the per-minute audio budget (429). Now: 429 → honor retry-after (≤30s) →
   retry once → `MinSpacingPacer.Backoff()` so later segments slow down. Verified: 5-min run with
   23 × 429s → 0 drops, 720 words transcribed (~96% of expected).
5. **ffplay/ffmpeg orphan sweeps were blanket kills** — they killed EVERY ffplay/ffmpeg on the
   box (including unrelated players and the other test assembly's processes → parallel-test
   interference). Now marker-scoped: ffplay carries `-window_title conversation-pcm`, ffmpeg
   carries `-metadata title=conversation-capture`; sweeps pgrep only their own marker.

**Not yet in phase 2**: the ASP.NET WS/SSE host (the desktop app hosts these classes in-proc;
NASTV-backed mode is phase 5) and NASTV's parallel segment dispatch + pair correction (the
SttGate serializes provider calls anyway — sequential dispatch is equivalent in practice).

**Local verification setup (laptop)**: `scripts/kokoro-server.py` from the V1 checkout runs
CPU-only with `HIP_VISIBLE_DEVICES="" CUDA_VISIBLE_DEVICES=""` (the ROCm torch build fails with
"HIP error: invalid device function" on this GPU); keys live in `~/.config/conversation/config.json`
(0600, written by hand or `ConfigStore` env seeding: GROQ_API_KEY / OPENROUTER_API_KEY /
REPLICATE_API_TOKEN). Production TTS is Replicate `lucataco/xtts-v2:<hash>` with a reference WAV
from the voice library (clean voices: emma-stone.wav, cap-01..12.wav).

## Voice library (phase 2 review, 2026-09-18)

`Benow.Conversation.Voices` ports NASTV's reference-voice import pipeline server-side so the
desktop app and NASTV (phase 4) share it — previously it lived only in the frontend
(voiceReference.ts / voiceSampleApi.ts):
- `VoiceReference` — batch analysis (20ms RMS frames, percentile noise floor, 200ms pause gaps)
  + best-segment selection (one long stable run preferred; runs concatenated chronologically
  when no single run is long enough). Pure; unit-tested with synthetic PCM.
- `VoiceLibrary` — import ANY audio format (ffmpeg decode → analyze → trim → 16 kHz mono 16-bit
  WAV), record from the mic (FfmpegAudioRecorder), list, delete. Sub-6s references are saved but
  flagged ("cloned voice may sound robotic") like NASTV.
- **Library location**: `~/.config/conversation/voices` — USER ASSETS, NEVER committed. The repo
  `voices/` dir is also gitignored (V1 assets). Verified: `git status` shows no voice files.
- **Deviation from the TS original (deliberate, 2026-09-18)**: the threshold now caps at half the
  90th-percentile level. The original's `noiseFloor*4` assumed ≥10% non-speech frames — an
  ideal reference clip (nearly all speech) computed a threshold ABOVE the speech and detected
  ZERO runs (a 12s tone in a 13s file → no speech). The cap keeps speech-dense files detectable
  while silence-only files still yield none.
- **Lab commands**: `--voices`, `--voice-import <file> [name]`, `--voice-import-dir <dir>`,
  `--voice-record <name> [sec]`, `--voice-delete <name>`, `--voice <name>` (override for --tts).
- Migrated 2026-09-18: the 34 NASTV voices (NAS /app/voices) imported into the user library —
  normalized 16 kHz mono, analyzed, warnings surfaced for short references. Verified by
  synthesizing with an imported voice through Replicate xtts.

## Desktop app (phase 3, 2026-09-18)

`src/Benow.Conversation.Desktop` (Avalonia 12.1.2) is the tray-first shell; `Program.cs` is the
composition root and `DesktopHost` is the toolkit-free brain (all decision logic, unit-tested).

- **One engine, two verbs** — `Speak` (voice → transcript → clipboard + synthesized Ctrl+V into
  the focused app) and `Converse` (voice or typed text → LLM → streaming text + TTS). A persona
  switch applies the bundle (prompt + speaker model + TTS engine + voice) via `PersonaBinding`
  and clears history; STT and the extractor model are deliberately persona-agnostic.
- **`--show`** opens the Converse overlay at startup. Needed because GNOME only shows tray icons
  with the AppIndicator extension LOADED (installed ≠ loaded: enabling it takes effect at the
  next shell start). Hotkeys work regardless — they read `/dev/input` via evdev, not the shell.
- **The host attaches its own event sink**: the engine publishes through `SinkRelay` (replaceable)
  and a late-attaching host wraps whatever is already there in a `CompositeSink`. A sink that is
  not attached fails invisibly — the overlay just stays empty — so the host attaches it itself
  rather than trusting the wiring order. `ChatClient.OnError` is a settable property for the same
  reason: a sink captured at construction goes stale when the UI attaches later.
- **Provider failures are surfaced, not swallowed**: `ChatClient` reports unreachable providers /
  rejected keys through `OnError` → engine sink → overlay status `error`. Previously a failed turn
  returned an empty result, which the UI showed as "no reply" — indistinguishable from a model
  that had nothing to say.
- **Settings are browser-served** from a loopback listener (`SettingsHost`, port 8791): page +
  JSON API for config/devices/voices/personas. Provider keys are write-only (presence flags only,
  an empty field means "leave the stored key alone"). The page can speak a test phrase.
- **Tray menu is a pure model** (`TrayModel`) mapped onto `NativeMenuItem`s — that is what makes
  the menu testable without a windowing system (`TrayMenuRenderingTests` renders it headless).
- **Verification**: `dotnet test benow-conversation.slnx` (345 tests: 120 Core + 41 Desktop +
  184 V1/4 skipped). This box has NO screenshot tool — render the UI offscreen instead: an
  Avalonia headless harness (`Avalonia.Headless` + `.UseSkia()`, `UseHeadlessDrawing = false`)
  can build `ConverseOverlay`, drive host state, and `CaptureRenderedFrame().Save(png)`; inspect
  the PNG with the visor MCP. Two real layout bugs were found only this way (clipped hint text).

## Latency work (phase 3, 2026-09-18) — what the bench measured

`Lab --bench <wav>` runs a full turn through the REAL capture path (the WAV is replayed at native
rate by ffmpeg `-re`) and reports stop→transcript, stop→first token, stop→first audio, stop→last
audio and per-chunk playback gaps, with medians over `--repeat n`. Knobs: `--no-prewarm`,
`--warmup ms`, `--no-correction`, `--first chars`, `--para chars`, `--mute`, `--out json`.

**Three bugs the harness found (all fixed here, all regression-tested):**

1. **The VAD ate the opening words.** Speech during the 500ms calibration window was DISCARDED, so
   "What is a good way to keep coffee beans fresh" reached Whisper as "good way to keep coffee
   beans fresh". Now the window is kept as bounded pre-roll context prepended to the first segment,
   and the close/drop thresholds compare against SPEECH bytes (not segment length) so the pre-roll
   cannot make a 2.3s burst close as 2.6s. Transcript became word-perfect on every run.
   **NASTV's `nastv-player-core/Voice/VoiceVAD.cs:71` still has this bug** — finding filed.
2. **Synthesis and playback were serialized.** Chunks of one reply are enqueued as the LLM streams
   them, and the old loop synthesized a chunk, then piped it (blocking while ffplay played it at
   1x), then synthesized the next. Measured inter-chunk silence: **6.6s and 10.3s**. The queue is
   now two stages — a synthesizer filling a 2-chunk look-ahead buffer and a player draining it —
   and the worst gap fell to **1.2s**. NASTV solved this in 2026-08-19/20 (parallel synthesis +
   ordered emitter in `VoiceSession`); V2 re-derived it, which is exactly what the NASTV comment
   warned about. Residual ~1s gap on a 2-chunk reply is inherent: the buffer starts cold because
   chunk 1 must be synthesized before any audio plays.
3. **Three instrumentation lies** (worse than no data): `ItemPiped` fired after the pipe — which
   returns only once ffplay has consumed the audio — so "first audio" was up to 1.5s late;
   `LastAudioPiped` was never marked (read -1); and `QueuedCount == 0` was treated as "finished"
   although the queue passes through empty mid-reply. `PlaybackStarted` (before the pipe) and
   `SpeechQueue.IsIdle` (queued empty AND nothing in flight) fix all three.

**Where the time actually goes** (8s question, Replicate XTTS, deepseek-chat-v3.1 via OpenRouter):
`stop→transcript` ~1.3-1.6s (streaming STT + full-audio correction) · `stop→first token` 4-13s (the
LLM dominates and varies most) · `stop→first audio` 7.8-24.6s. **The dominant lever is reply
LENGTH**: Replicate XTTS costs ~43ms/char to synthesize against ~65ms/char of audio, so a 250-char
reply is ~11s of synthesis before you hear anything, and a 840-char reply ran the turn to 128s.
A persona prompt that keeps replies short beats any pipeline tuning; per-chunk look-ahead only
buys back the gaps. Measured (medians, 8s question, same providers):

| configuration | stop→first audio | worst chunk gap | stop→last audio |
|---|---|---|---|
| serial synthesis, 841-char reply | 24.6s | 10.3s | 128s |
| pipelined, 288-char reply | 9.5s | 1.2s | 27.6s |
| pipelined + brevity prompt, ~100-char reply | 7.5s | none (1 chunk) | 8.0s |

A one-chunk reply cannot stutter, so the brevity prompt buys both speed and seamlessness.
NOT yet swept (each run spends real provider time): prewarm/warmup off, correction off,
STT/model variants. `PrewarmPlayback` was a dead option (documented lever, never wired) — now
wired, worth ~0.5s off the first chunk.

**Test-interference trap**: the V1 `PersistentAudioPipeline` swept EVERY ffplay on the machine
(`GetProcessesByName`) — it killed V2's prewarmed player mid-pipe and hung the solution test run.
Both are marker-scoped now (`conversation-pcm` / `conversation-v1-pcm`); keep them that way.

## Desktop bootstrap + verification notes (2026-09-19)

- **Tray prerequisite is self-inflicted on GNOME**: AppIndicator is installed-but-disabled by
  default. Until there is an installer (deliberately deferred), the app should handle it at
  startup: if Linux+GNOME and `gnome-extensions info ubuntu-appindicators@ubuntu.com` shows
  installed-but-not-enabled → `gnome-extensions enable` (user-level, no root) + a `notify-send`
  note that the tray appears after re-login. That closes the "who tells the user to start it"
  loop: first launch is however they got the binary; from then on it can offer "start at login"
  (write `~/.config/autostart/conversation.desktop`). NOT YET IMPLEMENTED — planned.
- **Real-display pixel capture is blocked by GNOME policy**: Shell's Screenshot D-Bus API is
  AccessDenied (GNOME 41+), gnome-screenshot's X11 fallback captures nothing on Wayland, and
  x11grab reads XWayland's never-composited root (black). The portal needs an interactive consent
  click. What DOES verify the real desktop: `xwininfo -tree` (window mapped/geometry),
  `busctl get-property org.kde.StatusNotifierWatcher ... RegisteredStatusNotifierItems` (tray
  registered), `GetLayout` on the item's `/net/avaloniaui/dbusmenu/<id>` path (real menu labels —
  all 19 items verified), and Playwright + screenshot + vision for the settings page. The Avalonia
  headless renderer covers overlay pixels. gnome-screenshot is now installed but is a dead end on
  Wayland; don't reach for it again.

## IAudioOut — the playback seam (0.5.0/0.5.1, 2026-09-19)

`SpeechQueue` owns everything improvable (look-ahead synthesis, ordering, pacing, cancellation,
gap metrics); the last mile is `IAudioOut` — `PlayAsync(chunk, ct)` BLOCKS for the audio duration
(that backpressure is what the look-ahead overlaps), `ResetAsync()` = barge-in, optional
`PrewarmAsync`. Desktop ships `PcmPlaybackAudioOut` (ffplay); NASTV writes a SignalR sink. 0.5.1
added the two lifecycle signals a remote sink needs: `FallbackRaised` (TtsAudio.FallbackMessage —
the user must know the configured TTS engine is not the one speaking) and `Drained` (fires once
when queued + in-flight + look-ahead are all empty, checked on BOTH stages' item boundaries so a
fully-dropped turn still drains — regression-tested; a host waiting on Drained must never hang).
Publish flow: bump Version in the Core csproj → commit → tag vX.Y.Z → push both → CI publishes to
nuget.benow.ca. NOTE: NuGet's client HTTP cache holds package indexes ~30 min — a just-published
version needs `dotnet restore --no-http-cache` to be seen immediately.

## Adaptive TTS chunk growth (0.5.4, 2026-09-19) — the "pause after the first line" fix

Live on the TV, the user heard: first line plays, LONG pause, then the rest arrives in a burst.
Diagnosis: chunk 1 is one sentence (~60c ≈ 4s of audio) while the old fixed stage 2 batched up
to 300c (~13s synthesis) — the audio ran dry before chunk 2 existed. A chunk can only gap when
its synthesis outlasts the previous chunk's AUDIO; Replicate synthesizes (~43ms/char) faster
than it plays (~65ms/char), so the pacer now grows each chunk's threshold from the previous
chunk's ACTUAL length (×GrowthFactor=1.4, clamp [40, 320]): synthesis of chunk N+1 always fits
inside chunk N's playback, and sizes amortize up to the cap. Paragraph breaks still fire early.
Strict 1.5 (perfect no-gap) is impossible because sentences arrive WHOLE — uniform sentences
overshoot any threshold (the exact worst case is 2.0×); tested bound is ≤2.05× per step (worst
gap ~2s instead of 10s+). Knobs: TtsChunkPacerOptions { FirstMinChars, GrowthFactor, MaxChars }
— GrowthFactor up to ~1.5 trades smoothness for fewer provider calls; above that gaps return.
NASTV picks this up via the package (parameterless `new TtsChunkPacer()`).
