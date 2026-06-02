# Voice Cloning Attempts

Tracking local and remote voice cloning approaches for the benow-conversation TTS pipeline.

## Hardware

- **Machine**: Lenovo Legion Go (Z1 Extreme)
- **CPU**: AMD Ryzen Z1 Extreme (8c/16t)
- **GPU**: AMD Radeon 780M (RDNA3, gfx1103), 3 GB VRAM
- **RAM**: 11 GB system + 11 GB swap
- **OS**: Ubuntu 24.04 (kernel 7.0)

## Attempts

### 1. CosyVoice-300M (local)

**Date**: 2026-05-31  
**Status**: ❌ Failed  
**Issue**: ROCm PyTorch kernels incompatible with Radeon 780M (gfx1103). GPU inference causes "invalid device function" HIP errors and GPU hangs that crash the Wayland display session. CPU mode too slow (>20s per short phrase).  
**Details**:
- PyTorch 2.3.1+rocm6.0, 2.4.1+rocm6.0, and 2.6.0+rocm6.1 all tested
- `torch.linalg.vector_norm` HIP kernel missing for gfx1103
- `HSA_OVERRIDE_GFX_VERSION=11.0.0` allows basic GPU ops but complex LLM inference causes GPU hang
- Missing `matcha` dependency required manual install from CosyVoice submodule
- Missing `llm.pt` in CosyVoice-300M-Instruct model (JIT-only)
- CosyVoice-300M base model used instead (has llm.pt)
- CPU mode: 8.9s load, inference times out (>2 min for "Hello world")
- Installed packages: pytorch-triton-rocm 3.2.0 causes CUDA_VISIBLE_DEVICES override issues

**Models downloaded**: CosyVoice-300M-Instruct (5.4 GB), CosyVoice-300M (5.9 GB), CosyVoice2-0.5B (6.5 GB)

### 2. Kokoro-82M (local)

**Date**: 2026-05-31  
**Status**: ✅ Working (CPU only)  
**Details**:
- Fast CPU synthesis (~2.2s per sentence)
- 12 pre-built voices (af_heart, af_nova, etc.)
- No voice cloning capability
- Running on port 50001
- Server: `scripts/kokoro-server.py`
- Must run with `CUDA_VISIBLE_DEVICES=""` to avoid ROCm GPU errors
- PyTorch 2.6.0+rocm6.1, transformers 4.51.3

### 3. F5-TTS (local)

**Date**: 2026-05-31  
**Status**: ⚠️ Works CPU-only, too slow for real-time  
**Details**:
- Installed in `.venv-f5tts` (Python 3.12, PyTorch 2.12.0 CPU)
- Model loads in ~3s, auto-transcribes reference audio
- GPU: ROCm 6.2 PyTorch wheels unavailable (index empty). F5-TTS docs recommend 2.5.1+rocm6.2 for RDNA3 but wheels don't exist.
- CPU inference: 3+ minutes per sentence — too slow for conversation TTS
- CLI: `f5-tts_infer-cli --model F5TTS_v1_Base --ref_audio <path> --gen_text "<text>"`
- Creates temp directory at resolution; output .wav if completes

### 4. GPT-SoVITS-CPUFast (local) — NOT ATTEMPTED

**Why**: Same 300M+ parameter class as CosyVoice/F5-TTS. CPU-Fast fork claims 45% improvement but from an unusable baseline — unlikely to reach sub-10s inference on this CPU. Skipped.
**Reference**: https://github.com/baicai-1145/GPT-SoVITS-CPUFast

## Remote APIs — None Found

| Provider | Voice Cloning | NSFW Policy |
|---|---|---|
| OpenRouter | No TTS voice cloning models | — |
| ElevenLabs | Yes (cloud) | Strict content policy |
| Play.ht | Yes (cloud) | Strict content policy |
| Resemble AI | Yes (cloud) | Strict content policy |

## Pipeline Stability Fixes (2026-05-31)

