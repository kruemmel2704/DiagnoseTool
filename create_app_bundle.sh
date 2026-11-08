#!/bin/bash
set -e

echo "=== Starting macOS App Bundle Creation ==="

# 1. Publish the project for both osx-arm64 and osx-x64
echo "Publishing project for osx-arm64..."
dotnet publish DiagnoseTool.csproj -c Release -r osx-arm64 -o ./publish/osx-arm64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true

echo "Publishing project for osx-x64..."
dotnet publish DiagnoseTool.csproj -c Release -r osx-x64 -o ./publish/osx-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true

# 2. Setup the App Bundle structure
APP_DIR="DiagnoseTool.app"
echo "Creating App Bundle directory at $APP_DIR..."
rm -rf "$APP_DIR"
mkdir -p "$APP_DIR"/Contents/MacOS
mkdir -p "$APP_DIR"/Contents/Resources

# 3. Copy resources and non-binary files from arm64 publish folder
echo "Copying baseline assets..."
cp -r ./publish/osx-arm64/* "$APP_DIR"/Contents/MacOS/

# 4. Merge native binaries into Universal binaries using lipo
echo "Merging binaries into Universal (arm64 + x64) using lipo..."
mkdir -p ./publish/universal

# Merge the main executable
lipo -create ./publish/osx-arm64/DiagnoseTool ./publish/osx-x64/DiagnoseTool -output ./publish/universal/DiagnoseTool
cp ./publish/universal/DiagnoseTool "$APP_DIR"/Contents/MacOS/

# Merge all architecture-specific dylibs
for arm_dylib in ./publish/osx-arm64/*.dylib; do
    filename=$(basename "$arm_dylib")
    x64_dylib="./publish/osx-x64/$filename"
    if [ -f "$x64_dylib" ]; then
        if cmp -s "$arm_dylib" "$x64_dylib"; then
            echo "$filename is identical in both, copying directly..."
            cp "$arm_dylib" "$APP_DIR"/Contents/MacOS/
        else
            echo "Attempting to merge $filename..."
            if lipo -create "$arm_dylib" "$x64_dylib" -output "./publish/universal/$filename" 2>/dev/null; then
                echo "Successfully created universal dylib for $filename"
                cp "./publish/universal/$filename" "$APP_DIR"/Contents/MacOS/
            else
                echo "Merging failed (likely because they already contain the same architectures). Copying version from arm64 publish."
                cp "$arm_dylib" "$APP_DIR"/Contents/MacOS/
            fi
        fi
    else
        echo "x64 version of $filename not found, copying arm64 version."
        cp "$arm_dylib" "$APP_DIR"/Contents/MacOS/
    fi
done

# 4. Create Info.plist
echo "Creating Info.plist..."
cat << 'EOF' > "$APP_DIR"/Contents/Info.plist
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
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleShortVersionString</key>
    <string>2.2.0</string>
    <key>CFBundleSignature</key>
    <string>????</string>
    <key>CFBundleVersion</key>
    <string>2.2.0</string>
    <key>LSMinimumSystemVersion</key>
    <string>10.12</string>
    <key>NSPrincipalClass</key>
    <string>NSApplication</string>
    <key>NSHighResolutionCapable</key>
    <true/>
</dict>
</plist>
EOF

# 5. Ensure execution rights
echo "Configuring permissions..."
chmod +x "$APP_DIR"/Contents/MacOS/DiagnoseTool

echo "=== App Bundle created successfully at $(pwd)/$APP_DIR ==="
