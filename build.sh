#!/usr/bin/env bash
# Builds the plugin for every supported platform into dist/X-Ray_<version>/ (and a zip of it).
# One package serves linux-x64, linux-arm64 (Raspberry Pi 4/5, 64-bit OS), win-x64, win-arm64 and
# osx-arm64: the plugin picks runtimes/<rid>/onnxruntime.native at startup.
set -euo pipefail
cd "$(dirname "$0")"
VERSION=$(sed -n 's:.*<AssemblyVersion>\(.*\)</AssemblyVersion>.*:\1:p' src/Jellyfin.Plugin.SceneXray/Jellyfin.Plugin.SceneXray.csproj)
OUT=dist/X-Ray_${VERSION}
PUB=$(mktemp -d)
dotnet publish src/Jellyfin.Plugin.SceneXray -c Release --self-contained false -o "$PUB" >/dev/null
rm -rf "$OUT" && mkdir -p "$OUT"
cp "$PUB"/Jellyfin.Plugin.SceneXray.dll "$PUB"/Jellyfin.Plugin.SceneXray.Sharing.dll "$PUB"/Microsoft.ML.OnnxRuntime.dll "$PUB"/System.Numerics.Tensors.dll "$OUT"/

# Native ONNX Runtime per platform, renamed so Jellyfin never mistakes it for a .NET assembly.
for pair in linux-x64:libonnxruntime.so linux-arm64:libonnxruntime.so win-x64:onnxruntime.dll win-arm64:onnxruntime.dll osx-arm64:libonnxruntime.dylib; do
  rid=${pair%%:*}
  src="$PUB/runtimes/$rid/native/${pair#*:}"
  [ -f "$src" ] || { echo "missing $src" >&2; exit 1; }
  mkdir -p "$OUT/runtimes/$rid"
  cp "$src" "$OUT/runtimes/$rid/onnxruntime.native"
done

cp images/logo.png "$OUT/logo.png"

cat > "$OUT/meta.json" <<JSON
{
  "category": "General",
  "changelog": "",
  "description": "Amazon-style X-Ray: the cast in the current scene and the song that is playing.",
  "guid": "5b3f1e9a-7c42-4d1b-9a60-2f8e4c7d3a11",
  "name": "X-Ray",
  "overview": "Who is on screen, and what is playing.",
  "owner": "achmadoo",
  "targetAbi": "12.3.0.0",
  "timestamp": "$(date -u +%Y-%m-%dT%H:%M:%SZ)",
  "version": "${VERSION}",
  "status": "Active",
  "autoUpdate": false,
  "imagePath": "logo.png",
  "assemblies": ["Jellyfin.Plugin.SceneXray.dll", "Jellyfin.Plugin.SceneXray.Sharing.dll", "Microsoft.ML.OnnxRuntime.dll", "System.Numerics.Tensors.dll"]
}
JSON
rm -rf "$PUB"
(cd "$OUT" && rm -f "../xray_${VERSION}.zip" && zip -qr "../xray_${VERSION}.zip" .)
echo "$OUT"
