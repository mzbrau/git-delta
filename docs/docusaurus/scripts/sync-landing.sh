#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SRC="$ROOT/../img/landing"
DEST="$ROOT/static/img/landing"

mkdir -p "$DEST"
# Copy PNGs only (skip README)
shopt -s nullglob
files=("$SRC"/*.png)
if ((${#files[@]} == 0)); then
  echo "sync-landing: no PNGs found in $SRC" >&2
  exit 1
fi
cp -f "${files[@]}" "$DEST/"
echo "sync-landing: copied ${#files[@]} PNG(s) → static/img/landing/"
