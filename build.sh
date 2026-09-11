#!/usr/bin/env bash
#
# Build both plugins, package them as Jellyfin-installable zips, and generate
# the repository manifest.json that Jellyfin reads to offer them for install.
#
#   ./build.sh [base-url]
#
# base-url is the directory this release's zips are served from — it must already
# include any path. GitHub release assets are flat, so for CI this is
#   https://github.com/<o>/<r>/releases/download/<tag>
# and it defaults to the "latest release" alias, which resolves to the same files
# once the tag is published. Jellyfin downloads the zip from sourceUrl, so this
# must be an address the *server* can reach.
#
# Every previously published version is carried into the manifest from
# versions.json, so upgrading never hides the older releases.

set -euo pipefail
cd "$(dirname "$0")"

REPO="${REPO:-lavavex/Jellyfin-Plugins}"
BASE="${1:-https://github.com/$REPO/releases/latest/download}"
OUT="releases"
mkdir -p "$OUT"

# name|guid|version|category|description
# Version tracks Jellyfin's major: 12.0.0.0 was the first release for Jellyfin 12.
PLUGINS=(
"Suwayomi Metadata|6f1c9d24-3b7a-4f18-9d55-2e7a1c4b8e90|12.0.0.1|Books|Series metadata and cover art for manga libraries, read from a Suwayomi server."
"MangaBaka|8c3e5a17-42b9-4d6e-b1f0-9a7c5d2e4b83|12.0.0.1|Books|Series metadata and cover art for manga and light novel libraries, from MangaBaka."
)

# Notes for the version being built. Earlier versions keep the notes recorded in
# versions.json — this only describes what is new in the build happening now.
changelog_for() {
  case "$1" in
    "Suwayomi Metadata")
      echo "The three providers now share one client, so a library refresh makes a single GraphQL query to Suwayomi instead of three. New setting limits the plugin to libraries whose content type is Books, so it no longer claims plain folders in photo or mixed libraries."
      ;;
    "MangaBaka")
      echo "Adds a local copy of MangaBaka's nightly database: download it from Dashboard > Scheduled Tasks and a whole-library refresh then matches offline, with no API requests at all. A second task refreshes every book library in one go. Fixes series being titled in Chinese or Japanese rather than English, the plugin searching MangaBaka for plain folders in every library on the server, and roughly 180 tags being written per series (now capped, 8 by default, spoilers dropped). Adds a title-language setting, matching for non-Latin titles, request throttling, and a link back to the MangaBaka page."
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

echo "repo:     $REPO"
echo "base url: $BASE"
echo

CURRENT="$OUT/.current.json"
: > "$CURRENT"

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

  echo "  -> $ZIP  ($(wc -c < "$ZIP" | tr -d ' ') bytes, md5 $SUM)"

  # One record per line; the manifest itself is assembled in python below, where
  # quoting a changelog into JSON is somebody else's problem.
  printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' \
    "$GUID" "$NAME" "$DESC" "$CAT" "$VER" "$(basename "$ZIP")" "$SUM" "$LOGO" "$CHANGELOG" >> "$CURRENT"
done

echo
BASE="$BASE" REPO="$REPO" CURRENT="$CURRENT" python3 - <<'PY'
import json, os, datetime

base = os.environ["BASE"].rstrip("/")
repo = os.environ["REPO"]
stamp = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
history = json.load(open("versions.json"))

manifest = []
for line in open(os.environ["CURRENT"], encoding="utf-8"):
    guid, name, desc, cat, ver, asset, checksum, logo, changelog = line.rstrip("\n").split("\t")

    versions = [{
        "version": ver,
        "changelog": changelog,
        "targetAbi": "12.0.0.0",
        "sourceUrl": f"{base}/{asset}",
        "checksum": checksum,
        "timestamp": stamp,
    }]

    # Older releases keep their own tag's asset URL and their recorded checksum;
    # they cannot be rebuilt byte-for-byte, and their zips are on that release.
    for old in reversed(history.get(guid, [])):
        if old["version"] == ver:
            continue
        versions.append({
            "version": old["version"],
            "changelog": old["changelog"],
            "targetAbi": old.get("targetAbi", "12.0.0.0"),
            "sourceUrl": f"https://github.com/{repo}/releases/download/{old['tag']}/{old['asset']}",
            "checksum": old["checksum"],
            "timestamp": old["timestamp"],
        })

    manifest.append({
        "guid": guid,
        "name": name,
        "description": desc,
        "overview": desc,
        "owner": "roberth",
        "category": cat,
        "imageUrl": f"{base}/{logo}",
        "versions": versions,
    })

with open("manifest.json", "w", encoding="utf-8") as f:
    json.dump(manifest, f, indent=2, ensure_ascii=False)
    f.write("\n")

print("wrote manifest.json")
for p in manifest:
    print(f"  {p['name']}: " + ", ".join(v["version"] for v in p["versions"]))
PY

rm -f "$CURRENT"
