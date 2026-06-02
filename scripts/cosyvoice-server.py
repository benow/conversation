#!/usr/bin/env python3
"""CosyVoice FastAPI server for TTS requests.

POST /v1/tts  { "text": "...", "voice_ref": "voices/female-1.wav", "instructions": "..." }
  → returns WAV audio stream
"""
import argparse, io, wave, logging, time, sys, os, gc
from pathlib import Path
from fastapi import FastAPI, HTTPException
from pydantic import BaseModel
from fastapi.responses import StreamingResponse

logging.basicConfig(level=logging.INFO, format="[COSY] %(message)s")
log = logging.getLogger("cosyvoice-server")

app = FastAPI(title="CosyVoice TTS Server")
cosyvoice = None
model_dir = None
device_type = "cpu"

class TtsRequest(BaseModel):
    text: str
    voice_ref: str  # path to reference audio file (relative to project root or absolute)
    instructions: str | None = None  # style/emotion/pace direction

def load_model(model_dir_path: str, force_cpu: bool = True):
    global cosyvoice, device_type
    sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    from cosyvoice.cli.cosyvoice import CosyVoice, CosyVoice2

    # GPU is unsafe on consumer RDNA3 GPUs (causes GPU hangs / display crashes).
    # Force CPU unless explicitly overridden with --gpu flag.
    device_type = "cpu"

    if not force_cpu:
        try:
            import torch
            if torch.cuda.is_available():
                device_type = "cuda"
                log.info(f"GPU mode requested: {torch.cuda.get_device_name(0)}")
        except Exception:
            pass

    log.info(f"Loading model in {device_type} mode (force_cpu={force_cpu})...")

    for cls_name, cls in [("CosyVoice2", CosyVoice2), ("CosyVoice", CosyVoice)]:
        try:
            t0 = time.time()
            kwargs = {"load_jit": False, "load_trt": False, "fp16": device_type == "cuda"}
            instance = cls(model_dir_path, **kwargs)
            elapsed = time.time() - t0
            log.info(f"{cls_name} loaded in {elapsed:.1f}s [{device_type}]")
            cosyvoice = instance
            return
        except Exception as e:
            log.info(f"  {cls_name}: {e}")
            gc.collect()

    raise RuntimeError("Failed to load CosyVoice model")

@app.post("/v1/tts")
async def synthesize(req: TtsRequest):
    if cosyvoice is None:
        raise HTTPException(503, "Model not loaded yet")

    ref_path = Path(req.voice_ref)
    if not ref_path.is_absolute():
        ref_path = Path(model_dir).parent.parent / req.voice_ref
    if not ref_path.exists():
        raise HTTPException(400, f"Voice reference file not found: {ref_path}")

    try:
        start = time.time()
        result = next(cosyvoice.inference_zero_shot(
            req.text,
            str(req.instructions or ""),
            str(ref_path),
            stream=False
        ))
        audio = result["tts_speech"]
        # Move GPU tensor to CPU
        if hasattr(audio, "cpu"):
            audio = audio.detach().cpu()
        duration = time.time() - start

        buf = io.BytesIO()
        with wave.open(buf, "wb") as wf:
            wf.setnchannels(1)
            wf.setsampwidth(2)
            wf.setframerate(22050)
            wf.writeframes(audio.numpy().tobytes())
        buf.seek(0)

        log.info(f"Synthesized {len(req.text)} chars → {len(audio)/22050:.1f}s audio in {duration:.1f}s (RTF {duration * 22050 / len(audio):.2f})")
        return StreamingResponse(buf, media_type="audio/wav")

    except Exception as e:
        log.error(f"Synthesis failed: {e}")
        raise HTTPException(500, str(e))

@app.get("/health")
async def health():
    return {
        "status": "ready" if cosyvoice else "loading",
        "model": model_dir or "not set",
        "device": device_type,
        "fp16": getattr(cosyvoice, "fp16", False) if cosyvoice else False
    }

if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--model-dir", required=True, help="Path to CosyVoice model directory")
    parser.add_argument("--port", type=int, default=50000)
    parser.add_argument("--gpu", action="store_true", help="Enable GPU mode (WARNING: may hang/crash on consumer RDNA3 GPUs)")
    args = parser.parse_args()
    model_dir = args.model_dir

    load_model(args.model_dir, force_cpu=not args.gpu)

    log.info(f"Starting server on port {args.port} [{device_type}]")
    import uvicorn
    uvicorn.run(app, host="0.0.0.0", port=args.port, log_level="info")
