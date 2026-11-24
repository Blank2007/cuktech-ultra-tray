#!/usr/bin/env bash
# 把 dist/CuktechTray.app 打成一个能直接分发的 zip。
# 用 zip 而不是 tar：macOS 自带的「归档实用工具」双击就解，解出来就是 .app。
set -euo pipefail

APP_DIR="${1:-dist}"
APP="${APP_DIR}/CuktechTray.app"

if [ ! -d "$APP" ]; then
  echo "找不到 $APP，先跑 make-app.sh" >&2
  exit 1
fi

VERSION="${2:-}"
if [ -z "$VERSION" ]; then
  VERSION="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$APP/Contents/Info.plist")"
fi

ZIP="CuktechTray-${VERSION}-mac.zip"

# -y 保留符号链接、-r 递归、-q 少吵、-X 去掉扩展属性（否则解压时会有 `__MACOSX`）
( cd "$APP_DIR" && rm -f "$ZIP" && zip -qryX "$ZIP" CuktechTray.app )
ls -l "${APP_DIR}/${ZIP}"
echo "SHA256: $(shasum -a 256 "${APP_DIR}/${ZIP}" | cut -d' ' -f1)"
