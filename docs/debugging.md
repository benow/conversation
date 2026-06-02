# Debugging Guide

## Quick Diagnostics

### Is TTS dropping text?
```bash
# Watch logs for dropped/missing text
tail -f logs/benow-conversation.log | grep -E "coverage|FAILED|skipped|Skipped|text integrity|Retry"
```

Key log patterns:
| Pattern | Meaning |
|---|---|
| `Low text coverage (<85%)` | Parser lost 15%+ of input text |
| `CRITICAL text coverage (<50%)` | Parser lost majority of text — severe |
| `failed text integrity check` | Modifier LLM response rejected |
| `all retries exhausted.*SKIPPED` | TTS segment failed after 3 attempts |
| `Suspiciously small audio` | TTS returned error JSON instead of audio |

### Is modifier injection losing text?
```bash
# Check modifier validation failures
grep "text integrity check" logs/benow-conversation.log | tail -20

# Check which model responded
grep "Modifier injection succeeded\|modifier injection" logs/benow-conversation.log | tail -10
```

### Is persona allocation correct?
```bash
# See which characters got which voices
grep "Character→Voice mapping" logs/benow-conversation.log | tail -5

# Reset allocations if wrong
# In chat, type: /reset
```

### Is TTS failing silently?
```bash
# Check for failed synthesis
grep "failed for" logs/benow-conversation.log | tail -20

# Check for retries (indicates transient failures)
grep "attempt " logs/benow-conversation.log | tail -20
```

## Pipeline Flow

```
User text
  → [STT cleanup: Llama 3.1 8B] (if Transformer != "none")
  → [LLM generation: Llama 3.3 70B]
  → IsMultiCharacter? ──No──→ SentenceSplitter → TTS → ffplay
  │                     
  └──Yes──→ IsMultiCharacterProse?
              │
              ├──Yes──→ CharacterNormalizer (LLM: prose→[Name:F] format)
              │              │
              └──No──────────┘
                            ↓
              ModifierInjector (LLM: add (modifier) tags)
                            ↓
              CharacterParser.Parse (split into segments)
                            ↓
              PersonaAllocator (assign voices)
                            ↓
              ParallelTtsPlayer (synthesize + play)
```

## Debugging Specific Issues

### Text appears in response but is not spoken
1. Check if text is in `[thought]` tags — thoughts use a different persona
2. Check if text is in `*narration*` — asterisk-wrapped text is narration
3. Check `text coverage` log — how much of the original text made it to segments?
4. Look for `"modifier"` failures that could drop text before parsing

### Wrong voice for a character
1. Check if the character name in the LLM output matches config: `CharacterAssignments`
2. Check gender detection: `[Name:F]` → `ParseNameAndGender`
3. Reset with `/reset` in chat to clear persisted assignments
4. Set explicit assignments in config:
   ```json
   "CharacterAssignments": {
     "Marina": "female-2",
     "UserName": "female-8"
   }
   ```

### Audio cuts off mid-sentence
1. Check for `"Stdin pipe broken"` — ffplay crashed mid-stream
2. Check `PersistentPipeline` config — if false, per-sentence ffplay may cut
3. Check `"suspiciously small audio"` — TTS returned error
4. OpenRouter TTS has a character limit — very long instructions can cause truncation

### Modifier prompt quality
The modifier prompt is in `appsettings.json` at `MultiCharacter.ModifierSystemPrompt`.
- **Too long** → LLM might truncate response
- **Too short** → LLM misses nuance
- Current prompt: ~900 chars. If quality drops, add back modifier descriptions.

## Testing

### Runtime Regression Test Enforcement

The system automatically emits regression test fixtures when text coverage drops below 50%.
These are `.pending` files in `tests/benow-conversation.Tests/fixtures/`.

**Enforcement**: Set `MultiCharacter.EnforceRegressionTests: true` in config.
- On daemon startup, if `.pending` files exist, the daemon fails to start with instructions.
- In development (`appsettings.Development.json`), this is `true` by default.
- In production (`appsettings.json`), this is `false` — only a warning is logged.

**To resolve a `.pending` file**:
1. Review the fixture contents — the `input` and `expectedSegments` fields
2. Verify the expected output is correct
3. `mv drop-YYYYMMDD-HHMMSS.pending drop-YYYYMMDD-HHMMSS.json`
4. Run `dotnet test --filter "GoldenMaster"` to verify it passes
5. Commit the `.json` file

**To bypass enforcement**: Set `EnforceRegressionTests: false` in `appsettings.Development.json`

### Running tests
```bash
dotnet test
dotnet test --filter "GoldenMaster"  # Regression tests only
```

### Test structure
```
tests/benow-conversation.Tests/
├── GoldenMasterParserTests.cs   # Known input→output regression tests
├── CharacterParserTests.cs      # Parser unit tests
├── ModifierInjectorTests.cs     # Modifier injection behavior
├── PersonaAllocatorTests.cs     # Voice allocation
├── ParallelTtsPlayerTests.cs    # TTS playback pipeline
├── SentenceSplitterTests.cs     # Text chunking
└── fixtures/                    # Golden master JSON fixtures
```

### Writing a Golden Master test
1. Create a JSON file in `tests/benow-conversation.Tests/fixtures/`
2. Format:
   ```json
   {
     "name": "bug-name-description",
     "description": "What this test covers",
     "input": "[Alice]Hello world. This is test text.",
     "expectedCoverage": { "minPercent": 80 },
     "expectedSegments": [
       { "characterName": "Alice", "gender": "F", "isNarration": false,
         "isThought": false, "spokenTextContains": "Hello world" }
     ]
   }
   ```
3. Run: `dotnet test --filter "GoldenMasterParserTests"`

### Writing a Modifier Injector test
See `tests/benow-conversation.Tests/ModifierInjectorTests.cs` for patterns:
- `SuccessfulInjection_CorrectRequestFormat` — validates request format and response parsing
- `HttpFailure_ReturnsOriginalText` — validates graceful degradation
- Use `CapturingMockHandler` to inspect HTTP requests

## Configuration

Key settings for debugging (in `appsettings.json`):

```json
{
  "Proxy": {
    "LogBodies": true,           // Log full request/response bodies
    "ChunkedTts": true,          // Sentence-by-sentence TTS
    "MinSentenceLength": 20      // Minimum chars before splitting
  },
  "MultiCharacter": {
    "AutoInjectModifiers": true, // Enable modifier LLM pass
    "AutoNormalize": true,       // Enable prose→script LLM pass
    "ModifierTimeoutMs": 8000,   // Timeout for modifier LLM
    "NormalizerTimeoutMs": 5000  // Timeout for normalizer LLM
  }
}
```
