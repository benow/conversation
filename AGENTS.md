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
