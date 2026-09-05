#!/usr/bin/env bash
# Fetches the embedding model. It lands in a gitignored directory; nothing here is
# committed. This script is the honest cost of running the embedding model in-process: a
# service that called an embedding API would need none of it -- and would need an API key,
# a second vendor, and a network round trip per query instead. ONNX Runtime itself arrives
# through NuGet, with native libraries for linux, macOS and Windows on x64 and arm64.
set -euo pipefail
cd "$(dirname "$0")/.."

MODEL_DIR="model-cache/multilingual-e5-small"
MODEL_REPO="https://huggingface.co/intfloat/multilingual-e5-small/resolve/main"
mkdir -p "$MODEL_DIR"

# The fp32 export, not a quantised one: the int8 builds are per-architecture and would
# make a container image unportable.
if [ ! -f "$MODEL_DIR/model.onnx" ]; then
  echo "==> multilingual-e5-small (470 MB, once)"
  curl -fsSL -o "$MODEL_DIR/model.onnx" "$MODEL_REPO/onnx/model.onnx"
fi
if [ ! -f "$MODEL_DIR/tokenizer.json" ]; then
  echo "==> tokenizer.json"
  curl -fsSL -o "$MODEL_DIR/tokenizer.json" "$MODEL_REPO/tokenizer.json"
fi
echo "==> ready"
