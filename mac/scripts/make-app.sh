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

# 版本号写进 Info.plist，跟 Windows 版共用同一个版本号
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
