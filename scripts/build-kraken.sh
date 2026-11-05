#!/bin/bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
source_dir="$root/native/kraken"
output="$root/artifacts/native/libkraken.dylib"
if [[ ! -f "$source_dir/kraken.cpp" || ! -f "$root/native/simde/simde/x86/sse.h" ]]; then
  echo 'Native sources are missing. Run: git submodule update --init --recursive' >&2
  exit 1
fi
if [[ -f "$output" ]] && ! find "$source_dir" "$root/native/simde/simde" -type f \( -name '*.cpp' -o -name '*.h' \) -newer "$output" -print -quit | read -r _; then
  exit 0
fi
mkdir -p "$(dirname "$output")"
echo 'Building universal libkraken (Apple Silicon + Intel)…'
xcrun clang++ -std=c++14 -O2 -dynamiclib -arch arm64 -arch x86_64 \
  -mmacosx-version-min=11.0 -DSIMDE_ENABLE_NATIVE_ALIASES \
  -I "$root/native/simde" -I "$source_dir" \
  "$source_dir/kraken.cpp" "$source_dir/compress.cpp" \
  "$source_dir/compr_multiarray.cpp" "$source_dir/compr_match_finder.cpp" \
  "$source_dir/compr_leviathan.cpp" "$source_dir/compr_kraken.cpp" \
  "$source_dir/compr_tans.cpp" "$source_dir/compr_entropy.cpp" \
  -install_name @rpath/libkraken.dylib -o "$output.tmp"
mv "$output.tmp" "$output"
