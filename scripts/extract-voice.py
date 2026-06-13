#!/usr/bin/env python3
"""
Voice extraction from video files — isolates clean speech segments for voice cloning.

Pipeline:
  1. ffmpeg extracts audio from MP4 to 16kHz mono WAV
  2. webrtcvad VAD splits into speech/silence frames
  3. Quality scoring per speech segment (SNR, spectral centroid, clipping, level, duration)
  4. Greedy select top-ranked segments to reach target duration
  5. Mild noise reduction via sox noisered (profile from all silence gaps)
  6. Normalize to -3dB peak, trim silence → output WAV

Usage:
    .venv-tts/bin/python scripts/extract-voice.py --input video.mp4 --name "person"
    .venv-tts/bin/python scripts/extract-voice.py --input video.mp4 --start 00:05 --end 00:30
"""

import argparse
import logging
import subprocess
import tempfile
import os
import sys
from pathlib import Path

import numpy as np
import webrtcvad

logging.basicConfig(level=logging.INFO, format="[extract-voice] %(message)s")
log = logging.getLogger("extract-voice")

# ---------------------------------------------------------------------------
# Constants
# ---------------------------------------------------------------------------
SAMPLE_RATE = 16000
VAD_FRAME_MS = 30
VAD_FRAME_SAMPLES = int(SAMPLE_RATE * VAD_FRAME_MS / 1000)  # 480 samples
VAD_MODE = 2
MERGE_GAP_MS = 300
MIN_SEGMENT_S = 0.5
NOISE_REDUCE_AMOUNT = 0.18
TARGET_RMS = -16.0
PEAK_CEILING = -1.0
DEFAULT_TARGET_S = 12

# ---------------------------------------------------------------------------
# Stage 1: Audio extraction
# ---------------------------------------------------------------------------

def extract_audio(input_path: str, start: str | None, end: str | None) -> tuple[np.ndarray, int]:
    """Extract 16kHz mono PCM from a video file via ffmpeg. Returns (samples, sr)."""
    cmd = ["ffmpeg", "-v", "error", "-i", input_path]
    if start:
        cmd += ["-ss", start]
    if end:
        cmd += ["-to", end]
    cmd += ["-ac", "1", "-ar", str(SAMPLE_RATE), "-f", "s16le", "pipe:1"]

    log.info("Extracting audio: %s", " ".join(cmd))
    proc = subprocess.run(cmd, capture_output=True, timeout=120)
    if proc.returncode != 0:
        stderr = proc.stderr.decode("utf-8", errors="replace").strip()
        raise RuntimeError(f"ffmpeg extraction failed: {stderr}" if stderr else f"ffmpeg exit code {proc.returncode}")

    if not proc.stdout:
        raise RuntimeError("ffmpeg produced no audio output")

    samples = np.frombuffer(proc.stdout, dtype=np.int16).astype(np.float32) / 32768.0
    log.info("Extracted %.1fs of audio (%d samples)", len(samples) / SAMPLE_RATE, len(samples))
    return samples, SAMPLE_RATE


# ---------------------------------------------------------------------------
# Stage 2: Voice Activity Detection
# ---------------------------------------------------------------------------

def run_vad(samples: np.ndarray) -> np.ndarray:
    """Run webrtcvad on 16kHz mono audio. Returns boolean array (one per frame)."""
    vad = webrtcvad.Vad(VAD_MODE)
    # Pad to multiple of frame size
    num_frames = len(samples) // VAD_FRAME_SAMPLES
    if num_frames == 0:
        raise RuntimeError(f"Audio too short for VAD: {len(samples)} samples < {VAD_FRAME_SAMPLES}")

    trimmed = samples[:num_frames * VAD_FRAME_SAMPLES]
    # Convert to int16 PCM bytes
    pcm = (trimmed * 32767).astype(np.int16)

    decisions = np.zeros(num_frames, dtype=bool)
    for i in range(num_frames):
        frame = pcm[i * VAD_FRAME_SAMPLES:(i + 1) * VAD_FRAME_SAMPLES].tobytes()
        try:
            decisions[i] = vad.is_speech(frame, SAMPLE_RATE)
        except Exception:
            decisions[i] = False

    speech_pct = 100 * decisions.sum() / len(decisions)
    log.info("VAD: %d frames, %.1f%% speech (%d/%d)", num_frames, speech_pct, decisions.sum(), len(decisions))
    return decisions


