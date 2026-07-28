#!/usr/bin/env bash
# Publish a mod to the in-game catalog.
#
#   tools/add-mod.sh <dll> --id <pkg.id> --name "Display Name" --version 1.0.0 \
#                    [--summary "..."] [--scope client|host] [--author NAME] [--tags a,b]
#
# Computes the sha256, writes catalog/<id>/<version>/version.json, and inserts or updates the
# entry in catalog/catalog.json. Re-running with a new --version adds a version and points
# latestVersion at it, which is what makes UPDATE appear in everyone's browse tab.
#
# Nobody has to update the manager for this: the catalog is fetched live.

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DIST="$REPO_ROOT/dist"
CATALOG="$REPO_ROOT/catalog/catalog.json"

die() { printf '\033[31m%s\033[0m\n' "$1" >&2; exit 1; }
ok()  { printf '\033[32m    %s\033[0m\n' "$1"; }
step(){ printf '\033[36m==> %s\033[0m\n' "$1"; }

command -v python3 >/dev/null || die "python3 is required."
command -v sha256sum >/dev/null || die "sha256sum is required."

[ $# -ge 1 ] || die "usage: tools/add-mod.sh <dll> --id <pkg.id> --name \"Name\" --version 1.0.0 [...]"

DLL="$1"; shift
ID=""; NAME=""; VERSION=""; SUMMARY=""; SCOPE="client"; AUTHOR="ItsKarlin"; TAGS=""

while [ $# -gt 0 ]; do
    case "$1" in
        --id)      ID="$2"; shift 2 ;;
        --name)    NAME="$2"; shift 2 ;;
        --version) VERSION="$2"; shift 2 ;;
        --summary) SUMMARY="$2"; shift 2 ;;
        --scope)   SCOPE="$2"; shift 2 ;;
        --author)  AUTHOR="$2"; shift 2 ;;
        --tags)    TAGS="$2"; shift 2 ;;
        *) die "unknown option: $1" ;;
    esac
done

[ -f "$DLL" ]        || die "no such file: $DLL"
[ -n "$ID" ]         || die "--id is required (reverse-DNS, e.g. itskarlin.bapbap.my-mod)"
[ -n "$NAME" ]       || die "--name is required - it must match the mod's MelonInfo name, or the manager cannot tell it is installed"
[ -n "$VERSION" ]    || die "--version is required"
[ "$SCOPE" = "client" ] || [ "$SCOPE" = "host" ] || die "--scope must be client or host"

FILE="$(basename "$DLL")"

step "Staging $FILE into dist/"
mkdir -p "$DIST"
if [ "$(cd "$(dirname "$DLL")" && pwd)/$FILE" != "$DIST/$FILE" ]; then
    cp -f "$DLL" "$DIST/$FILE"
fi
SHA=$(sha256sum "$DIST/$FILE" | cut -d' ' -f1)
SIZE=$(stat -c%s "$DIST/$FILE")
ok "sha256 $SHA"
ok "$SIZE bytes"

step "Writing catalog/$ID/$VERSION/version.json"
VDIR="$REPO_ROOT/catalog/$ID/$VERSION"
mkdir -p "$VDIR"
python3 - "$VDIR/version.json" "$ID" "$VERSION" "$FILE" "$SHA" <<'PY'
import json,sys
path,pkg,ver,fname,sha = sys.argv[1:6]
json.dump({
    "schemaVersion": 1, "id": pkg, "version": ver,
    "files": [{
        "sourcePath": f"dist/{fname}",
        "targetPath": f"Mods/{fname}",
        "sha256": sha,
        "description": "Main mod dll",
    }],
}, open(path,"w"), indent=2)
open(path,"a").write("\n")
PY
ok "written"

step "Updating catalog/catalog.json"
python3 - "$CATALOG" "$ID" "$NAME" "$VERSION" "$SUMMARY" "$SCOPE" "$AUTHOR" "$TAGS" <<'PY'
import json,sys
path,pkg,name,ver,summary,scope,author,tags = sys.argv[1:9]
d = json.load(open(path))
entry = {
    "id": pkg, "name": name, "summary": summary, "author": author, "scope": scope,
    "latestVersion": ver,
    "tags": [t.strip() for t in tags.split(",") if t.strip()],
    "versionManifestPath": f"catalog/{pkg}/{ver}/version.json",
}
pkgs = d.setdefault("packages", [])
for i, existing in enumerate(pkgs):
    if existing.get("id") == pkg:
        # Keep anything the author set by hand that this script does not manage.
        merged = dict(existing); merged.update({k: v for k, v in entry.items() if v not in ("", [])})
        pkgs[i] = merged
        print(f"    updated existing entry -> {ver}")
        break
else:
    pkgs.append(entry)
    print(f"    added new entry")
json.dump(d, open(path,"w"), indent=2); open(path,"a").write("\n")
PY

step "Validating"
python3 -c "
import json,sys
cat=json.load(open('$CATALOG'))
vm=json.load(open('$VDIR/version.json'))
entry=next(p for p in cat['packages'] if p['id']=='$ID')
assert entry['versionManifestPath']=='catalog/$ID/$VERSION/version.json', 'manifest path mismatch'
assert vm['files'][0]['sha256']=='$SHA', 'hash mismatch'
assert vm['files'][0]['targetPath'].startswith('Mods/'), 'targetPath must be under Mods/'
print('    catalog.json and version.json agree')
"

echo
ok "Done. Commit and push:"
echo "    git add dist/$FILE catalog/ && git commit -m 'Publish $NAME $VERSION' && git push"
echo
echo "It appears in everyone's browse tab on their next catalog refresh - no client update needed."
