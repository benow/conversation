#!/usr/bin/env python3
"""
Batch female voice extraction from video directories.

Scans a directory of videos, finds the best contiguous female-voice segment
(~30s) from each, verifies with word recognition (Whisper), and extracts it.

Pipeline per video:
  1. ffmpeg extracts full audio to 16kHz mono
  2. webrtcvad splits into speech/silence frames
  3. Merge close speech into "talk regions"
  4. Score each region: pitch (F0 in female range), quality, duration
  5. Whisper word-detection on top candidates — must contain actual words
  6. Extract best ~30s contiguous chunk from the winning region
  7. Peak normalize → save

Usage:
    .venv-tts/bin/python scripts/batch-female-extract.py --dir /path/to/videos
"""

import argparse
import logging
import os
import sys
import subprocess
import tempfile
import shutil
from pathlib import Path

import numpy as np
import librosa
import webrtcvad
import soundfile as sf

logging.basicConfig(level=logging.INFO, format="[batch-female] %(message)s")
log = logging.getLogger("batch-female")

# ---------------------------------------------------------------------------
# Constants
# ---------------------------------------------------------------------------
SAMPLE_RATE = 16000
VAD_FRAME_MS = 30
VAD_FRAME_SAMPLES = int(SAMPLE_RATE * VAD_FRAME_MS / 1000)
VAD_MODE = 2
MERGE_GAP_S = 3.0
MIN_REGION_S = 8.0
TARGET_DURATION_S = 30
F0_FEMALE_LOW = 150.0
F0_FEMALE_HIGH = 280.0
F0_IDEAL = 210.0
MIN_WORDS_WHISPER = 4


# ---------------------------------------------------------------------------
# Audio extraction
# ---------------------------------------------------------------------------

def extract_full_audio(video_path: str) -> tuple[np.ndarray, int]:
    cmd = ["ffmpeg", "-v", "error", "-i", video_path,
           "-ac", "1", "-ar", str(SAMPLE_RATE), "-f", "s16le", "pipe:1"]
    proc = subprocess.run(cmd, capture_output=True, timeout=300)
    if proc.returncode != 0 or not proc.stdout:
        raise RuntimeError(f"ffmpeg extraction failed (exit {proc.returncode})")
    samples = np.frombuffer(proc.stdout, dtype=np.int16).astype(np.float32) / 32768.0
    dur = len(samples) / SAMPLE_RATE
    log.info("  Extracted %.1fs audio (%d samples)", dur, len(samples))
    return samples, SAMPLE_RATE


# ---------------------------------------------------------------------------
# VAD + region building
# ---------------------------------------------------------------------------

def run_vad(samples: np.ndarray) -> np.ndarray:
    vad = webrtcvad.Vad(VAD_MODE)
    num_frames = len(samples) // VAD_FRAME_SAMPLES
    if num_frames == 0:
        return np.array([], dtype=bool)
    trimmed = samples[:num_frames * VAD_FRAME_SAMPLES]
    pcm = (trimmed * 32767).astype(np.int16)
    decisions = np.zeros(num_frames, dtype=bool)
    for i in range(num_frames):
        frame = pcm[i * VAD_FRAME_SAMPLES:(i + 1) * VAD_FRAME_SAMPLES].tobytes()
        try:
            decisions[i] = vad.is_speech(frame, SAMPLE_RATE)
        except Exception:
            decisions[i] = False
    return decisions


