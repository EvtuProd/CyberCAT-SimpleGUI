#!/bin/bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
rid="${1:-osx-arm64}"
case "$rid" in osx-arm64|osx-x64) ;; *) echo 'Usage: build-macos.sh [osx-arm64|osx-x64]' >&2; exit 2 ;; esac
dotnet_cmd="${DOTNET:-dotnet}"
if ! command -v "$dotnet_cmd" >/dev/null 2>&1; then
  if [[ -x "$HOME/.local/share/dotnet/dotnet" ]]; then dotnet_cmd="$HOME/.local/share/dotnet/dotnet";
  elif [[ -x "$HOME/.dotnet/dotnet" ]]; then dotnet_cmd="$HOME/.dotnet/dotnet";
  else echo '.NET 8 SDK is required to build. See README-macOS.md.' >&2; exit 1; fi
fi
output="$root/artifacts/$rid"
app="$output/CyberCAT.app"
# This directory contains only generated build output. Publish into an empty
# bundle so older dependency files cannot survive a package downgrade/removal.
if [[ -d "$app" ]]; then rm -rf -- "$app"; fi
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
"$dotnet_cmd" publish "$root/CyberCAT.Mac/CyberCAT.Mac.csproj" \
  -c Release -r "$rid" --self-contained true \
  -p:NuGetAudit=false -p:PublishTrimmed=false -p:DebugType=None \
  -o "$app/Contents/MacOS" --nologo -v:quiet -clp:ErrorsOnly
cp "$root/scripts/Info.plist" "$app/Contents/Info.plist"
cp "$root/LICENSE" "$app/Contents/Resources/LICENSE.txt"
cp -R "$root/licenses" "$app/Contents/Resources/"
cp "$root/WolvenKit/LICENSE" "$app/Contents/Resources/WolvenKit-LICENSE.txt"
cp "$root/native/simde/COPYING" "$app/Contents/Resources/SIMDe-LICENSE.txt"
cp "$root/README-macOS.md" "$app/Contents/Resources/"
iconset="$root/artifacts/CyberCAT.iconset"
mkdir -p "$iconset"
for size in 16 32 128 256 512; do
  sips -s format png -z "$size" "$size" "$root/CP2077SaveEditor/icon.ico" --out "$iconset/icon_${size}x${size}.png" >/dev/null
  double=$((size * 2))
  sips -s format png -z "$double" "$double" "$root/CP2077SaveEditor/icon.ico" --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$iconset" -o "$app/Contents/Resources/CyberCAT.icns"
# Ad-hoc signing makes the locally built Apple Silicon bundle runnable.
# Distribution with Developer ID signing/notarization is a separate release step.
codesign --force --deep --sign - --entitlements "$root/scripts/macos.entitlements" "$app"
codesign --verify --deep --strict "$app"
ditto -c -k --sequesterRsrc --keepParent "$app" "$output/CyberCAT-$rid.zip"
echo "Built: $app"
echo "Archive: $output/CyberCAT-$rid.zip"
