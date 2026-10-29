#!/usr/bin/env bash
# Wraps a macOS publish of the app into "MacosUtils.app", so it opens with a double click, has the
# app icon and shows its folder-access texts by name. Runs on macOS (plutil, iconutil, codesign, and
# rsvg-convert from "brew install librsvg").
#
#   bash MacosUtils.App/macOS/package-app.sh <publish folder> <version> <output folder>
#
# The bundle is signed ad hoc (no Apple Developer account): Apple Silicon needs a signature to run
# at all. It is not notarized, so a downloaded copy has to be allowed once (see the README).
set -euo pipefail

publish=$1
version=$2
out=$3
here=$(cd "$(dirname "$0")" && pwd)
logo=$(cd "$here/../../assets/logo/png" && pwd)
app="$out/MacosUtils.app"
executable="$app/Contents/MacOS/MacosUtils"

rm -rf "$app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp -R "$publish/." "$app/Contents/MacOS/"
test -f "$executable"

cp "$here/Info.plist" "$app/Contents/Info.plist"
plutil -replace CFBundleShortVersionString -string "$version" "$app/Contents/Info.plist"
plutil -replace CFBundleVersion -string "$version" "$app/Contents/Info.plist"
plutil -lint "$app/Contents/Info.plist"

# The icon: up to 256 px from the PNGs, the larger sizes from the SVG (rsvg-convert).
iconset=$(mktemp -d)/AppIcon.iconset
mkdir -p "$iconset"
cp "$logo/16.png" "$iconset/icon_16x16.png"
cp "$logo/32.png" "$iconset/icon_16x16@2x.png"
cp "$logo/32.png" "$iconset/icon_32x32.png"
cp "$logo/64.png" "$iconset/icon_32x32@2x.png"
cp "$logo/128.png" "$iconset/icon_128x128.png"
cp "$logo/256.png" "$iconset/icon_128x128@2x.png"
cp "$logo/256.png" "$iconset/icon_256x256.png"
rsvg-convert -w 512 -h 512 -o "$iconset/icon_256x256@2x.png" "$logo/../logo.svg"
cp "$iconset/icon_256x256@2x.png" "$iconset/icon_512x512.png"
rsvg-convert -w 1024 -h 1024 -o "$iconset/icon_512x512@2x.png" "$logo/../logo.svg"
iconutil -c icns "$iconset" -o "$app/Contents/Resources/AppIcon.icns"

# macOS treats every file in Contents/MacOS as code, the .NET assemblies too, so each one is signed
# (a non-Mach-O file keeps its signature in extended attributes, which ditto's zip preserves). The
# main executable is signed last, with the bundle.
find "$app/Contents/MacOS" -type f ! -path "$executable" -print0 |
    xargs -0 codesign --force --sign - --timestamp=none
codesign --force --sign - --timestamp=none "$app"
codesign --verify --strict --verbose=2 "$app"