def build_segments(decisions: np.ndarray) -> list[tuple[int, int]]:
    """Convert VAD decisions to (start_frame, end_frame) segments, merging close gaps."""
    segments = []
    in_speech = False
    start = 0

    for i, is_speech in enumerate(decisions):
        if is_speech and not in_speech:
            start = i
            in_speech = True
        elif not is_speech and in_speech:
            segments.append((start, i))
            in_speech = False

    if in_speech:
        segments.append((start, len(decisions)))

    # Merge segments separated by short silence
    merged = []
    gap_frames = max(1, MERGE_GAP_MS // VAD_FRAME_MS)
    for seg in segments:
        if merged and seg[0] - merged[-1][1] <= gap_frames:
            merged[-1] = (merged[-1][0], seg[1])
        else:
            merged.append(seg)

    merged_count = len(merged)

    # Filter out very short segments
    min_frames = max(1, int(MIN_SEGMENT_S * 1000 / VAD_FRAME_MS))
    filtered = [s for s in merged if s[1] - s[0] >= min_frames]

    log.info("Segments: %d raw → %d merged → %d filtered (≥%.1fs)",
             len(segments), merged_count, len(filtered), MIN_SEGMENT_S)
    return filtered


# ---------------------------------------------------------------------------
# Stage 3: Quality scoring
# ---------------------------------------------------------------------------

def score_segment(
    samples: np.ndarray,
    seg: tuple[int, int],
    global_silence_rms: float,
    frame_to_sample: float,
) -> dict:
    """Score a speech segment on multiple quality dimensions."""
    frame_start, frame_end = seg
    s0 = int(frame_start * frame_to_sample)
    s1 = int(frame_end * frame_to_sample)
    s0 = max(0, s0)
    s1 = min(len(samples), s1)
    speech = samples[s0:s1]

    if len(speech) == 0:
        return {"score": -999, "snr": 0, "centroid": 0, "clip_ratio": 0, "rms_db": -99, "duration": 0}

    speech_rms = float(np.sqrt(np.mean(speech ** 2)))
    duration = len(speech) / SAMPLE_RATE

    # SNR
    noise_rms = max(global_silence_rms, 1e-10)
    snr = 20 * np.log10(max(speech_rms, 1e-10) / noise_rms)
    snr_score = min(snr / 30.0, 1.0)

    # Spectral centroid (FFT)
    n_fft = min(2048, len(speech))
    fft = np.abs(np.fft.rfft(speech, n=n_fft))
    freqs = np.fft.rfftfreq(n_fft, 1.0 / SAMPLE_RATE)
    centroid = float(np.sum(freqs * fft) / max(np.sum(fft), 1e-10))
    if centroid < 200:
        centroid_score = centroid / 200.0
    elif centroid <= 4000:
        centroid_score = 1.0
    else:
        centroid_score = max(0.0, 1.0 - (centroid - 4000) / 4000.0)

    # Clipping
    peak = max(float(np.max(np.abs(speech))), 1e-10)
    clip_ratio = float(np.sum(np.abs(speech) >= 0.95 * peak) / len(speech))
    clip_penalty = clip_ratio * 2.0

    # RMS level
    rms_db = 20 * np.log10(max(speech_rms, 1e-10))
    if -25 <= rms_db <= -10:
        level_score = 1.0
    elif rms_db > -10:
        level_score = max(0.0, 1.0 - (rms_db + 10) / 10.0)
    else:
        level_score = max(0.0, 1.0 - (-25 - rms_db) / 25.0)

    # Duration bonus (prefer >= 3s)
    duration_bonus = min(duration / 3.0, 1.0) * 0.15

    total = 0.35 * snr_score + 0.2 * centroid_score + 0.25 * level_score + duration_bonus - clip_penalty

    return {
        "score": round(total, 4),
        "snr": round(snr, 1),
        "centroid": round(centroid, 0),
        "clip_ratio": round(clip_ratio, 4),
        "rms_db": round(rms_db, 1),
        "duration": round(duration, 2),
        "frame_start": frame_start,
        "frame_end": frame_end,
    }


def compute_global_silence_rms(samples: np.ndarray, decisions: np.ndarray) -> float:
    """Compute RMS of all silence frames for global noise floor estimation."""
    frame_to_sample = len(samples) / len(decisions) if len(decisions) > 0 else VAD_FRAME_SAMPLES
    silence_samples = []
    for i, is_speech in enumerate(decisions):
        if not is_speech:
            s0 = int(i * frame_to_sample)
            s1 = int((i + 1) * frame_to_sample)
            silence_samples.append(samples[s0:s1])

    if not silence_samples:
        # Entire file is speech — use lowest 5% energy as noise estimate
        frame_energy = np.array([np.mean(samples[i * VAD_FRAME_SAMPLES:(i + 1) * VAD_FRAME_SAMPLES] ** 2)
                                 for i in range(len(samples) // VAD_FRAME_SAMPLES)])
        threshold = np.percentile(frame_energy, 5)
        noise_rms = float(np.sqrt(threshold))
        log.info("Global silence RMS (low-energy estimate): %.6f", noise_rms)
        return noise_rms

    all_silence = np.concatenate(silence_samples)
    rms = float(np.sqrt(np.mean(all_silence ** 2)))
    log.info("Global silence RMS (from %d silence frames): %.6f", len(silence_samples), rms)
    return rms


# ---------------------------------------------------------------------------
# Stage 4: Segment selection
# ---------------------------------------------------------------------------

def select_segments(scored: list[dict], target_duration: float) -> list[dict]:
    """Greedy select top-ranked segments until target duration is met."""
    selected = []
    total = 0.0
    for seg in sorted(scored, key=lambda s: s["score"], reverse=True):
        if total >= target_duration and seg["score"] < selected[-1]["score"] * 0.5:
            break
        if total >= target_duration * 1.5:
            break
        selected.append(seg)
        total += seg["duration"]

    selected.sort(key=lambda s: s["frame_start"])
    log.info("Selected %d segments (%.1fs) from %d candidates", len(selected), total, len(scored))
    return selected


# ---------------------------------------------------------------------------
# Stage 6: Dynamic range compression (optional)
# ---------------------------------------------------------------------------

def apply_compression(samples: np.ndarray) -> np.ndarray:
    """Apply gentle speech compression via sox compand to reduce crest factor."""
    tmpdir = tempfile.mkdtemp(prefix="extract-voice-comp-")
    try:
        input_wav = os.path.join(tmpdir, "input.wav")
        output_wav = os.path.join(tmpdir, "compressed.wav")
        write_wav(input_wav, samples, SAMPLE_RATE)

        # Speech-optimized compand: fast attack (5ms), moderate release (100ms),
        # soft knee from -40 to -14 dB, 6:1 ratio, makeup gain
        subprocess.run(
            ["sox", input_wav, output_wav,
             "compand", "0.005,0.1", "6:-40,-20,-14,-6", "3", "-90", "0.1"],
            check=True, capture_output=True, timeout=30,
        )

        _, compressed = read_wav(output_wav)
        rms_before = 20 * np.log10(max(float(np.sqrt(np.mean(samples ** 2))), 1e-10))
        rms_after = 20 * np.log10(max(float(np.sqrt(np.mean(compressed ** 2))), 1e-10))
        pk_before = 20 * np.log10(max(float(np.max(np.abs(samples))), 1e-10))
        pk_after = 20 * np.log10(max(float(np.max(np.abs(compressed))), 1e-10))
        log.info("Compression: RMS %.1f → %.1f dBFS, peak %.1f → %.1f dBFS",
                 rms_before, rms_after, pk_before, pk_after)
        return compressed.astype(np.float32)

    except subprocess.CalledProcessError as e:
        stderr = e.stderr.decode("utf-8", errors="replace") if e.stderr else ""
        log.warning("sox compand failed: %s — returning unprocessed audio", stderr.strip() or str(e))
        return samples
    finally:
        import shutil
        shutil.rmtree(tmpdir, ignore_errors=True)

def apply_noise_reduction(
    samples: np.ndarray,
    decisions: np.ndarray,
    selected: list[dict],
) -> np.ndarray:
    """Apply mild noise reduction via sox noisered using silence gaps as noise profile."""
    # Collect silence audio for noise profile
    silence_chunks = []
    frame_to_sample = len(samples) / len(decisions) if len(decisions) > 0 else VAD_FRAME_SAMPLES
    for i, is_speech in enumerate(decisions):
        if not is_speech:
            s0 = int(i * frame_to_sample)
            s1 = int((i + 1) * frame_to_sample)
            chunk = samples[s0:s1]
            if len(chunk) > 0:
                silence_chunks.append(chunk)

    if len(silence_chunks) < 2:
        log.info("Skipping noise reduction: insufficient silence data")
        return samples

    silence_audio = np.concatenate(silence_chunks)
    # Concatenate selected speech segments with 100ms padding
    pad_samples = int(0.1 * SAMPLE_RATE)
    speech_chunks = []
    for seg in selected:
        s0 = int(seg["frame_start"] * frame_to_sample)
        s1 = int(seg["frame_end"] * frame_to_sample)
        s0 = max(0, s0 - pad_samples)
        s1 = min(len(samples), s1 + pad_samples)
        chunk = samples[s0:s1]
        if len(chunk) > 0:
            speech_chunks.append(chunk)

    if not speech_chunks:
        return samples

    speech_audio = np.concatenate(speech_chunks)

    # Write temp files for sox
    tmpdir = tempfile.mkdtemp(prefix="extract-voice-")
    try:
        noise_wav = os.path.join(tmpdir, "noise.wav")
        speech_wav = os.path.join(tmpdir, "speech.wav")
        cleaned_wav = os.path.join(tmpdir, "cleaned.wav")
        profile_file = os.path.join(tmpdir, "noise.profile")

        write_wav(noise_wav, silence_audio, SAMPLE_RATE)
        write_wav(speech_wav, speech_audio, SAMPLE_RATE)

        # Generate noise profile (profile-file is an effect arg, -n = null audio output)
        subprocess.run(
            ["sox", noise_wav, "-n", "noiseprof", profile_file],
            check=True, capture_output=True, timeout=30,
        )
        log.info("Noise profile captured from %.1fs of silence", len(silence_audio) / SAMPLE_RATE)

        # Apply noise reduction
        subprocess.run(
            ["sox", speech_wav, cleaned_wav,
             "noisered", profile_file, str(NOISE_REDUCE_AMOUNT)],
            check=True, capture_output=True, timeout=30,
        )
        log.info("Noise reduction applied (amount=%.2f)", NOISE_REDUCE_AMOUNT)

        # Read back the cleaned audio
        fs, cleaned = read_wav(cleaned_wav)
        if fs != SAMPLE_RATE:
            log.warning("sox changed sample rate from %d to %d", SAMPLE_RATE, fs)
        return cleaned.astype(np.float32)

    except subprocess.CalledProcessError as e:
        stderr = e.stderr.decode("utf-8", errors="replace") if e.stderr else ""
        log.warning("sox noise reduction failed: %s — returning unprocessed audio", stderr.strip() or str(e))
        return samples
    finally:
        import shutil
        shutil.rmtree(tmpdir, ignore_errors=True)


# ---------------------------------------------------------------------------
# Audio I/O helpers
# ---------------------------------------------------------------------------

def write_wav(path: str, samples: np.ndarray, sr: int) -> None:
    """Write float32 audio to a 16-bit WAV file."""
    import soundfile as sf
    sf.write(path, samples, sr, subtype="PCM_16")


def read_wav(path: str) -> tuple[int, np.ndarray]:
    """Read a WAV file, returning (sample_rate, float32 samples)."""
    import soundfile as sf
    data, sr = sf.read(path, dtype="float32")
    if data.ndim > 1:
        data = data.mean(axis=1)  # convert to mono
    return sr, data


# ---------------------------------------------------------------------------
# Main pipeline
# ---------------------------------------------------------------------------

def main():
    parser = argparse.ArgumentParser(
        description="Extract clean voice samples from video for voice cloning",
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    parser.add_argument("--input", required=True, help="Path to video file (MP4, MKV, etc.)")
    parser.add_argument("--name", help="Output filename stem → voices/{name}.wav (default: derived from input)")
    parser.add_argument("--start", help="Start timestamp (e.g. 00:05, hh:mm:ss)")
    parser.add_argument("--end", help="End timestamp (e.g. 00:30, hh:mm:ss)")
    parser.add_argument("--target-duration", type=float, default=DEFAULT_TARGET_S,
                        help=f"Seconds of clean audio to select (default: {DEFAULT_TARGET_S})")
    parser.add_argument("--output", help="Direct output path (overrides --name and voices/ directory)")
    parser.add_argument("--sensitivity", type=int, default=VAD_MODE,
                        help="VAD aggressiveness 0-3 (0=least, 3=most, default: %(default)s)")
    parser.add_argument("--reduce", action="store_true", help="Apply noise reduction (off by default)")
    parser.add_argument("--compress", action="store_true",
                        help="Apply gentle speech compression to even out loudness")
    parser.add_argument("--norm", choices=["rms", "peak"], default="peak",
                        help="Normalization mode: rms (loudness, default) or peak")
    parser.add_argument("--play", action="store_true", help="Play result via ffplay after saving")
    parser.add_argument("--no-play", action="store_true", help="Never play (default for non-interactive)")
    args = parser.parse_args()

    # Determine output path
    if args.output:
        output_path = args.output
    else:
        name = args.name or Path(args.input).stem
        project_root = Path(__file__).resolve().parent.parent
        voices_dir = project_root / "voices"
        voices_dir.mkdir(exist_ok=True)
        output_path = str(voices_dir / f"{name}.wav")

    # Stage 1: Extract audio
    samples, sr = extract_audio(args.input, args.start, args.end)

    # Stage 2: VAD
    decisions = run_vad(samples)
    segments = build_segments(decisions)

    if not segments:
        log.error("No speech segments found. Try lowering --sensitivity or check the input.")
        sys.exit(1)

    # Stage 3: Quality scoring
    frame_to_sample = len(samples) / len(decisions)
    global_silence_rms = compute_global_silence_rms(samples, decisions)
    scored = [score_segment(samples, seg, global_silence_rms, frame_to_sample) for seg in segments]

    for i, s in enumerate(scored):
        log.info("  seg %2d: score=% .3f  snr=% .1fdB  centroid=% .0fHz  clip=%.1f%%  level=% .1fdBFS  dur=%.2fs",
                 i, s["score"], s["snr"], s["centroid"], s["clip_ratio"] * 100, s["rms_db"], s["duration"])

    # Stage 4: Select best segments
    selected = select_segments(scored, args.target_duration)
    total_dur = sum(s["duration"] for s in selected)
    avg_score = sum(s["score"] for s in selected) / max(len(selected), 1)
    log.info("Best segments: %.1fs total, avg score %.3f", total_dur, avg_score)

    # Stage 5: Optional noise reduction (off by default)
    if args.reduce and global_silence_rms > 0:
        samples = apply_noise_reduction(samples, decisions, selected)

    # Stage 6: Concatenate, normalize, save
    speech_chunks = []
    pad_samples = int(0.05 * sr)  # 50ms padding
    for seg in selected:
        s0 = int(seg["frame_start"] * frame_to_sample)
        s1 = int(seg["frame_end"] * frame_to_sample)
        s0 = max(0, s0 - pad_samples)
        s1 = min(len(samples), s1 + pad_samples)
        speech_chunks.append(samples[s0:s1])

    if not speech_chunks:
        log.error("No audio to save.")
        sys.exit(1)

    output_audio = np.concatenate(speech_chunks)

    # Stage 6: Optional compression
    if args.compress:
        output_audio = apply_compression(output_audio)

    # Normalize
    rms = float(np.sqrt(np.mean(output_audio ** 2)))
    peak = float(np.max(np.abs(output_audio)))
    rms_db = 20 * np.log10(max(rms, 1e-10))
    peak_db = 20 * np.log10(max(peak, 1e-10))

    if args.norm == "rms":
        rms_gain = (10 ** (TARGET_RMS / 20)) / max(rms, 1e-10)
        peak_gain = (10 ** (PEAK_CEILING / 20)) / max(peak, 1e-10)
        gain = min(rms_gain, peak_gain)
        output_audio = output_audio * gain
        new_rms = 20 * np.log10(max(float(np.sqrt(np.mean(output_audio ** 2))), 1e-10))
        new_peak = 20 * np.log10(max(float(np.max(np.abs(output_audio))), 1e-10))
        log.info("RMS normalize: %.1f → %.1f dBFS (peak %.1f → %.1f dBFS, gain %.1f dB)",
                 rms_db, new_rms, peak_db, new_peak, 20 * np.log10(gain))
    else:
        if peak > 0:
            target_peak = 10 ** (PEAK_CEILING / 20)
            output_audio = output_audio * (target_peak / peak)
            new_rms = 20 * np.log10(max(float(np.sqrt(np.mean(output_audio ** 2))), 1e-10))
            log.info("Peak normalize: RMS %.1f → %.1f dBFS, peak %.1f → %.1f dBFS",
                     rms_db, new_rms, peak_db, PEAK_CEILING)

    # Trim leading/trailing silence
    threshold = 0.01
    nonzero = np.where(np.abs(output_audio) > threshold)[0]
    if len(nonzero) > 0:
        output_audio = output_audio[nonzero[0]:nonzero[-1] + 1]

    # Fade in/out (10ms)
    fade_len = int(0.01 * sr)
    if len(output_audio) > 2 * fade_len:
        output_audio[:fade_len] *= np.linspace(0, 1, fade_len)
        output_audio[-fade_len:] *= np.linspace(1, 0, fade_len)

    write_wav(output_path, output_audio, sr)
    final_dur = len(output_audio) / sr
    log.info("Saved: %s (%.1fs, %d samples)", output_path, final_dur, len(output_audio))

    # Playback
    if args.play or (not args.no_play and sys.stdout.isatty()):
        try:
            subprocess.run(
                ["ffplay", "-v", "quiet", "-nodisp", "-autoexit", output_path],
                timeout=120,
            )
        except Exception:
            pass

    return output_path


if __name__ == "__main__":
    main()