def build_talk_regions(decisions: np.ndarray, samples: np.ndarray) -> list[dict]:
    """Merge speech frames into contiguous 'talk regions' separated by >= MERGE_GAP_S silence."""
    merge_gap_frames = max(1, int(MERGE_GAP_S * 1000 / VAD_FRAME_MS))
    min_frames = max(1, int(MIN_REGION_S * 1000 / VAD_FRAME_MS))
    frame_to_sample = len(samples) / len(decisions)

    segments = []
    in_speech, start = False, 0
    for i, speech in enumerate(decisions):
        if speech and not in_speech:
            start = i; in_speech = True
        elif not speech and in_speech:
            segments.append((start, i)); in_speech = False
    if in_speech:
        segments.append((start, len(decisions)))

    merged = []
    for seg in segments:
        if merged and seg[0] - merged[-1][1] <= merge_gap_frames:
            merged[-1] = (merged[-1][0], seg[1])
        else:
            merged.append(seg)

    regions = []
    for s0, s1 in merged:
        dur = (s1 - s0) * VAD_FRAME_MS / 1000.0
        if dur < MIN_REGION_S:
            continue
        start_sample = int(s0 * frame_to_sample)
        end_sample = int(s1 * frame_to_sample)
        region_audio = samples[start_sample:end_sample]
        regions.append({
            "start_frame": s0, "end_frame": s1,
            "start_sample": start_sample, "end_sample": end_sample,
            "duration": dur, "audio": region_audio,
        })
    return regions


# ---------------------------------------------------------------------------
# Pitch / female-voice scoring
# ---------------------------------------------------------------------------

def estimate_pitch_distribution(audio: np.ndarray, sr: int) -> tuple[float, float, float]:
    """Estimate F0 via librosa PYIN. Returns (median_f0, voiced_pct, f0_std).
    Caps analysis to at most 60s (middle portion) for performance."""
    dur = len(audio) / sr
    if dur < 0.5:
        return 0.0, 0.0, 0.0
    max_samples = 60 * sr
    if len(audio) > max_samples:
        offset = (len(audio) - max_samples) // 2
        audio = audio[offset:offset + max_samples]
    try:
        f0, voiced, _ = librosa.pyin(
            audio.astype(np.float64), fmin=80, fmax=600,
            sr=sr, frame_length=2048
        )
        voiced_f0 = f0[voiced]
        if len(voiced_f0) < 5:
            return 0.0, 0.0, 0.0
        median = float(np.median(voiced_f0))
        std = float(np.std(voiced_f0))
        voiced_pct = float(len(voiced_f0)) / len(f0) * 100
        return median, voiced_pct, std
    except Exception:
        return 0.0, 0.0, 0.0


def score_female_voice(median_f0: float, voiced_pct: float, f0_std: float,
                       spectral_centroid: float, f0_low: float, f0_high: float,
                       strict: bool = False) -> float:
    """Score how likely this voice is female. Returns 0.0–1.0."""
    if median_f0 <= 0 or voiced_pct < 10:
        return 0.0
    if strict and median_f0 < f0_low:
        return 0.0

    f0_score = 0.0
    if f0_low <= median_f0 <= f0_high:
        dist = abs(median_f0 - F0_IDEAL) / (f0_high - F0_IDEAL)
        f0_score = max(0.0, 1.0 - dist)
    elif median_f0 > f0_high:
        f0_score = max(0.0, 1.0 - (median_f0 - f0_high) / 200.0)
    else:
        f0_score = max(0.0, median_f0 / f0_low * 0.5)

    voiced_score = min(voiced_pct / 60.0, 1.0)
    stability_score = max(0.0, 1.0 - f0_std / 100.0)
    centroid_score = min(max(0.0, (spectral_centroid - 800) / 2000.0), 1.0)

    return 0.50 * f0_score + 0.25 * voiced_score + 0.15 * stability_score + 0.10 * centroid_score


# ---------------------------------------------------------------------------
# Quality scoring
# ---------------------------------------------------------------------------

def score_quality(region_audio: np.ndarray, global_silence_rms: float) -> dict:
    rms = float(np.sqrt(np.mean(region_audio ** 2)))
    noise_rms = max(global_silence_rms, 1e-10)
    snr = 20 * np.log10(max(rms, 1e-10) / noise_rms)
    snr_score = min(snr / 30.0, 1.0)

    n_fft = min(2048, len(region_audio))
    fft = np.abs(np.fft.rfft(region_audio, n=n_fft))
    freqs = np.fft.rfftfreq(n_fft, 1.0 / SAMPLE_RATE)
    centroid = float(np.sum(freqs * fft) / max(np.sum(fft), 1e-10))

    peak = max(float(np.max(np.abs(region_audio))), 1e-10)
    clip_ratio = float(np.sum(np.abs(region_audio) >= 0.95 * peak) / len(region_audio))
    clip_penalty = clip_ratio * 3.0

    rms_db = 20 * np.log10(max(rms, 1e-10))
    if -25 <= rms_db <= -8:
        level_score = 1.0
    elif rms_db > -8:
        level_score = max(0.0, 1.0 - (rms_db + 8) / 10.0)
    else:
        level_score = max(0.0, 1.0 - (-25 - rms_db) / 25.0)

    quality = 0.40 * snr_score + 0.40 * level_score - clip_penalty
    return {"quality": max(0.0, quality), "snr": round(snr, 1),
            "centroid": round(centroid, 0), "clip_ratio": round(clip_ratio, 4)}


