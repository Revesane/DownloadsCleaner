#!/usr/bin/env bash
# Сборка версии для macOS: self-contained .app для Apple Silicon или Intel.
#
#   ./build-mac.sh            # osx-arm64 (Apple Silicon)
#   ./build-mac.sh osx-x64    # Intel
#   VERSION=1.2.3 ./build-mac.sh   # версия программы (по умолчанию - из .csproj)
#
# Кроме .app собирается установщик .dmg: окно с программой и ярлыком «Программы»,
# куда её достаточно перетащить (стандартная установка на macOS).
#
# Требуется .NET 8 SDK (https://dotnet.microsoft.com/download/dotnet/8.0) и macOS 12+.

set -euo pipefail

RID="${1:-osx-arm64}"
PROJECT="src/DownloadsCleaner.Mac/DownloadsCleaner.Mac.csproj"
OUT="publish/macos/$RID"

if ! command -v dotnet >/dev/null 2>&1; then
  echo "Не найден dotnet. Установите .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0" >&2
  exit 1
fi

case "$RID" in
  osx-arm64|osx-x64) ;;
  *) echo "Неизвестный runtime: $RID (ожидается osx-arm64 или osx-x64)" >&2; exit 1 ;;
esac

if [ -z "${VERSION:-}" ]; then
  VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$PROJECT" | head -1)"
fi
VERSION="${VERSION:-1.0.0}"

echo "== Публикация $RID (версия $VERSION) =="
rm -rf "$OUT"
FILES="$OUT/files"
dotnet publish "$PROJECT" \
    -c Release \
    -r "$RID" \
    --self-contained true \
    -p:PublishSingleFile=false \
    -p:DebugType=none \
    -p:DebugSymbols=false \
    -p:Version="$VERSION" \
    -o "$FILES" \
    --nologo

# ---- Пакет .app ----
# dotnet publish кладёт только файлы программы; пакет macOS собираем сами:
#   Downloads Cleaner.app/Contents/{Info.plist, MacOS/<файлы программы>, Resources/AppIcon.icns}
APP="$OUT/Downloads Cleaner.app"
EXE="DownloadsCleaner.Mac"
echo "== Пакет $APP =="
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$FILES/." "$APP/Contents/MacOS/"
rm -rf "$FILES"
chmod +x "$APP/Contents/MacOS/$EXE"
cp "installer/macos/AppIcon.icns" "$APP/Contents/Resources/AppIcon.icns"
# Идентификатор совпадает с меткой автозапуска (MacAutostartService.Label).
cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>
    <string>Downloads Cleaner</string>
    <key>CFBundleDisplayName</key>
    <string>Downloads Cleaner</string>
    <key>CFBundleIdentifier</key>
    <string>com.revesane.downloadscleaner</string>
    <key>CFBundleExecutable</key>
    <string>$EXE</string>
    <key>CFBundleIconFile</key>
    <string>AppIcon</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleInfoDictionaryVersion</key>
    <string>6.0</string>
    <key>CFBundleShortVersionString</key>
    <string>$VERSION</string>
    <key>CFBundleVersion</key>
    <string>$VERSION</string>
    <key>LSMinimumSystemVersion</key>
    <string>12.0</string>
    <key>LSApplicationCategoryType</key>
    <string>public.app-category.utilities</string>
    <key>NSHighResolutionCapable</key>
    <true/>
</dict>
</plist>
PLIST

# Ad-hoc подпись (без сертификата): без подписи Apple Silicon вообще не запускает программу.
# Предупреждение «не удалось проверить разработчика» при первом запуске всё равно будет.
if command -v codesign >/dev/null 2>&1; then
  echo "== Подпись (ad-hoc) =="
  # Сначала каждый файл в Contents/MacOS (и .dylib, и .dll/.json - иначе codesign считает
  # их неподписанными вложениями), кроме главного исполняемого, потом сам пакет.
  find "$APP/Contents/MacOS" -type f ! -name "$EXE" | while IFS= read -r f; do
    codesign --force --sign - "$f"
  done
  codesign --force --sign - "$APP"
  codesign --verify --deep --strict --verbose=2 "$APP"
else
  echo "(подпись делается только на macOS - пропущено)"
fi

# ---- Установщик .dmg: программа + ярлык на «Программы» для перетаскивания ----
case "$RID" in
  osx-arm64) ARCH_NAME="AppleSilicon" ;;
  *)         ARCH_NAME="Intel" ;;
esac
DMG="publish/macos/DownloadsCleaner-macOS-$ARCH_NAME.dmg"
if command -v hdiutil >/dev/null 2>&1 && [ -d "$OUT/Downloads Cleaner.app" ]; then
  echo "== Установщик $DMG =="
  STAGE="$(mktemp -d)"
  cp -R "$OUT/Downloads Cleaner.app" "$STAGE/"
  ln -s /Applications "$STAGE/Applications"
  rm -f "$DMG"
  # hdiutil иногда падает с «Resource busy» (диск занят проверкой системы) - пробуем ещё раз.
  for attempt in 1 2 3 4 5; do
    if hdiutil create -volname "Downloads Cleaner" -srcfolder "$STAGE" -ov -format UDZO "$DMG"; then break; fi
    if [ "$attempt" = 5 ]; then exit 1; fi
    echo "hdiutil не справился (попытка $attempt), повтор через 5 с..."
    sleep 5
  done
  rm -rf "$STAGE"
else
  echo "(.dmg собирается только на macOS - пропущено)"
fi

echo ""
echo "Готово:"
ls -1 "$OUT" | sed 's/^/  /'
[ -f "$DMG" ] && echo "  установщик: $DMG"
echo ""
echo "Запуск:  open \"$OUT/Downloads Cleaner.app\""
