#!/usr/bin/env bash
# Packages the Thunderstore folder and submits it to thunderstore.io.
# Excludes *.zip files and the Images/ folder.
#
# Dry-run by default — builds the zip and prints what would submit, but does
# not upload. Pass --publish to actually submit to thunderstore.io.
#
# Auth: set THUNDERSTORE_TOKEN, pass --token, or create a .thunderstore-token
# file next to this script containing the bearer token.
#
# Usage examples:
#   ./publish-thunderstore.sh                       # dry run
#   ./publish-thunderstore.sh --publish             # actually submit
#   ./publish-thunderstore.sh --publish --token tss_xxx
#   ./publish-thunderstore.sh --categories mods,misc,client-side
#
# Requires: bash, python3 (or python/py), curl

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")" && pwd)"
THUNDERSTORE_DIR="$SCRIPT_DIR/Thunderstore"
TOKEN_FILE="$SCRIPT_DIR/.thunderstore-token"
TOKEN="${THUNDERSTORE_TOKEN:-}"
TEAM="virtualbjorn"
COMMUNITY="valheim"
CATEGORIES=("Mods" "Misc" "Server-side" "Client-side" "Bog Witch Update" "AI Generated")
NSFW="false"
KEEP_ZIP="false"
PUBLISH="false"

usage() {
    cat <<'EOF'
Usage: publish-thunderstore.sh [options]

  --publish                  Actually submit (default is dry-run)
  --token <token>            Thunderstore API bearer token
  --team <name>              Team author name (default: virtualbjorn)
  --community <slug>         Community slug (default: valheim)
  --categories a,b,c         Comma-separated category labels (auto-slugified)
  --nsfw                     Mark submission as NSFW
  --keep-zip                 Keep the generated zip after submission
  --thunderstore-dir <path>  Override Thunderstore folder location
  -h, --help                 Show this help
EOF
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --publish)          PUBLISH="true"; shift ;;
        --token)            TOKEN="$2"; shift 2 ;;
        --team)             TEAM="$2"; shift 2 ;;
        --community)        COMMUNITY="$2"; shift 2 ;;
        --categories)       IFS=',' read -r -a CATEGORIES <<< "$2"; shift 2 ;;
        --nsfw)             NSFW="true"; shift ;;
        --keep-zip)         KEEP_ZIP="true"; shift ;;
        --thunderstore-dir) THUNDERSTORE_DIR="$2"; shift 2 ;;
        -h|--help)          usage; exit 0 ;;
        *) echo "Unknown option: $1" >&2; usage; exit 1 ;;
    esac
done

need() {
    command -v "$1" >/dev/null 2>&1 || return 1
}

PYTHON=""
for cmd in python3 python py; do
    if need "$cmd"; then PYTHON="$cmd"; break; fi
done
if [[ -z "$PYTHON" ]]; then
    echo "Python not found (tried python3, python, py). Install from https://www.python.org/" >&2
    exit 1
fi
need curl || { echo "Required command not found: curl" >&2; exit 1; }

[[ -d "$THUNDERSTORE_DIR" ]] || { echo "Thunderstore folder not found: $THUNDERSTORE_DIR" >&2; exit 1; }

MANIFEST="$THUNDERSTORE_DIR/manifest.json"
[[ -f "$MANIFEST" ]] || { echo "manifest.json not found: $MANIFEST" >&2; exit 1; }

# Read name + version_number from manifest.json via Python
{ read -r MOD_NAME; read -r VERSION; } < <(MANIFEST="$MANIFEST" "$PYTHON" - <<'PY'
import json, os
with open(os.environ["MANIFEST"], encoding="utf-8") as f:
    d = json.load(f)
print(d.get("name", ""))
print(d.get("version_number", ""))
PY
)
if [[ -z "$MOD_NAME" || -z "$VERSION" ]]; then
    echo "manifest.json is missing name or version_number" >&2
    exit 1
fi

if [[ -z "$TOKEN" && -f "$TOKEN_FILE" ]]; then
    TOKEN="$(tr -d '[:space:]' < "$TOKEN_FILE")"
fi
if [[ "$PUBLISH" == "true" && -z "$TOKEN" ]]; then
    echo "No API token. Set THUNDERSTORE_TOKEN, pass --token, or create $TOKEN_FILE" >&2
    exit 1
fi

slugify() {
    printf '%s' "$1" | tr '[:upper:]' '[:lower:]' | sed -E 's/[^a-z0-9]+/-/g; s/^-+//; s/-+$//'
}