# ---------------------------------------------------------------------------
# Whisper word detection
# ---------------------------------------------------------------------------

_whisper_model = None

def get_whisper_model():
    global _whisper_model
    if _whisper_model is None:
        import whisper
        os.environ["CUDA_VISIBLE_DEVICES"] = ""
        _whisper_model = whisper.load_model("tiny", device="cpu")
        log.info("  Whisper tiny model loaded (CPU)")
    return _whisper_model


def has_recognizable_speech(audio: np.ndarray, sr: int) -> tuple[bool, str, int]:
    """Run Whisper on audio segment. Returns (is_speech, text, word_count)."""
    try:
        model = get_whisper_model()
        result = model.transcribe(audio.astype(np.float32), language="en",
                                  fp16=False, verbose=False)
        text = result["text"].strip()
        words = text.split()
        word_count = len([w for w in words if len(w) > 1])
        is_speech = word_count >= MIN_WORDS_WHISPER and len(text) > 15
        return is_speech, text, word_count
    except Exception as e:
        log.warning("  Whisper error: %s", e)
        return False, "", 0


# ---------------------------------------------------------------------------
# Region selection
# ---------------------------------------------------------------------------

def select_best_regions(regions: list[dict], samples: np.ndarray,
                        decisions: np.ndarray, global_silence_rms: float,
                        skip_whisper: bool, target_duration: float,
                        f0_low: float, f0_high: float,
                        max_regions: int = 1,
                        strict_female: bool = False) -> list[dict]:
    """Score, verify and return the top N talk regions."""
    if not regions:
        return None

    frame_to_sample = len(samples) / len(decisions)

    for r in regions:
        median_f0, voiced_pct, f0_std = estimate_pitch_distribution(r["audio"], SAMPLE_RATE)
        q = score_quality(r["audio"], global_silence_rms)
        female = score_female_voice(median_f0, voiced_pct, f0_std, q["centroid"],
                                     f0_low, f0_high, strict=strict_female)

        dur_bonus = min(r["duration"] / target_duration, 1.0)
        r["f0"] = round(median_f0, 0)
        r["voiced_pct"] = round(voiced_pct, 1)
        r["f0_std"] = round(f0_std, 1)
        r["female_score"] = round(female, 3)
        r["quality_score"] = round(q["quality"], 3)
        r["snr"] = q["snr"]
        r["centroid"] = q["centroid"]
        r["combined"] = round(female * 0.45 + q["quality"] * 0.35 + dur_bonus * 0.20, 3)

        log.info("  region %.1fs: F0=%.0fHz voiced=%.0f%% female=%.3f quality=%.3f SNR=%.1fdB → combined=%.3f",
                 r["duration"], r["f0"], r["voiced_pct"], r["female_score"],
                 r["quality_score"], r["snr"], r["combined"])

    regions.sort(key=lambda r: r["combined"], reverse=True)

    selected = []
    for r in regions:
        if not skip_whisper and r["duration"] >= 10:
            sample_dur = min(len(r["audio"]) / SAMPLE_RATE, 30.0)
            sample_audio = r["audio"][:int(sample_dur * SAMPLE_RATE)]
            is_speech, text, wc = has_recognizable_speech(sample_audio, SAMPLE_RATE)
            r["whisper_text"] = text
            r["whisper_words"] = wc
            log.info("  Whisper check (%.1fs): words=%d speech=%s text=\"%s\"",
                     r["duration"], wc, is_speech, text[:80])
            if not is_speech:
                continue
        elif not skip_whisper and r["duration"] < 10:
            continue

        selected.append(r)
        if len(selected) >= max_regions:
            break

    if not selected and regions:
        selected = regions[:max_regions]
        log.info("  No Whisper-verified regions — falling back to top %d scored", len(selected))

    return selected


