#!/usr/bin/env bash
#
# Build both plugins, package them as Jellyfin-installable zips, and generate
# the repository manifest.json that Jellyfin reads to offer them for install.
#
#   ./build.sh [base-url]
#
# base-url is the directory the zips are served from — it must already include
# any path. GitHub release assets are flat, so for CI this is
#   https://github.com/<o>/<r>/releases/download/<tag>
# while a raw-git base would end in /releases. Jellyfin
# downloads the zip from sourceUrl, so this must be an address the *server* can
# reach — a LAN Gitea URL for private use, or the GitHub mirror for sharing.
#
# Defaults to the GitHub mirror.

set -euo pipefail
cd "$(dirname "$0")"

BASE="${1:-https://raw.githubusercontent.com/lavavex/Jellyfin-Plugins/main}"
OUT="releases"
mkdir -p "$OUT"

# name|guid|version|category|description
# Version tracks Jellyfin's major: 12.0.0.0 is the first release for Jellyfin 12.
PLUGINS=(
"Suwayomi Metadata|6f1c9d24-3b7a-4f18-9d55-2e7a1c4b8e90|12.0.0.0|Books|Series metadata and cover art for manga libraries, read from a Suwayomi server."
"MangaBaka|8c3e5a17-42b9-4d6e-b1f0-9a7c5d2e4b83|12.0.0.0|Books|Series metadata and cover art for manga and light novel libraries, from MangaBaka."
)
changelog_for() {
  case "$1" in
    "Suwayomi Metadata")
      echo "12.0.0.0 for Jellyfin 12. Listed under Books and as a selectable book metadata provider. Settings page no longer overlays other dashboard pages. Server URL starts blank."
      ;;
    "MangaBaka")
      echo "12.0.0.0 for Jellyfin 12. Listed under Books and as a selectable book metadata provider. Settings page no longer overlays other dashboard pages. Reads titles, publication dates, and tags per the MangaBaka spec. Optional beta (v2) API."
      ;;
  esac
}

proj_dir() {
  case "$1" in
    "Suwayomi Metadata") echo "src/Jellyfin.Plugin.Suwayomi" ;;
    "MangaBaka")         echo "src/Jellyfin.Plugin.MangaBaka" ;;
  esac
}
asm_name() {
  case "$1" in
    "Suwayomi Metadata") echo "Jellyfin.Plugin.Suwayomi" ;;
    "MangaBaka")         echo "Jellyfin.Plugin.MangaBaka" ;;
  esac
}
slug() { echo "$1" | tr '[:upper:] ' '[:lower:]_'; }

# Catalogue artwork, published alongside the zips so imageUrl resolves from the
# same base. Jellyfin shows this in the plugin catalogue listing.
logo() {
  case "$1" in
    "Suwayomi Metadata") echo "suwayomi.png" ;;
    "MangaBaka")         echo "mangabaka.png" ;;
  esac
}

DOTNET="${DOTNET:-dotnet}"
command -v "$DOTNET" >/dev/null || DOTNET=/opt/homebrew/bin/dotnet

echo "base url: $BASE"
echo

ENTRIES=""
for spec in "${PLUGINS[@]}"; do
  IFS='|' read -r NAME GUID VER CAT DESC <<< "$spec"
  DIR=$(proj_dir "$NAME"); ASM=$(asm_name "$NAME"); SLUG=$(slug "$NAME")
  LOGO=$(logo "$NAME")
  CHANGELOG=$(changelog_for "$NAME")
  if [ ! -f "assets/$LOGO" ]; then
    echo "missing assets/$LOGO -- run assets/make_logos.py" >&2
    exit 1
  fi

  echo "building $NAME ..."
  "$DOTNET" build "$DIR" -c Release --nologo -v q >/dev/null

  ZIP="$OUT/${SLUG}_${VER}.zip"
  rm -f "$ZIP"
  STAGE=$(mktemp -d)
  cp "$DIR/bin/Release/net10.0/$ASM.dll" "$STAGE/"
  # Sideloaded zips have no catalogue metadata, so ship meta.json in the zip
  # or Jellyfin files the plugin under Other.
  cat > "$STAGE/meta.json" <<META
{
  "guid": "$GUID",
  "name": "$NAME",
  "description": "$DESC",
  "overview": "$DESC",
  "owner": "roberth",
  "category": "$CAT",
  "version": "$VER",
  "targetAbi": "12.0.0.0",
  "changelog": "$CHANGELOG",
  "timestamp": "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
}
META
  ( cd "$STAGE" && zip -q -r "$OLDPWD/$ZIP" . )
  rm -rf "$STAGE"

  # Jellyfin verifies the download against this MD5.
  if command -v md5sum >/dev/null; then SUM=$(md5sum "$ZIP" | cut -d' ' -f1)
  else SUM=$(md5 -q "$ZIP"); fi
  STAMP=$(date -u +%Y-%m-%dT%H:%M:%SZ)

  echo "  -> $ZIP  ($(wc -c < "$ZIP" | tr -d ' ') bytes, md5 $SUM)"

  [ -n "$ENTRIES" ] && ENTRIES="$ENTRIES,"
  ENTRIES="$ENTRIES
  {
    \"guid\": \"$GUID\",
    \"name\": \"$NAME\",
    \"description\": \"$DESC\",
    \"overview\": \"$DESC\",
    \"owner\": \"roberth\",
    \"category\": \"$CAT\",
    \"imageUrl\": \"$BASE/$LOGO\",
    \"versions\": [
      {
        \"version\": \"$VER\",
        \"changelog\": \"$CHANGELOG\",
        \"targetAbi\": \"12.0.0.0\",
        \"sourceUrl\": \"$BASE/$(basename "$ZIP")\",
        \"checksum\": \"$SUM\",
        \"timestamp\": \"$STAMP\"
      }
    ]
  }"
done

printf '[%s\n]\n' "$ENTRIES" > manifest.json
echo
echo "wrote manifest.json"
python3 -c "import json;d=json.load(open('manifest.json'));print(f'  valid JSON, {len(d)} plugins')" 2>/dev/null \
  || echo "  (install python3 to validate)"