CATEGORY_SLUGS=()
for c in "${CATEGORIES[@]}"; do
    s="$(slugify "$c")"
    [[ -n "$s" ]] && CATEGORY_SLUGS+=("$s")
done

ZIP_NAME="${MOD_NAME}-${VERSION}.zip"
ZIP_PATH="$THUNDERSTORE_DIR/$ZIP_NAME"
rm -f "$ZIP_PATH"

echo "Packaging $MOD_NAME v$VERSION"
echo "  source : $THUNDERSTORE_DIR"
echo "  output : $ZIP_PATH"

SRC="$THUNDERSTORE_DIR" OUT="$ZIP_PATH" "$PYTHON" - <<'PY'
import os, zipfile
src = os.environ["SRC"]
out = os.environ["OUT"]
with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
    for root, dirs, files in os.walk(src):
        rel_root = os.path.relpath(root, src).replace("\\", "/")
        if rel_root == ".":
            rel_root = ""
        # Skip the Images/ directory entirely (don't descend into it)
        if rel_root == "Images" or rel_root.startswith("Images/"):
            dirs[:] = []
            continue
        for name in files:
            if name.lower().endswith(".zip"):
                continue
            full = os.path.join(root, name)
            arc  = (rel_root + "/" + name) if rel_root else name
            print(f"  + {arc}")
            z.write(full, arc)
PY

if ZIP_SIZE=$(stat -c%s "$ZIP_PATH" 2>/dev/null); then
    :
elif ZIP_SIZE=$(stat -f%z "$ZIP_PATH" 2>/dev/null); then
    :
else
    ZIP_SIZE=$(wc -c < "$ZIP_PATH" | tr -d ' ')
fi
printf 'Created %s (%s bytes)\n' "$ZIP_NAME" "$ZIP_SIZE"

if [[ "$PUBLISH" != "true" ]]; then
    echo
    echo "Dry run (no --publish) — would submit:"
    echo "  team       : $TEAM"
    echo "  community  : $COMMUNITY"
    echo "  categories : ${CATEGORY_SLUGS[*]}"
    echo "  nsfw       : $NSFW"
    echo
    echo "Re-run with --publish to upload."
    [[ "$KEEP_ZIP" != "true" ]] && rm -f "$ZIP_PATH"
    exit 0
fi

# State used by trap and helpers
INIT_RESP_FILE=""
URLS_FILE=""
PARTS_FILE=""
cleanup() {
    [[ -n "$INIT_RESP_FILE" && -f "$INIT_RESP_FILE" ]] && rm -f "$INIT_RESP_FILE"
    [[ -n "$URLS_FILE"      && -f "$URLS_FILE"      ]] && rm -f "$URLS_FILE"
    [[ -n "$PARTS_FILE"     && -f "$PARTS_FILE"     ]] && rm -f "$PARTS_FILE"
    [[ "$KEEP_ZIP" != "true" && -f "$ZIP_PATH" ]] && rm -f "$ZIP_PATH"
}
trap cleanup EXIT

BASE_URL="https://thunderstore.io"
AUTH_HEADER="Authorization: Bearer $TOKEN"

echo "Initiating upload..."
INIT_BODY="$(FN="$ZIP_NAME" SZ="$ZIP_SIZE" "$PYTHON" - <<'PY'
import json, os
print(json.dumps({
    "filename": os.environ["FN"],
    "file_size_bytes": int(os.environ["SZ"]),
}))
PY
)"

INIT_RESP_FILE="$(mktemp)"
curl -fsSL -X POST \
    -H "$AUTH_HEADER" \
    -H "Content-Type: application/json" \
    -d "$INIT_BODY" \
    -o "$INIT_RESP_FILE" \
    "$BASE_URL/api/experimental/usermedia/initiate-upload/"

URLS_FILE="$(mktemp)"
{ read -r MEDIA_UUID; read -r PART_COUNT; } < <(RESP="$INIT_RESP_FILE" OUT="$URLS_FILE" "$PYTHON" - <<'PY'
import json, os
with open(os.environ["RESP"], encoding="utf-8") as f:
    d = json.load(f)
urls = d["upload_urls"]
print(d["user_media"]["uuid"])
print(len(urls))
with open(os.environ["OUT"], "w", encoding="utf-8") as f:
    for u in urls:
        f.write(f'{u["part_number"]}\t{u["url"]}\n')
PY
)
echo "  media uuid : $MEDIA_UUID"
echo "  parts      : $PART_COUNT"