# ---------------------------------------------------------------------------
# Chunk extraction
# ---------------------------------------------------------------------------

def extract_best_chunk(samples: np.ndarray, region: dict,
                       target_duration: float) -> np.ndarray:
    """Extract the best contiguous chunk from within a talk region."""
    audio = region["audio"]
    target = int(target_duration * SAMPLE_RATE)

    if len(audio) <= target + int(SAMPLE_RATE):
        return audio

    # Sliding window: find the chunk with highest RMS (most energetic = clearest speech)
    step = int(SAMPLE_RATE * 1.0)
    best_start = 0
    best_rms = 0.0
    win = target

    for start in range(0, len(audio) - win, step):
        chunk = audio[start:start + win]
        rms = float(np.sqrt(np.mean(chunk ** 2)))
        if rms > best_rms:
            best_rms = rms
            best_start = start

    return audio[best_start:best_start + win]


# ---------------------------------------------------------------------------
# Main batch processor
# ---------------------------------------------------------------------------

def find_video_files(directory: str) -> list[str]:
    exts = {".mp4", ".mkv", ".wmv", ".avi", ".mov", ".webm", ".flv", ".m4v", ".mpg", ".mpeg"}
    videos = []
    for root, _, files in os.walk(directory):
        for f in files:
            if Path(f).suffix.lower() in exts:
                videos.append(os.path.join(root, f))
    return sorted(videos)


def process_video(video_path: str, output_dir: str, skip_whisper: bool,
                  target_duration: float, f0_low: float, f0_high: float,
                  segments_per_video: int = 1,
                  strict_female: bool = False) -> list[dict]:
    video_name = Path(video_path).stem
    log.info("Video: %s", video_name)

    try:
        samples, sr = extract_full_audio(video_path)
    except Exception as e:
        log.warning("  Skipping — extraction failed: %s", e)
        return None

    decisions = run_vad(samples)
    if len(decisions) == 0 or decisions.sum() == 0:
        log.info("  No speech detected — skipping")
        return None

    regions = build_talk_regions(decisions, samples)
    if not regions:
        log.info("  No talk regions >= %.0fs — skipping", MIN_REGION_S)
        return None

    log.info("  %d talk regions found", len(regions))

    silence_rms = 0.0
    silence_frames = samples[np.where(~decisions)[0] * VAD_FRAME_SAMPLES] if len(decisions) > 0 else np.array([])
    if len(silence_frames) > 0:
        silence_rms = float(np.sqrt(np.mean(silence_frames ** 2)))

    best_regions = select_best_regions(regions, samples, decisions, silence_rms, skip_whisper,
                                       target_duration, f0_low, f0_high,
                                       max_regions=segments_per_video,
                                       strict_female=strict_female)
    if not best_regions:
        log.info("  No suitable region — skipping")
        return []

    results = []
    for idx, best in enumerate(best_regions):
        chunk = extract_best_chunk(samples, best, target_duration)

        peak = float(np.max(np.abs(chunk)))
        if peak > 0:
            target_peak = 10 ** (-1.0 / 20)
            chunk = chunk * (target_peak / peak)

        threshold = 0.01
        nonzero = np.where(np.abs(chunk) > threshold)[0]
        if len(nonzero) > 0:
            chunk = chunk[nonzero[0]:nonzero[-1] + 1]

        fade = int(0.01 * sr)
        if len(chunk) > 2 * fade:
            chunk[:fade] *= np.linspace(0, 1, fade)
            chunk[-fade:] *= np.linspace(1, 0, fade)

        suffix = f"_{idx+1}" if len(best_regions) > 1 else ""
        out_path = os.path.join(output_dir, f"{video_name}{suffix}.wav")
        sf.write(out_path, chunk, sr, subtype="PCM_16")
        dur = len(chunk) / sr
        log.info("  Saved: %s (%.1fs)", out_path, dur)

        results.append({
            "video": video_path,
            "output": out_path,
            "duration": round(dur, 1),
            "f0": best.get("f0", 0),
            "female_score": best.get("female_score", 0),
            "quality_score": best.get("quality_score", 0),
            "snr": best.get("snr", 0),
            "centroid": best.get("centroid", 0),
            "whisper_text": best.get("whisper_text", ""),
        })

    return results


