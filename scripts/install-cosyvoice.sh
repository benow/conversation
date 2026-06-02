#!/bin/bash
# install-cosyvoice.sh — Install CosyVoice-300M-Instruct for local TTS
# Run from the project root: ./scripts/install-cosyvoice.sh

set -euo pipefail
RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'; NC='\033[0m'
log()  { echo -e "${GREEN}[COSY]${NC} $1"; }
warn() { echo -e "${YELLOW}[COSY]${NC} $1"; }
err()  { echo -e "${RED}[COSY]${NC} $1"; exit 1; }

PROJECT_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
VENV_DIR="$PROJECT_ROOT/.venv-tts"
MODEL_DIR="$VENV_DIR/pretrained_models/CosyVoice-300M-Instruct"
VOICES_DIR="$PROJECT_ROOT/voices"
SERVER_SCRIPT="$PROJECT_ROOT/scripts/cosyvoice-server.py"

# ── Python version check ──────────────────────────
PYTHON_BIN="python3.12"
if ! command -v "$PYTHON_BIN" &>/dev/null; then
    if command -v python3.11 &>/dev/null; then
        PYTHON_BIN="python3.11"
    elif command -v python3.10 &>/dev/null; then
        PYTHON_BIN="python3.10"
    else
        warn "Python 3.10–3.12 not found. Installing deadsnakes PPA for 3.12..."
        sudo add-apt-repository -y ppa:deadsnakes/ppa
        sudo apt-get update -qq
        sudo apt-get install -y python3.12 python3.12-venv python3.12-dev
        PYTHON_BIN="python3.12"
    fi
fi
log "Using Python: $($PYTHON_BIN --version)"

# ── System dependencies ───────────────────────────
log "Installing system dependencies..."
sudo apt-get install -y -qq sox libsox-dev espeak-ng libsndfile1 2>/dev/null || true

# ── Virtual environment ───────────────────────────
if [ ! -d "$VENV_DIR" ]; then
    log "Creating virtual environment at $VENV_DIR..."
    $PYTHON_BIN -m venv "$VENV_DIR"
fi
source "$VENV_DIR/bin/activate"
pip install --upgrade pip -q

# ── PyTorch (CPU-only) ────────────────────────────
log "Installing PyTorch (CPU)..."
pip install torch torchaudio --index-url https://download.pytorch.org/whl/cpu -q

# ── CosyVoice ─────────────────────────────────────
log "Installing CosyVoice..."
pip install cosyvoice soundfile fastapi uvicorn python-multipart -q

# ── System audio lib for CosyVoice ────────────────
pip install onnxruntime -q

# ── Model download ────────────────────────────────
if [ ! -d "$MODEL_DIR" ]; then
    log "Downloading CosyVoice-300M-Instruct model (~600MB)..."
    python3 -c "
from modelscope import snapshot_download
snapshot_download('iic/CosyVoice-300M-Instruct', local_dir='$MODEL_DIR')
"
else
    log "Model already downloaded at $MODEL_DIR"
fi

# ── Voice reference directory ─────────────────────
mkdir -p "$VOICES_DIR"
log "Voice reference directory: $VOICES_DIR"

# ── Verify ────────────────────────────────────────
log "Verifying installation..."
python3 -c "
import sys
sys.path.insert(0, '$VENV_DIR/lib/python$(python3 -c \"import sys; print(f'{sys.version_info.major}.{sys.version_info.minor}')\")/site-packages')
from cosyvoice.cli.cosyvoice import CosyVoice
print('CosyVoice imported successfully')
" || warn "CosyVoice import failed — some dependencies may be missing."

# ── Done ──────────────────────────────────────────
log ""
log "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
log "Installation complete."
log ""
log "Next steps:"
log "  1. Generate voice reference clips:"
log "     ./scripts/bootstrap-voices.sh"
log ""
log "  2. Start the CosyVoice server:"
log "     ./scripts/cosyvoice-server.sh"
log ""
log "  3. Test with a single phrase:"
log "     curl -X POST http://localhost:50000/v1/tts \\"
log "       -H 'Content-Type: application/json' \\"
log "       -d '{\"text\":\"Hello world\",\"voice_ref\":\"voices/female-1.wav\"}' \\"
log "       --output /tmp/test.wav"
log ""
log "  4. Switch backend in appsettings.json:"
log "     \"TtsBackend\": \"cosyvoice\""
log "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
