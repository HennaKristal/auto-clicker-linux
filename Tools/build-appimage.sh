#!/usr/bin/env bash
set -euo pipefail

project_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
releases_directory="$project_directory/Releases"
application_id="org.hennakristal.AutoClicker"
packaging_tool_version="1.9.1"
packaging_tool_checksum="ed4ce84f0d9caff66f50bcca6ff6f35aae54ce8135408b3fa33abfc3cb384eb0"

if [[ $(uname -s) != Linux || $(uname -m) != x86_64 ]]; then
    printf 'Build this AppImage on an x86_64 Linux machine.\n' >&2
    exit 1
fi

for command in dotnet curl python3 sha256sum; do
    if ! command -v "$command" > /dev/null; then
        printf 'Required build tool is missing: %s\n' "$command" >&2
        exit 1
    fi
done

# Keep temporary packaging files inside Releases.
mkdir -p "$releases_directory"
build_directory=$(mktemp -d "$releases_directory/.build-appimage.XXXXXX")
trap 'rm -rf -- "$build_directory"' EXIT
application_directory="$build_directory/AutoClicker.AppDir"
publish_directory="$application_directory/usr/lib/AutoClicker"
packaging_tool="$build_directory/appimagetool.AppImage"
output_file="$releases_directory/AutoClicker-x86_64.AppImage"
mkdir -p "$publish_directory"

# Include .NET and the application's native publish libraries.
dotnet publish "$project_directory/AutoClicker.csproj" \
    --configuration Release \
    --runtime linux-x64 \
    --self-contained true \
    -p:PublishSingleFile=false \
    -p:PublishTrimmed=false \
    --output "$publish_directory"

cat > "$application_directory/AppRun" <<'LAUNCHER'
#!/bin/sh
application_directory=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
exec "$application_directory/usr/lib/AutoClicker/AutoClicker" "$@"
LAUNCHER
chmod +x "$application_directory/AppRun"

# AppImage requires an internal desktop entry and icon.
cat > "$application_directory/$application_id.desktop" <<DESKTOP
[Desktop Entry]
Type=Application
Name=AutoClicker
Comment=KDE Wayland auto clicker and macro recorder
Exec=AutoClicker
Icon=$application_id
Terminal=false
Categories=Utility;
DESKTOP

python3 - "$project_directory/Assets/Icons/icon.ico" "$application_directory/$application_id.png" <<'PYTHON'
import struct
import sys
from pathlib import Path

# Reuse the PNG already embedded in the existing application icon.
icon = Path(sys.argv[1]).read_bytes()
image_size, image_offset = struct.unpack_from("<II", icon, 14)
image = icon[image_offset:image_offset + image_size]
if not image.startswith(b"\x89PNG\r\n\x1a\n"):
    raise SystemExit("The application icon must contain a PNG image.")
Path(sys.argv[2]).write_bytes(image)
PYTHON
ln -s "$application_id.png" "$application_directory/.DirIcon"

# Download and verify the official packaging tool.
curl --fail --location --retry 3 --silent --show-error \
    "https://github.com/AppImage/appimagetool/releases/download/$packaging_tool_version/appimagetool-x86_64.AppImage" \
    --output "$packaging_tool"
printf '%s  %s\n' "$packaging_tool_checksum" "$packaging_tool" | sha256sum --check --status
chmod +x "$packaging_tool"

# Extraction lets the packaging tool run without a FUSE installation.
ARCH=x86_64 "$packaging_tool" --appimage-extract-and-run \
    "$application_directory" "$build_directory/AutoClicker-x86_64.AppImage"
chmod +x "$build_directory/AutoClicker-x86_64.AppImage"
mv -f -- "$build_directory/AutoClicker-x86_64.AppImage" "$output_file"
printf '\nAppImage created: %s\n' "$output_file"
