#!/usr/bin/env bash
# Build a universal (arm64 + x86_64) CuktechTray binary on a single runner.
#
# Why: GitHub's macos-26 (arm64) runners queue for 10+ minutes and are often
# cancelled before they start, while SwiftPM itself can emit both slices from an
# Intel runner. So we ask for both architectures here and merge the result, which
# keeps ad-hoc ad-hoc signed universal builds reproducible on the stable runner.
#
# SwiftPM puts multi-arch output in .build/apple/Products/Release/, not in
# .build/release/, so we copy it back to the path every other script expects.
#
# Usage: bash mac/scripts/build-universal.sh [package-path]
set -euo pipefail

pkg="${1:-mac}"
dest="$pkg/.build/release/CuktechTray"

if swift build -c release --package-path "$pkg" --arch arm64 --arch x86_64; then
  built=$(find "$pkg/.build/apple/Products/Release" -maxdepth 1 -name CuktechTray -type f 2>/dev/null | head -n1 || true)
  if [ -n "${built:-}" ]; then
    mkdir -p "$(dirname "$dest")"
    cp -f "$built" "$dest"
  fi
else
  echo "universal build failed, falling back to the host architecture" >&2
  swift build -c release --package-path "$pkg"
fi

ls -l "$dest"
lipo -info "$dest" || true
