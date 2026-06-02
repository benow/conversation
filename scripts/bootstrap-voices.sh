#!/bin/bash
# bootstrap-voices.sh — Generate voice reference clips using OpenRouter TTS
# Run from project root. Requires DOTNET_ENVIRONMENT=Development for API key.
set -euo pipefail

PROJECT_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
VOICES_DIR="$PROJECT_ROOT/voices"
SRC="$PROJECT_ROOT/src/benow-conversation"

mkdir -p "$VOICES_DIR"

# One sentence per persona — short, neutral, captures voice timbre
declare -A REFERENCE_TEXTS=(
    ["female-1"]="Hello, I am Marina. This is my natural speaking voice."
    ["female-2"]="Hello, I am Sophia. This is my natural speaking voice."
    ["female-3"]="Hello, I am the quiet one. This is my natural speaking voice."
    ["female-4"]="Hello, I am Emily. This is my natural speaking voice."
    ["female-5"]="Hello, I am Rachel. This is my natural speaking voice."
    ["female-6"]="Hello, I am Elara. This is my natural speaking voice."
    ["female-7"]="Hello, I am Olivia. This is my natural speaking voice."
    ["female-8"]="Hello, I am Lily. This is my natural speaking voice."
    ["female-9"]="Hello, I am the narrator. This is my natural speaking voice."
    ["female-10"]="Hello, I am Mira. This is my natural speaking voice."
    ["female-11"]="Hello, I am Jasmine. This is my natural speaking voice."
    ["female-12"]="Hello, I am Celeste. This is my natural speaking voice."
    ["female-13"]="Hello, I am Sage. This is my natural speaking voice."
    ["male-1"]="Hello, my name is Andy. This is my natural speaking voice."
)

echo "Generating voice reference clips..."
echo "Output directory: $VOICES_DIR"
echo ""

for persona in "${!REFERENCE_TEXTS[@]}"; do
    text="${REFERENCE_TEXTS[$persona]}"
    output="$VOICES_DIR/$persona.wav"

    if [ -f "$output" ]; then
        echo "  [SKIP] $persona — already exists"
        continue
    fi

    echo "  [SYNTH] $persona — \"$text\""
    DOTNET_ENVIRONMENT=Development dotnet run --project "$SRC" \
        --persona "$persona" \
        --output "$output" \
        "$text" \
        2>&1 | grep -E "Synthesizing|Playing|Application" || true
    echo "    → $output"
done

echo ""
echo "Done. $(ls "$VOICES_DIR"/*.wav 2>/dev/null | wc -l) reference clips generated."
