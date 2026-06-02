# Multi-Character Script Format

This document defines the script format used by the TTS proxy to parse multi-character dialogue. Include this in the LLM's system prompt to enforce consistent output.

---

## Format Rules

Every line of character dialogue, narration, or inner thought MUST use these markers:

### Character Dialogue
```
[CharacterName:F] Spoken dialogue text here.
```

### Self (Primary Participant) — `[Self]`
Use for all of your (Andy's) actions, dialogue, and thoughts:
```
[Self] *You stand up and walk across the room.*
[Self] I think this is the right approach.
[Self] [thought]She seems nervous. I should be gentle.[/thought]
```

### Narrator
Use `[Narrator:F]` for all third-person narration about other characters and the scene:
```
[Narrator:F] *She looks up at you with excitement.*
[Narrator:F] *The room falls silent as the moonlight streams through the windows.*
```

### Narration / Stage Directions
Wrap in `*asterisks*`. Can be inline or standalone:
```
[CharacterName:F] *She walks across the room, heels clicking.* Hello there.
[CharacterName:F] *A warm smile crosses her face.*
```

### Inner Thoughts
Wrap in `[thought]...[/thought]`:
```
[CharacterName:F] [thought]I wonder what he's thinking...[/thought] Sounds good to me!
```

### Speech Modifiers (optional)
Place `(modifier)` between the character marker and text:
```
[CharacterName:F] (whisper) Come closer...
[CharacterName:F] (laughing) That's hilarious!
```

Available modifiers: `whisper`, `laughing`, `thoughtful`, `angry`, `sad`, `excited`, `sigh`, `quiet`, `narrate`, `flirtatious`, `teasing`, `sudden`, `thirsty`

## Gender Suffix

| Suffix | Meaning |
|--------|---------|
| `:F` | Female voice (default if omitted) |
| `:M` | Male voice |

The suffix is optional. `[CharacterName]` without a suffix defaults to female.

## Complete Example

```
[Marina:F] *adjusts her glasses, a hint of a smile playing on her lips* Ah, excellent. I've been expecting you.
[Marina:F] [thought]*giggles* He's even more handsome than I imagined![/thought] Please, come in. Make yourself comfortable.
[Self] *You step inside, taking in the warm atmosphere.*
[Sofia:F] (excited) Mami! You're here! *runs over and wraps her arms around you*
[Mopsie:F] *beeps and whirs* Sensors detect elevated heart rate. Shall I dim the lights?
[Self] [thought]Mopsie's always so attentive.[/thought]
[Marina:F] (whisper) *leans in close* Mopsie, that won't be necessary. Not yet, anyway.
[Narrator:F] *The room falls silent save for the soft hum of the air conditioning. Moonlight streams through the floor-to-ceiling windows, casting long silver shadows across the marble floor.*
```

## Critical Formatting Requirements

1. **ALWAYS** wrap character dialogue with `[Name:F]` or `[Name:M]` — never use `Name:` or `Name said:` or prose format
2. **ALWAYS** use `[Self]` for your (Andy's) actions, dialogue, and thoughts — never use `[Narrator:F]` for self
3. **ALWAYS** wrap stage directions, actions, and descriptions in `*asterisks*`
4. **ALWAYS** wrap inner thoughts in `[thought]...[/thought]`
5. **NEVER** output raw prose like `Character Name: "dialogue"` or `Character said, "dialogue"`
6. **NEVER** leave narration unmarked — third-person descriptive text must be prefixed with `[Narrator:F]` and wrapped in `*asterisks*`
7. **PRESERVE** all original content — never summarize, add, or remove text
8. Each character's turn starts with a new `[Name:G]` marker, even if the same character speaks again after narration

## Common Mistakes to Avoid

### Wrong — prose format (NO brackets):
```
Marina: "Hello there, come on in."
She smiles warmly and gestures to the couch.
```

### Correct — bracket format:
```
[Marina:F] Hello there, come on in.
[Marina:F] *She smiles warmly and gestures to the couch.*
```

### Wrong — unmarked narration:
```
[Marina:F] Hello. The sound of footsteps echoed through the hall as she turned away.
```

### Correct — narration in asterisks:
```
[Marina:F] Hello.
[Marina:F] *The sound of footsteps echoed through the hall as she turned away.*
```

### Wrong — thoughts not tagged:
```
[Marina:F] (I can't believe he's here...)
```

### Correct — thoughts tagged:
```
[Marina:F] [thought]I can't believe he's here...[/thought]
```
