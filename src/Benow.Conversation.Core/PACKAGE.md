# Benow.Conversation.Core

The shared conversation engine (Conversation V2): voice pipeline (VAD with pre-roll, segment
dispatch, progressive TTS pacing), AI provider clients (STT / chat / TTS), the extractor-speaker
LLM strategy, capture and playback primitives, persona bundles, and the conversation engine that
ties them together.

Consumed by the [Conversation desktop app](https://github.com/benow/conversation) and by NASTV
(plan §4.5). Dependency ceiling: only `Microsoft.Extensions.Logging.Abstractions` — no ASP.NET,
no DB drivers — so the assembly stays loadable inside a NASTV plugin (deps.json-free probing).

Key types: `ConversationEngine`, `ConversationFactory`, `VoiceVAD`, `SpeechQueue`,
`TtsChunkPacer`, `TurnTimeline`, `PersonaStore`/`PersonaBinding`, `ConfigStore`.
