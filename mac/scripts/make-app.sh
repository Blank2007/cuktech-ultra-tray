#!/bin/bash
# 把 swift build 出来的裸 Mach-O 组装成 .app，并做 ad-hoc 签名。
#
# 用法：bash mac/scripts/make-app.sh <built-binary> <output-dir>
#   例：bash mac/scripts/make-app.sh mac/.build/release/CuktechTray dist
#
# 为什么要自己组装：SwiftPM 只产出可执行文件，菜单栏 App 需要 Info.plist 里的
# LSUIElement（不进 Dock/Cmd-Tab）和 NSBluetoothAlwaysUsageDescription（不写这条
# 蓝牙权限弹窗会直接闪退）。签名用 ad-hoc（-s -），因为不买开发者账号；
# 注意 codesign --deep 自 macOS 13 起不再用于签名，这里只签最外层。
set -euo pipefail

BIN="${1:?usage: make-app.sh <binary> <outdir>}"
OUT="${2:?usage: make-app.sh <binary> <outdir>}"
HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/.." && pwd)"
APP="$OUT/CuktechTray.app"

test -f "$BIN" || { echo "missing binary: $BIN" >&2; exit 1; }

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN" "$APP/Contents/MacOS/CuktechTray"
cp "$ROOT/Resources/Info.plist" "$APP/Contents/Info.plist"
chmod +x "$APP/Contents/MacOS/CuktechTray"

# 图标：源码里没有 .icns（二进制不适合进 git），构建时按需画一套 iconset 再打包。
# 生成失败不致命——只是 Finder 里显示成通用图标，比构建直接挂掉好。
echo "== 图标 =="
mkdir -p "$ROOT/.build"
ICONSET="$ROOT/.build/AppIcon.iconset"
if [ ! -d "$ICONSET" ]; then
  if xcrun -sdk macosx swiftc -O "$ROOT/scripts/make-icon.swift" -o "$ROOT/.build/make-icon" 2>/dev/null; then
    "$ROOT/.build/make-icon" "$ICONSET"
  fi
fi
if [ -d "$ICONSET" ]; then
  if xcrun iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/AppIcon.icns" 2>/dev/null; then
    echo "   AppIcon.icns 已放入 Resources"
  else
    echo "   iconutil 打包失败，跳过图标" >&2
  fi
else
  echo "   没生成出 iconset，跳过图标" >&2
fi

# 版本号写进 Info.plist。macOS 版自成一套号（0.1.x），源头是 Sources/CuktechCore/BuildInfo.swift
VER="$("$APP/Contents/MacOS/CuktechTray" --version 2>/dev/null || echo "")"
if [ -n "$VER" ]; then
  /usr/bin/sed -i '' "s/__VERSION__/$VER/g" "$APP/Contents/Info.plist"
else
  /usr/bin/sed -i '' "s/__VERSION__/0.0.0/g" "$APP/Contents/Info.plist"
fi

codesign --force -s - "$APP"
codesign --verify --verbose "$APP" || true

echo "app=$APP"
du -sh "$APP"
