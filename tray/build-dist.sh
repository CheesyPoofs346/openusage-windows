#!/usr/bin/env bash
# Build a self-contained Windows distribution of OpenUsage into ../dist/.
# Produces a folder you can zip and run on any Windows 10/11 x64 machine that has the WebView2 runtime
# (bundled with Windows 11) and the Swift for Windows runtime available.
set -euo pipefail
cd "$(dirname "$0")"

OUT="../dist"
rm -rf "$OUT"
mkdir -p "$OUT"

echo "==> Building the Swift usage-reader CLI (release)"
( cd .. && swift build -c release --product openusage-cli )

echo "==> Publishing the .NET taskbar app (self-contained, single-file)"
dotnet publish -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:DebugType=none \
  -o "$OUT"

echo "==> Bundling the CLI next to the tray exe"
cp ../.build/release/openusage-cli.exe "$OUT/openusage.exe"

# Bundle the Swift for Windows *runtime redistributable* so the CLI runs on machines with no Swift SDK.
# These live under Programs/Swift/Runtimes/<ver>/usr/bin (NOT the toolchain's bin, which only has a few).
RUNTIME_DIR=""
for cand in "$LOCALAPPDATA/Programs/Swift/Runtimes"/*/usr/bin \
            "$HOME/AppData/Local/Programs/Swift/Runtimes"/*/usr/bin \
            "/c/Program Files/Swift/Runtimes"/*/usr/bin; do
  [ -d "$cand" ] && RUNTIME_DIR="$cand"
done
if [ -n "$RUNTIME_DIR" ]; then
  echo "==> Bundling Swift runtime from $RUNTIME_DIR"
  cp "$RUNTIME_DIR"/*.dll "$OUT/" 2>/dev/null || true
else
  echo "!! Swift runtime redistributable not found — the bundle will need Swift installed to run." >&2
fi

echo "==> Done. Run: $OUT/OpenUsageTray.exe"
