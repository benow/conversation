#!/bin/bash
# cosyvoice-server.sh — Start the CosyVoice TTS server
set -euo pipefail

PROJECT_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
VENV_DIR="$PROJECT_ROOT/.venv-tts"
SERVER_SCRIPT="$PROJECT_ROOT/scripts/cosyvoice-server.py"
MODEL_DIR="$VENV_DIR/pretrained_models/CosyVoice-300M"
PORT="${1:-50000}"

if [ ! -d "$VENV_DIR" ]; then
    echo "Virtual environment not found. Run ./scripts/install-cosyvoice.sh first."
    exit 1
fi

source "$VENV_DIR/bin/activate"
export MODEL_DIR PORT
export CUDA_VISIBLE_DEVICES=""

echo "Starting CosyVoice server on port $PORT..."
echo "Model: $MODEL_DIR"
echo ""

exec python3 "$SERVER_SCRIPT" --model-dir "$MODEL_DIR" --port "$PORT" "$@"
