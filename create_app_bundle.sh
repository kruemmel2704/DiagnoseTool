#!/bin/bash
# Erstellt pro Architektur ein ad-hoc signiertes DiagnoseTool.app und ein DMG:
#   DiagnoseTool-macOS-AppleSilicon.dmg (osx-arm64)
#   DiagnoseTool-macOS-Intel.dmg        (osx-x64)
# Ad-hoc-Signatur ("codesign -s -") benötigt keinen Apple-Developer-Account, ist aber auf
# Apple Silicon Pflicht, damit macOS das Programm überhaupt startet.
# Aufruf: ./create_app_bundle.sh [arm64|x64]   (ohne Argument: beide)
set -euo pipefail

APP_VERSION=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' DiagnoseTool.csproj | head -n1)
APP_VERSION=${APP_VERSION:-0.0.0}
ARCHS=("${@:-arm64 x64}")
read -r -a ARCHS <<< "${ARCHS[*]}"

echo "=== DiagnoseTool macOS Build v$APP_VERSION (${ARCHS[*]}) ==="

for ARCH in "${ARCHS[@]}"; do
    case "$ARCH" in
        arm64) LABEL="AppleSilicon"; MIN_OS="11.0" ;;
        x64)   LABEL="Intel";        MIN_OS="10.15" ;;
        *) echo "Unbekannte Architektur: $ARCH"; exit 1 ;;
    esac

    RID="osx-$ARCH"
    OUT="./publish/$RID"
    APP_DIR="./build-macos/$LABEL/DiagnoseTool.app"
    DMG="DiagnoseTool-macOS-$LABEL.dmg"

    echo "--- [$LABEL] dotnet publish ($RID) ---"
    dotnet publish DiagnoseTool.csproj -c Release -r "$RID" -o "$OUT" --self-contained true \
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true

    echo "--- [$LABEL] App-Bundle ---"
    rm -rf "$APP_DIR"
    mkdir -p "$APP_DIR/Contents/MacOS" "$APP_DIR/Contents/Resources"
    cp -R "$OUT"/. "$APP_DIR/Contents/MacOS/"
    rm -f "$APP_DIR/Contents/MacOS/"*.pdb
    chmod +x "$APP_DIR/Contents/MacOS/DiagnoseTool"

    cat > "$APP_DIR/Contents/Info.plist" << EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleDevelopmentRegion</key>
    <string>de</string>
    <key>CFBundleExecutable</key>
    <string>DiagnoseTool</string>
    <key>CFBundleIdentifier</key>
    <string>com.cpm.DiagnoseTool</string>
    <key>CFBundleInfoDictionaryVersion</key>
    <string>6.0</string>
    <key>CFBundleName</key>
    <string>DiagnoseTool</string>
    <key>CFBundleDisplayName</key>
    <string>DiagnoseTool</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleShortVersionString</key>
    <string>${APP_VERSION}</string>
    <key>CFBundleVersion</key>
    <string>${APP_VERSION}</string>
    <key>LSMinimumSystemVersion</key>
    <string>${MIN_OS}</string>
    <key>NSPrincipalClass</key>
    <string>NSApplication</string>
    <key>NSHighResolutionCapable</key>
    <true/>
</dict>
</plist>
EOF

    echo "--- [$LABEL] Ad-hoc-Signatur ---"
    xattr -cr "$APP_DIR" 2>/dev/null || true
    codesign --force --deep --sign - --timestamp=none "$APP_DIR"
    codesign --verify --deep --strict --verbose=2 "$APP_DIR"
    codesign -dv "$APP_DIR" 2>&1 | grep -E "Signature|Identifier|Format" || true

    echo "--- [$LABEL] DMG ---"
    STAGE="./build-macos/$LABEL/dmg"
    rm -rf "$STAGE" && mkdir -p "$STAGE"
    cp -R "$APP_DIR" "$STAGE/"
    ln -s /Applications "$STAGE/Programme"
    rm -f "$DMG"
    hdiutil create -volname "DiagnoseTool $APP_VERSION" -srcfolder "$STAGE" -ov -format UDZO "$DMG"
    echo "=== $DMG erstellt ==="
done
