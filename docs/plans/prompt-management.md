# Prompt Management (Conversation V2)

Status: PROPOSED — awaiting approval. Written 2026-09-19 after the latency bench showed reply
length (driven by prompts) dominates end-to-end latency, and the user asked for prompts to become
first-class objects.

## Problem

Prompts exist but aren't manageable:

- The engine has ONE system prompt slot (`config.LlmSystemPrompt`), normally filled by the active
  persona. Cross-cutting policies — "be brief", "write for the ear, no markdown", "use metric
  units" — can only be used by pasting them into every persona.
- The bench proved this matters beyond taste: an 841-char reply took 128s end-to-end; a ~100-char
  reply took 8s. Brevity is a *policy* that applies to every persona, not a persona of its own.
- The user's earlier request — edit prompts by voice using the internal STT — needs prompt objects
  to attach that UI to.

## Design

### 1. `Prompt` + `PromptStore` (Core)

```csharp
public sealed class Prompt
{
    public string Name { get; set; } = "";       // unique key, e.g. "Brevity"
    public string Body { get; set; } = "";       // the prompt text
    public bool IsBuiltIn { get; set; }          // protected from delete, like personas
}
```

`PromptStore` mirrors `PersonaStore`: JSON at `~/.config/conversation/prompts.json` (0600),
reserved built-ins, `Upsert` validation ("a prompt needs a body"), `Delete` protects built-ins,
atomic save. `DefaultName`-style reserved names are not needed — prompts are inert until
referenced.

Stock prompts (seeded on first run):

| Name | Body (gist) |
|---|---|
| Brevity | Answer in at most two short sentences. No preamble, no offers to help further. |
| Speakable | Write for the ear: no markdown, no lists, no URLs; spell out numbers and symbols. |
| Curious | Ask one short follow-up question when the request is ambiguous. |

### 2. Personas compose prompts (the bundle grows one field)

`Persona` gains `List<string> PromptNames`. The persona's own `SystemPrompt` stays — it is the
persona's base text (who they ARE); referenced prompts are policies (how they behave).

Composition (`PersonaBinding.Compose`, replacing the raw `SystemPrompt` assignment):

```
[persona.SystemPrompt] + "\n\n" + [prompt.Body for each PromptNames entry, in list order]
```

- Stable order = stable prompt prefix (prompt-cache friendliness; NASTV's recency-anchor lesson).
- Missing references are skipped with a warning (a deleted prompt must not break a persona).
- Engine change: NONE. `ChatOptions.SystemPrompt` keeps receiving one string.

### 3. Settings UI

- New "Prompts" section: list, add, edit, delete (built-ins deletable? No — same rule as
  personas), plus the voice-dictation button on every prompt textarea (VoiceSession → text).
- Persona editor gains a prompt checklist with order (order = composition order).

### 4. API

`GET /api/prompts`, `POST /api/prompts` (upsert/delete, same shape as personas). Personas API
passes `promptNames` through.

## Out of scope / notes

- Engine-internal prompts (extractor, tool routing — `PromptConstants`/`PromptBuilders`) are NOT
  user-managed; they are machinery, not policy. Keep them in code.
- NASTV adoption: NASTV personas live in the AI plugin's config store. When NASTV adopts Core
  (plan §4.5), persona→prompt composition comes with the package; mapping plugin persona prompts
  onto the bundle is part of that phase, not this one.
- No per-turn prompt overrides (tray "mood switch") yet — persona selection already covers it.

## Phases

1. Core: `Prompt`, `PromptStore`, `PersonaBinding.Compose`, persona field + validation; tests
   (store CRUD, reserved built-ins, composition order + stability, missing-reference tolerance).
2. Settings: API endpoints + Prompts section + persona prompt checklist.
3. Polish: dictate-into-prompt button (reuses `VoiceSession`), overlay hint.

## Why not "the brevity prompt is just a setting"?

It was a test hack: I copied config.json with a hand-written `llmSystemPrompt`. That only works
when the user is a developer with a JSON editor — and it clobbers the persona's prompt instead of
composing with it.
