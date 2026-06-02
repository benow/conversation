#!/usr/bin/env python3
"""Kokoro FastAPI server for text-to-speech.

POST /v1/tts  { "text": "...", "voice": "af_heart", "speed": 1.0 }
  → returns WAV audio stream
"""
import argparse, io, wave, logging, time, sys
from fastapi import FastAPI, HTTPException
from pydantic import BaseModel
from fastapi.responses import StreamingResponse

logging.basicConfig(level=logging.INFO, format="[KOKORO] %(message)s")
log = logging.getLogger("kokoro-server")

app = FastAPI(title="Kokoro TTS Server")
pipeline = None

VOICES = [
    "af_alloy", "af_aoede", "af_bella", "af_heart", "af_jessica",
    "af_kore", "af_nicole", "af_nova", "af_river", "af_sarah", "af_sky",
    "am_onyx"
]

class TtsRequest(BaseModel):
    text: str
    voice: str = "af_heart"
    speed: float = 1.0

def load_model():
    global pipeline
    from kokoro import KPipeline
    log.info("Loading Kokoro-82M...")
    start = time.time()
    pipeline = KPipeline(lang_code='a')
    log.info(f"Model loaded in {time.time()-start:.1f}s")
    log.info("Pre-loading voices...")
    for v in VOICES:
        pipeline.load_single_voice(v)
    log.info(f"{len(VOICES)} voices loaded")

@app.post("/v1/tts")
async def synthesize(req: TtsRequest):
    if pipeline is None:
        raise HTTPException(503, "Model not loaded yet")
    if req.voice not in VOICES:
        raise HTTPException(400, f"Unknown voice: {req.voice}. Available: {VOICES}")

    try:
        start = time.time()
        generator = pipeline(req.text, voice=req.voice, speed=req.speed)

        full_audio = None
        total_samples = 0
        for gs, ps, audio in generator:
            if full_audio is None:
                full_audio = audio
            else:
                import numpy as np
                full_audio = np.concatenate([full_audio, audio])
            total_samples += len(audio)

        duration = total_samples / 24000
        # Convert to numpy if torch tensor (CPU mode)
        if hasattr(full_audio, 'cpu'):
            full_audio = full_audio.detach().cpu().numpy()
        elif not hasattr(full_audio, 'astype'):
            import numpy as np
            full_audio = np.asarray(full_audio)
        buf = io.BytesIO()
        with wave.open(buf, "wb") as wf:
            wf.setnchannels(1)
            wf.setsampwidth(2)
            wf.setframerate(24000)
            wf.writeframes((full_audio * 32767).astype('int16').tobytes())
        buf.seek(0)

        elapsed = time.time() - start
        log.info(f"V:{req.voice} T:{len(req.text)}c → {duration:.1f}s RTF:{elapsed/duration:.1f}x")
        return StreamingResponse(buf, media_type="audio/wav")

    except Exception as e:
        log.error(f"Synthesis failed: {e}")
        raise HTTPException(500, str(e))

@app.get("/health")
async def health():
    return {"status": "ready" if pipeline else "loading"}

if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, default=50001)
    args = parser.parse_args()

    load_model()

    import uvicorn
    log.info(f"Serving on port {args.port}")
    uvicorn.run(app, host="0.0.0.0", port=args.port, log_level="info")