After user reported dropouts, incomplete phrases, persona misallocations, and missing TTS text, the following fixes were applied:

### Root Causes Identified

1. **ModifierInjector** — LLM modifies text but only verified bracket count, not content
2. **CharacterNormalizer** — only checked if output < 30% of input, allowed 40-50% drops
3. **CharacterParser.StripStrayMarkers** — `AnyBracketRegex().Replace(text, "")` stripped ALL bracket content
4. **ParallelTtsPlayer** — silently skipped failed TTS segments with no retry
5. **ModifierTimeoutMs: 3000** — too short for LLM with long system prompt

### Fixes Applied

| File | Change | Impact |
|---|---|---|
| `ModifierInjector.cs` | Text integrity validation — strips modifier tags, checks alpha-numeric ratio (0.60–2.5) | Prevents LLM from silently dropping text |
| `CharacterNormalizer.cs` | Alpha-numeric ratio check (≥50%) in addition to length check | Catches 40-70% content loss |
| `CharacterParser.cs` | `StripStrayMarkers` now only removes matched character markers, not all bracket content | Prevents text inside malformed brackets from being dropped |
| `ParallelTtsPlayer.cs` | Retry logic: up to 2 retries with exponential backoff (500ms, 1000ms) on TTS failure | Recovers from transient API errors |
| `ProxyService.cs` | Text coverage: warns at <85% (was <50%), errors at <50% | Earlier detection of text loss |
| `appsettings.json` | `ModifierTimeoutMs`: 3000 → 8000ms | Reduces modifier timeout failures |

### Stability Tips

1. **Prompt quality matters more than model.** The modifier/normalizer system prompts are long — shorter, clearer prompts reduce LLM hallucination
2. **One slow model > racing fast ones.** The 3-model race with 8s timeout means the fastest-but-sloppier model wins. Consider using a single reliable model
3. **Check logs for `ValidateTextIntegrity` warnings.** When modifier output fails validation, the original text is used instead — look for the warning log
4. **Text coverage at 85-95% is normal.** Modifier tags and bracket markers account for ~5-15% of character difference
5. **Persona persistence.** Character→persona assignments survive restarts (saved to `appsettings.json`). Reset with `/reset` in chat

## Conclusion (2026-05-31)

**Voice cloning is not currently feasible on this hardware.** All local voice cloning models (CosyVoice 300M, F5-TTS, GPT-SoVITS) are 300M-500M parameter diffusion/LLM models requiring GPU for practical inference speeds. The Radeon 780M (gfx1103) consumer GPU is not supported by ROCm PyTorch wheels from PyPI — they're compiled only for data center GPUs (MI200/MI300 series).

**What works**: Kokoro-82M (CPU, 12 pre-built voices, ~2s per sentence, no cloning). Running on port 50001.

**What would make voice cloning viable**:
- NVIDIA GPU with CUDA support (RTX 3060+, 8 GB+ VRAM)
- Remote API with NSFW-tolerant policy (none found as of 2026-05)
- PyTorch ROCm wheels compiled with `AMDGPU_TARGETS=gfx1103` (requires source build, ~hours)

## Key Findings

1. **ROCm + Consumer GPUs**: PyTorch ROCm wheels from PyPI are compiled for data center GPUs (MI200/MI300). Consumer RDNA3 GPUs (Radeon 780M) require specific ROCm versions and may need `HSA_OVERRIDE_GFX_VERSION` workarounds.
2. **Execstack**: ROCm .so files often have `RWE` GNU_STACK flags that must be cleared with `patchelf --clear-execstack` on newer Ubuntu kernels.
3. **CUDA_VISIBLE_DEVICES=""**: Essential for CPU-only PyTorch models when ROCm is installed, otherwise models auto-detect the GPU and crash.
4. **Reference audio length**: CosyVoice limits prompt audio to ≤30 seconds. Bootstrap scripts generated 25-60s clips — need trimming.