PARTS_FILE="$(mktemp)"

upload_one_part() {
    local part_file="$1" part_url="$2" part_num="$3" part_size="$4"
    echo "Uploading part $part_num/$PART_COUNT ($part_size bytes)..."
    local headers_file etag
    headers_file="$(mktemp)"
    curl -fsSL -X PUT -T "$part_file" \
        -H "Content-Type: application/octet-stream" \
        -D "$headers_file" -o /dev/null "$part_url"
    etag="$(grep -i '^etag:' "$headers_file" | head -n1 | sed -E 's/^[Ee][Tt][Aa][Gg]:[[:space:]]*//; s/\r$//')"
    rm -f "$headers_file"
    printf '%s\t%s\n' "$etag" "$part_num" >> "$PARTS_FILE"
}

if [[ "$PART_COUNT" -eq 1 ]]; then
    IFS=$'\t' read -r PART_NUMBER PART_URL < "$URLS_FILE"
    upload_one_part "$ZIP_PATH" "$PART_URL" "$PART_NUMBER" "$ZIP_SIZE"
else
    PART_SIZE=$(( (ZIP_SIZE + PART_COUNT - 1) / PART_COUNT ))
    while IFS=$'\t' read -r PART_NUMBER PART_URL; do
        OFFSET=$(( (PART_NUMBER - 1) * PART_SIZE ))
        REMAINING=$(( ZIP_SIZE - OFFSET ))
        THIS_SIZE=$(( REMAINING < PART_SIZE ? REMAINING : PART_SIZE ))
        TMP_PART="$(mktemp)"
        tail -c +$((OFFSET + 1)) "$ZIP_PATH" | head -c "$THIS_SIZE" > "$TMP_PART"
        upload_one_part "$TMP_PART" "$PART_URL" "$PART_NUMBER" "$THIS_SIZE"
        rm -f "$TMP_PART"
    done < "$URLS_FILE"
fi

echo "Finishing upload..."
FINISH_BODY="$(PARTS_FILE="$PARTS_FILE" "$PYTHON" - <<'PY'
import json, os
parts = []
with open(os.environ["PARTS_FILE"], encoding="utf-8") as f:
    for line in f:
        line = line.rstrip("\n")
        if not line:
            continue
        etag, pn = line.split("\t")
        parts.append({"ETag": etag, "PartNumber": int(pn)})
print(json.dumps({"parts": parts}))
PY
)"
curl -fsSL -X POST \
    -H "$AUTH_HEADER" \
    -H "Content-Type: application/json" \
    -d "$FINISH_BODY" \
    -o /dev/null \
    "$BASE_URL/api/experimental/usermedia/$MEDIA_UUID/finish-upload/"

echo "Submitting package..."
# Pass categories as newline-separated env var to avoid arg-quoting headaches
CATS_NL="$(printf '%s\n' "${CATEGORY_SLUGS[@]}")"
SUBMIT_BODY="$(
    AUTHOR="$TEAM" COMMUNITY="$COMMUNITY" CATS="$CATS_NL" NSFW="$NSFW" UUID="$MEDIA_UUID" \
    "$PYTHON" - <<'PY'
import json, os
cats = [c for c in os.environ["CATS"].splitlines() if c]
nsfw = os.environ["NSFW"].lower() == "true"
community = os.environ["COMMUNITY"]
print(json.dumps({
    "author_name": os.environ["AUTHOR"],
    "communities": [community],
    "community_categories": {community: cats},
    "has_nsfw_content": nsfw,
    "upload_uuid": os.environ["UUID"],
}))
PY
)"

SUBMIT_RESP_FILE="$(mktemp)"
curl -fsSL -X POST \
    -H "$AUTH_HEADER" \
    -H "Content-Type: application/json" \
    -d "$SUBMIT_BODY" \
    -o "$SUBMIT_RESP_FILE" \
    "$BASE_URL/api/experimental/submission/submit/"

echo
echo "Submitted successfully."
RESP="$SUBMIT_RESP_FILE" "$PYTHON" - <<'PY'
import json, os
with open(os.environ["RESP"], encoding="utf-8") as f:
    d = json.load(f)
pv = d.get("package_version") or {}
fn = pv.get("full_name")
web = pv.get("website_url")
dl  = pv.get("download_url")
if fn:  print(f"  package  : {fn}")
if web: print(f"  page     : {web}")
elif dl: print(f"  download : {dl}")
PY
rm -f "$SUBMIT_RESP_FILE"
