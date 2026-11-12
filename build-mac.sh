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

echo "== Публикация $RID =="
rm -rf "$OUT"
dotnet publish "$PROJECT" \
    -c Release \
    -r "$RID" \
    --self-contained true \
    -p:PublishSingleFile=false \
    -p:DebugType=none \
    -p:DebugSymbols=false \
    ${VERSION:+-p:Version=$VERSION} \
    -o "$OUT" \
    --nologo

# .app создаётся самим SDK при публикации с -r osx-*; переименовываем его в человеческое имя.
APP="$OUT/DownloadsCleaner.Mac.app"
if [ -d "$APP" ]; then
  mv "$APP" "$OUT/Downloads Cleaner.app"
  # Имя исполняемого файла внутри пакета должно совпадать с именем пакета.
  mv "$OUT/Downloads Cleaner.app/Contents/MacOS/DownloadsCleaner.Mac" \
     "$OUT/Downloads Cleaner.app/Contents/MacOS/Downloads Cleaner" 2>/dev/null || true
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
  hdiutil create -volname "Downloads Cleaner" -srcfolder "$STAGE" -ov -format UDZO "$DMG"
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
