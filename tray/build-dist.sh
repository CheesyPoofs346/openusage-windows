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

# Best-effort: copy the Swift for Windows runtime DLLs next to the CLI so it runs without the SDK on PATH.
SWIFT_BIN="$(dirname "$(command -v swift 2>/dev/null || true)")"
if [ -n "${SWIFT_BIN:-}" ] && [ -d "$SWIFT_BIN" ]; then
  echo "==> Copying Swift runtime DLLs from $SWIFT_BIN"
  for dll in swiftCore swiftWinSDK swiftCRT Foundation FoundationNetworking FoundationEssentials \
             FoundationInternationalization swiftDispatch dispatch BlocksRuntime swift_Concurrency \
             swift_RegexParser swift_StringProcessing _FoundationICU swiftSynchronization; do
    cp "$SWIFT_BIN/$dll.dll" "$OUT/" 2>/dev/null || true
  done
  cp "$SWIFT_BIN"/icu*.dll "$OUT/" 2>/dev/null || true
fi

echo "==> Done. Run: $OUT/OpenUsageTray.exe"
