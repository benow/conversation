#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/../src/benow-conversation"

dotnet build

export DOTNET_ENVIRONMENT=Development

exec dotnet run -- --stt --daemon