def main():
    parser = argparse.ArgumentParser(description="Batch female voice extraction")
    parser.add_argument("--dir", required=True, help="Directory containing videos to scan")
    parser.add_argument("--output-dir", help="Output directory (default: voices/female-batch)")
    parser.add_argument("--target-duration", type=float, default=TARGET_DURATION_S,
                        help=f"Target contiguous seconds per video (default: {TARGET_DURATION_S})")
    parser.add_argument("--min-f0", type=float, default=F0_FEMALE_LOW,
                        help="Minimum F0 for female voice (default: %(default)s)")
    parser.add_argument("--max-f0", type=float, default=F0_FEMALE_HIGH,
                        help="Maximum F0 for female voice (default: %(default)s)")
    parser.add_argument("--whisper", action="store_true", help="Enable Whisper word verification (slow, CPU only)")
    parser.add_argument("--segments-per-video", type=int, default=1,
                        help="Number of segments to extract per video (default: 1)")
    parser.add_argument("--strict-female", action="store_true",
                        help="Hard F0 cutoff: reject voices below --min-f0")
    parser.add_argument("--max-videos", type=int, default=0,
                        help="Process at most N videos (0=all)")
    args = parser.parse_args()

    target_duration = args.target_duration
    f0_low = args.min_f0
    f0_high = args.max_f0

    directory = os.path.abspath(args.dir)
    if not os.path.isdir(directory):
        log.error("Directory not found: %s", directory)
        sys.exit(1)

    if args.output_dir:
        output_dir = os.path.abspath(args.output_dir)
    else:
        project_root = Path(__file__).resolve().parent.parent
        output_dir = str(project_root / "voices" / "female-batch")
    os.makedirs(output_dir, exist_ok=True)

    videos = find_video_files(directory)
    if not videos:
        log.error("No video files found in %s", directory)
        sys.exit(1)

    log.info("Found %d videos in %s", len(videos), directory)
    if args.max_videos > 0:
        videos = videos[:args.max_videos]
        log.info("Limited to first %d", args.max_videos)

    import time
    start_time = time.time()
    last_progress = start_time
    total_videos = len(videos)
    total_segments = 0

    results = []
    for i, video_path in enumerate(videos):
        now = time.time()
        if now - last_progress >= 30 or i == 0:
            elapsed = now - start_time
            rate = (i + 1) / max(elapsed, 1) * 60
            eta = (total_videos - i) / max(rate, 0.001)
            log.info("Progress: %d/%d (%.1f/min) ETA %.0fm, %d segments",
                     i + 1, total_videos, rate, eta, total_segments)
            last_progress = now

        try:
            result = process_video(video_path, output_dir, not args.whisper,
                                   target_duration, f0_low, f0_high,
                                   segments_per_video=args.segments_per_video,
                                   strict_female=args.strict_female)
            if result:
                results.extend(result)
                total_segments += len(result)
            log.info("")
        except Exception as e:
            log.warning("  Unexpected error: %s", e)

    log.info("=== SUMMARY ===")
    log.info("Processed: %d videos", len(videos))
    log.info("Extracted: %d samples → %s", len(results), output_dir)

    for r in results:
        name = Path(r["video"]).stem
        log.info("  %s  F0=%.0fHz  female=%.3f  quality=%.3f  SNR=%.1fdB  dur=%.1fs  text=\"%s\"",
                 name, r["f0"], r["female_score"], r["quality_score"],
                 r["snr"], r["duration"],
                 r.get("whisper_text", "")[:60])


if __name__ == "__main__":
    main()
